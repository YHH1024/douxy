using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.utils;
using Serilog;

namespace dy.net.service
{
    /// <summary>飞书推送编排:读当天同步记录(13列含字幕) → 写多维表格 → 群通知 → 结果落库。
    /// Quartz 任务与手动触发(FeishuController)共用本方法。</summary>
    public class FeishuPushService
    {
        private readonly DouyinCommonService commonService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly FeishuBitableService bitableService;
        private readonly FeishuNotifyService notifyService;

        // 手动推送与23:50定时任务互斥闸门
        private static readonly SemaphoreSlim _pushGate = new(1, 1);

        public FeishuPushService(DouyinCommonService commonService, DouyinVideoService douyinVideoService,
            FeishuBitableService bitableService, FeishuNotifyService notifyService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.bitableService = bitableService;
            this.notifyService = notifyService;
        }

        public async Task<FeishuPushResult> RunDailyPushAsync()
        {
            var config = commonService.GetConfig();
            if (config == null || !config.FeishuPushEnabled)
                return new FeishuPushResult { Success = false, Message = "飞书推送未开启" };
            if (string.IsNullOrWhiteSpace(config.FeishuAppId) || string.IsNullOrWhiteSpace(config.FeishuAppSecret))
                return new FeishuPushResult { Success = false, Message = "飞书AppId/AppSecret未配置" };

            if (!await _pushGate.WaitAsync(0))
                return new FeishuPushResult { Success = false, Message = "已有推送任务进行中,稍后再试" };
            try
            {
            FeishuPushResult result;
            try
            {
                var all = await douyinVideoService.GetAllAsync();
                var today = all.Where(v => v.SyncTime >= DateTime.Today).OrderBy(v => v.SyncTime).ToList();
                var rows = new List<FeishuVideoRow>();
                foreach (var v in today)
                {
                    string subtitle = string.Empty;
                    if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath))
                    {
                        subtitle = await SubtitleTextReader.ReadAsync(Path.GetFullPath(v.SubtitleSavePath));
                    }
                    rows.Add(new FeishuVideoRow
                    {
                        SyncTimeMs = new DateTimeOffset(v.SyncTime).ToUnixTimeMilliseconds(),
                        CreateTimeMs = v.CreateTime != default ? new DateTimeOffset(v.CreateTime).ToUnixTimeMilliseconds() : (long?)null,
                        SyncType = v.ViedoType.GetDesc(),
                        Author = v.Author ?? string.Empty,
                        VideoKind = $"{v.Tag1 ?? string.Empty} {v.Tag2 ?? string.Empty} {v.Tag3 ?? string.Empty}".Trim(),
                        Title = v.VideoTitle ?? string.Empty,
                        DyUser = v.DyUser ?? string.Empty,
                        PlayCount = v.PlayCount ?? 0,
                        DiggCount = v.DiggCount ?? 0,
                        CommentCount = v.CommentCount ?? 0,
                        ShareCount = v.ShareCount ?? 0,
                        CollectCount = v.CollectCount ?? 0,
                        Subtitle = subtitle,
                    });
                }
                result = await bitableService.PushDailyAsync(config, rows);
                Log.Information("[feishu] 推送完成 {Count} 条", result.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[feishu] 推送失败");
                result = new FeishuPushResult { Success = false, Message = ex.Message };
            }

            var stamp = $"{DateTime.Now.Month}月{DateTime.Now.Day}日";
            var text = result.Success
                ? $"{stamp}抖小云同步数据已推送 {result.Count} 条 → {result.BaseUrl}"
                : $"{stamp}抖小云推送失败:{result.Message}";
            await notifyService.SendAsync(config, text);

            try
            {
                config.FeishuLastPushResult = $"{DateTime.Now:yyyy-MM-dd HH:mm} " +
                    (result.Success ? $"成功 {result.Count}条" : $"失败 {result.Message}");
                await commonService.UpdateConfig(config);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[feishu] 推送结果落库失败(不影响推送本身)");
            }
            return result;
            }
            finally { _pushGate.Release(); }
        }
    }
}
