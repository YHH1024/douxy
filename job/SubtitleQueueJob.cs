using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.service;
using dy.net.utils;
using Quartz;

namespace dy.net.job
{
    /// <summary>字幕队列消费:每2分钟扫库——未提交的上传ASR异步队列,已提交的轮询写回。
    /// 404重报≤3次;手动先完成的让位;48h窗口+单轮100条。</summary>
    [DisallowConcurrentExecution]
    public class SubtitleQueueJob : IJob
    {
        private readonly DouyinCommonService commonService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly LocalAsrSubtitleService asrService;

        public SubtitleQueueJob(DouyinCommonService commonService,
            DouyinVideoService douyinVideoService, LocalAsrSubtitleService asrService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.asrService = asrService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var config = commonService.GetConfig();
            if (config == null || !config.AutoGenSubtitle) return;

            // ASR 健康检查(不通则走现有拉起流程;仍不通下轮再试)
            var health = await asrService.CheckHealthAsync(config);
            if (!health.Success)
            {
                // EnsureAsrRunningAsync 为 private:拉起由 dysync 侧现有入口触发,这里只探,下轮再试
                Serilog.Log.Debug("[subtitle-queue] ASR未就绪: {Msg}", health.Message);
                return;
            }

            var all = await douyinVideoService.GetAllAsync();

            // ---- ① 轮询已提交的 ----
            var submitted = all.Where(v => v.AsrTaskId.HasValue
                && string.IsNullOrWhiteSpace(v.SubtitleSavePath)).ToList();
            foreach (var v in submitted)
            {
                var (status, text, segs, err) = await asrService.QueueStatusAsync(config, v.AsrTaskId.Value);
                switch (status)
                {
                    case 0:
                    case 1:
                        if (v.AsrTaskStatus != status) { v.AsrTaskStatus = status; await douyinVideoService.UpdateOne(v); }
                        break;
                    case 2: // 成功:写回(手动先完成则让位)
                        if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath)) { v.AsrTaskId = null; v.AsrTaskStatus = null; await douyinVideoService.UpdateOne(v); break; }
                        var srt = LocalAsrSubtitleService.BuildSrtContentFrom(text, segs);
                        var srtPath = Path.ChangeExtension(v.VideoSavePath, ".srt");
                        var txtPath = Path.ChangeExtension(v.VideoSavePath, ".txt");
                        try
                        {
                            await File.WriteAllTextAsync(srtPath, srt, System.Text.Encoding.UTF8);
                            if (!string.IsNullOrWhiteSpace(text)) await File.WriteAllTextAsync(txtPath, text, System.Text.Encoding.UTF8);
                            v.SubtitleSavePath = srtPath;
                            v.SubtitleStatusMsg = "Subtitle generated via ASR queue.";
                            v.SubtitleCreateTime = DateTime.Now;
                            v.AsrTaskId = null; v.AsrTaskStatus = null; v.AsrRetryCount = 0;
                            await douyinVideoService.UpdateOne(v);
                        }
                        catch (Exception ex)
                        {
                            v.SubtitleStatusMsg = $"写回失败: {ex.Message}";
                            await douyinVideoService.UpdateOne(v);
                        }
                        break;
                    case 3:
                        v.SubtitleStatusMsg = $"ASR: {err}";
                        v.AsrTaskId = null; v.AsrTaskStatus = null;
                        await douyinVideoService.UpdateOne(v);
                        break;
                    case -1: // 404 任务丢失
                        v.AsrRetryCount++;
                        if (v.AsrRetryCount >= 3)
                        {
                            v.SubtitleStatusMsg = "ASR任务丢失,重试超限";
                            v.AsrTaskId = null; v.AsrTaskStatus = null;
                        }
                        else { v.AsrTaskId = null; v.AsrTaskStatus = null; } // 下轮重新提交
                        await douyinVideoService.UpdateOne(v);
                        break;
                    default: // -2 查询异常:不动,下轮再查
                        break;
                }
            }

            // ---- ② 提交新的(48h窗口,限100) ----
            var cutoff = DateTime.Now.AddHours(-48);
            var toSubmit = all.Where(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)
                && !v.AsrTaskId.HasValue
                && v.SyncTime >= cutoff
                && !string.IsNullOrWhiteSpace(v.VideoSavePath))
                .Take(100).ToList();
            foreach (var v in toSubmit)
            {
                if (!File.Exists(v.VideoSavePath))
                {
                    v.SubtitleStatusMsg = "Video file not found.";
                    await douyinVideoService.UpdateOne(v);
                    continue;
                }
                var (ok, taskId, dedup, err) = await asrService.QueueSubmitAsync(
                    config, v.VideoSavePath, v.VideoTitle, $"dysync-{v.Id}");
                if (ok)
                {
                    v.AsrTaskId = taskId;
                    v.AsrTaskStatus = 0;
                    await douyinVideoService.UpdateOne(v);
                }
                else
                {
                    Serilog.Log.Debug("[subtitle-queue] 提交失败 {Id}: {Err}", v.Id, err);
                    break; // ASR不可用,本轮放弃剩余
                }
            }
        }
    }
}
