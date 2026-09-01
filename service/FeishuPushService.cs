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
        private readonly LocalAsrSubtitleService asrSubtitleService;

        // 手动推送与23:50定时任务互斥闸门
        private static readonly SemaphoreSlim _pushGate = new(1, 1);

        public FeishuPushService(DouyinCommonService commonService, DouyinVideoService douyinVideoService,
            FeishuBitableService bitableService, FeishuNotifyService notifyService,
            LocalAsrSubtitleService asrSubtitleService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.bitableService = bitableService;
            this.notifyService = notifyService;
            this.asrSubtitleService = asrSubtitleService;
        }

        public async Task<FeishuPushResult> RunDailyPushAsync(bool waitForSubtitles = false)
        {
            var config = commonService.GetConfig();
            if (config == null || !config.FeishuPushEnabled)
                return new FeishuPushResult { Success = false, Message = "飞书推送未开启" };
            if (string.IsNullOrWhiteSpace(config.FeishuAppId) || string.IsNullOrWhiteSpace(config.FeishuAppSecret))
                return new FeishuPushResult { Success = false, Message = "飞书AppId/AppSecret未配置" };
            // 2026-08-24 方案B:推送强制用户身份——未授权直接失败并提示,不再静默回落应用空间
            // (避免表格建到机器人空间造成困惑;回落代码保留但推送路径不再触达)
            if (!bitableService.HasUserAuth(config))
                return new FeishuPushResult { Success = false, Message = "飞书账号未授权——请到设置页点击「授权飞书账号」后重试" };

            if (!await _pushGate.WaitAsync(0))
                return new FeishuPushResult { Success = false, Message = "已有推送任务进行中,稍后再试" };
            try
            {
                var pushDate = DateTime.Today; // 跨午夜保护:等待最长到04:50,期间Today会变新一天——筛选/表名/Base月份/通知日期全用入口快照
                // 字幕等待(仅定时任务):今日存在字幕在转/待转的视频时推迟推送,直到全部终态或超保险丝。
                // 失败(StatusMsg有值)是终态不阻塞;手动推送 waitForSubtitles=false 跳过整段。
                // B3:未开自动字幕时队列不会转写任何视频,三空记录永远非终态——跳过等待,否则每晚硬等满5h保险丝
                if (waitForSubtitles && config.AutoGenSubtitle)
                {
                    var deadline = DateTime.Now.AddHours(5); // 保险丝:最多等5小时(23:50→04:50),防ASR彻底故障时当天永不推送
                    int consecutiveAsrFail = 0;
                    bool asrAlarmSent = false;
                    while (DateTime.Now < deadline)
                    {
                        var pendingCheck = (await douyinVideoService.GetAllAsync())
                            .Where(v => v.SyncTime >= pushDate);
                        if (!pendingCheck.Any(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                                                 && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)))
                            break; // 全部终态,正常推送

                        var asrHealth = await asrSubtitleService.CheckHealthAsync(config);
                        if (asrHealth.Success)
                        {
                            consecutiveAsrFail = 0;
                        }
                        else
                        {
                            consecutiveAsrFail++;
                            if (consecutiveAsrFail >= 2 && !asrAlarmSent)
                            {
                                asrAlarmSent = true; // 一晚只告警一次
                                Log.Warning("[feishu] ASR不可用,推送等待中: {Msg}", asrHealth.Message);
                                await notifyService.SendAsync(config,
                                    $"【抖小云】ASR 服务不可用({asrHealth.Message}),今日飞书推送暂停等待中,请检查 ASR 服务");
                            }
                        }
                        Log.Information("[feishu] 今日仍有字幕未就绪,10分钟后重查(截止 {Deadline:HH:mm})", deadline);
                        await Task.Delay(TimeSpan.FromMinutes(10));
                    }
                }

            FeishuPushResult result;
            try
            {
                var lanBase = config.LanBaseUrl; // 内网播放链接基数(空=不写内网列)
                var all = await douyinVideoService.GetAllAsync();
                var today = all.Where(v => v.SyncTime >= pushDate).OrderBy(v => v.SyncTime).ToList();
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
                        PlayUrl = string.IsNullOrWhiteSpace(v.AwemeId) ? string.Empty : $"https://www.douyin.com/video/{v.AwemeId}",
                        LanPlayUrl = string.IsNullOrWhiteSpace(lanBase) || string.IsNullOrWhiteSpace(v.VideoSavePath)
                            ? string.Empty
                            : $"{lanBase.TrimEnd('/')}/api/video/play/{v.Id}",
                        DouyinNo = v.AuthorDouyinNo ?? string.Empty,
                        SecUid = v.AuthorSecUid ?? string.Empty,
                        VideoId = v.AwemeId ?? string.Empty,
                    });
                }
                result = await bitableService.PushDailyAsync(config, rows, pushDate);
                Log.Information("[feishu] 推送完成 {Count} 条", result.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[feishu] 推送失败");
                result = new FeishuPushResult { Success = false, Message = ex.Message };
            }

            var stamp = $"{pushDate.Month}月{pushDate.Day}日"; // 用入口快照:跨午夜推送(04:50)时数据是昨天的,Now会把通知日期标错一天
            var text = result.Success
                ? $"{stamp}抖小云同步数据已推送 {result.Count} 条 → {result.BaseUrl}"
                : $"{stamp}抖小云推送失败:{result.Message}";
            await notifyService.SendAsync(config, text);

            try
            {
                config.FeishuLastPushResult = $"{DateTime.Now:yyyy-MM-dd HH:mm} " +
                    (result.Success ? $"成功 {result.Count}条" : $"失败 {result.Message}");
                // 列级更新:config是入口快照,等待字幕最长5h,整实体落库会把期间用户在设置页的改动静默回滚
                await commonService.UpdateConfigColumnsAsync(config, nameof(config.FeishuLastPushResult));
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
