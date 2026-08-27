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
            // 外层兜底:DB等异常只记日志,不中断Job调度
            try
            {
                await ExecuteInner(context);
            }
            catch (Exception ex)
            {
                Serilog.Log.Error(ex, "[subtitle-queue] 轮询异常,本轮放弃,下轮重试");
            }
        }

        private async Task ExecuteInner(IJobExecutionContext context)
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
                        if (v.AsrTaskStatus != status) { v.AsrTaskStatus = status; await douyinVideoService.UpdateSubtitleFieldsAsync(v); }
                        break;
                    case 2: // 成功:写回(手动先完成则让位)
                        if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath)) { v.AsrTaskId = null; v.AsrTaskStatus = null; await douyinVideoService.UpdateSubtitleFieldsAsync(v); break; }
                        var gate = LocalAsrSubtitleService.TryAcquireVideoGate(v.Id);
                        if (gate == null) break; // 手动路径正在处理,本轮跳过
                        try
                        {
                            var srt = LocalAsrSubtitleService.BuildSrtContentFrom(text, segs);
                            if (string.IsNullOrWhiteSpace(srt))
                            {
                                // M1:空文本(纯音乐/VAD全滤)不落空文件,标终态可手动重试
                                v.SubtitleStatusMsg = "ASR returned empty content.";
                                v.AsrRetryCount = 0; // 清空历史计数,避免残留
                                v.AsrTaskId = null; v.AsrTaskStatus = null;
                                await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                                break;
                            }
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
                                await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                            }
                            catch (Exception ex)
                            {
                                Serilog.Log.Debug(ex, "[subtitle-queue] 写回失败 {Id}", v.Id);
                                // M2:写回失败计数,超限终态,不再无限重转
                                v.AsrRetryCount += 1;
                                v.AsrTaskId = null; v.AsrTaskStatus = null;
                                if (v.AsrRetryCount >= 3) v.SubtitleStatusMsg = "字幕写回失败(重试超限)";
                                await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                            }
                        }
                        finally { LocalAsrSubtitleService.ReleaseVideoGate(v.Id, gate); }
                        break;
                    case 3: // ASR侧失败:瞬时故障(显存临时不足等)有限重试,超限才终态
                        v.AsrRetryCount += 1;
                        v.AsrTaskId = null; v.AsrTaskStatus = null;
                        if (v.AsrRetryCount >= 3)
                            v.SubtitleStatusMsg = $"ASR: {err}(重试超限)";
                        await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                        break;
                    case -1: // 404 任务丢失
                        v.AsrRetryCount++;
                        if (v.AsrRetryCount >= 3)
                        {
                            v.SubtitleStatusMsg = "ASR任务丢失,重试超限";
                            v.AsrTaskId = null; v.AsrTaskStatus = null;
                        }
                        else { v.AsrTaskId = null; v.AsrTaskStatus = null; } // 下轮重新提交
                        await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                        break;
                    default: // -2 查询异常:不动,下轮再查
                        break;
                }
            }

            // ---- ② 提交新的(可配回扫窗口 AsrBackfillHours,默认48h;调大可转历史视频。限100/轮) ----
            var backfillHours = config.AsrBackfillHours <= 0 ? 24 : config.AsrBackfillHours; // 0视为只转当天(24h)
            var cutoff = DateTime.Now.AddHours(-backfillHours);
            // M3:无文件路径的当日记录标终态——否则飞书字幕等待永远等不到它,每晚硬等满5h保险丝。
            // B4:窗口含昨天——最后一轮标记跑在22:00,22:00后~午夜前入库的无文件记录次日已不是"今日",
            // 若不含昨天会永远漏标(如23:58同步的图文),当晚推送为其硬等5h
            var markSince = DateTime.Today.AddDays(-1);
            var noFile = all.Where(v => v.SyncTime >= markSince
                && string.IsNullOrWhiteSpace(v.VideoSavePath)
                && string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)).ToList();
            foreach (var v in noFile)
            {
                v.SubtitleStatusMsg = "no video file";
                await douyinVideoService.UpdateSubtitleFieldsAsync(v);
            }
            var toSubmit = all.Where(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                && (string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)
                    || ((v.SubtitleStatusMsg == "Video file not found." || v.SubtitleStatusMsg == "no video file") && !string.IsNullOrWhiteSpace(v.VideoSavePath) && File.Exists(v.VideoSavePath)))
                && !v.AsrTaskId.HasValue
                && v.SyncTime >= cutoff
                && !string.IsNullOrWhiteSpace(v.VideoSavePath))
                .Take(100).ToList();
            foreach (var v in toSubmit)
            {
                if (!File.Exists(v.VideoSavePath))
                {
                    v.SubtitleStatusMsg = "Video file not found.";
                    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                    continue;
                }
                if (v.SubtitleStatusMsg == "Video file not found." || v.SubtitleStatusMsg == "no video file")
                {
                    v.SubtitleStatusMsg = null; // 文件已恢复,清失败标记重新入队(字幕补漏)
                    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
                }
                var (ok, taskId, dedup, err) = await asrService.QueueSubmitAsync(
                    config, v.VideoSavePath, v.VideoTitle, $"dysync-{v.Id}");
                if (ok)
                {
                    v.AsrTaskId = taskId;
                    v.AsrTaskStatus = 0;
                    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
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
