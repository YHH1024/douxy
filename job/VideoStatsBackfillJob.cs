using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.model.response;
using dy.net.service;
using dy.net.utils;
using Quartz;

namespace dy.net.job
{
    /// <summary>关注博主视频统计回填:每天05:30刷新「发布≤3天」的关注视频五项统计
    /// (更库+回写飞书原日期表原行)。幂等,无新增副作用。</summary>
    [DisallowConcurrentExecution]
    public class VideoStatsBackfillJob : IJob
    {
        private readonly DouyinCommonService commonService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly DouyinCookieService douyinCookieService;
        private readonly DouyinHttpClientService douyinHttpClientService;
        private readonly DouyinFollowService douyinFollowService;
        private readonly FeishuBitableService feishuBitableService;
        private readonly Random _random = new();

        public VideoStatsBackfillJob(DouyinCommonService commonService, DouyinVideoService douyinVideoService,
            DouyinCookieService douyinCookieService, DouyinHttpClientService douyinHttpClientService,
            DouyinFollowService douyinFollowService, FeishuBitableService feishuBitableService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.douyinCookieService = douyinCookieService;
            this.douyinHttpClientService = douyinHttpClientService;
            this.douyinFollowService = douyinFollowService;
            this.feishuBitableService = feishuBitableService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var config = commonService.GetConfig();
            if (config == null) return;

            var cutoff = DateTime.Now.AddDays(-3);
            var targets = (await douyinVideoService.GetAllAsync())
                .Where(v => v.ViedoType == VideoTypeEnum.dy_follows && v.CreateTime >= cutoff)
                .ToList();
            if (!targets.Any())
            {
                Serilog.Log.Debug("[stats-backfill] 无发布≤3天的关注视频,跳过");
                return;
            }

            // 回填只要登录态拉数据,不要求配置关注视频存储路径(那是「下载关注视频」的业务条件)
            var cookies = await douyinCookieService.GetOpendCookiesAsync(x => true);
            var cookie = cookies?.FirstOrDefault();
            if (cookie == null)
            {
                Serilog.Log.Debug("[stats-backfill] 无可用Cookie,跳过");
                return;
            }

            var changed = new List<DouyinVideo>();
            // 按博主分组拉主页最新数据
            foreach (var authorGroup in targets.GroupBy(v => v.AuthorId))
            {
                var followed = await douyinFollowService.GetByUperId(authorGroup.Key, cookie.MyUserId);
                if (followed == null || string.IsNullOrWhiteSpace(followed.SecUid))
                {
                    Serilog.Log.Debug("[stats-backfill] 博主 {Author} 不在关注表或无SecUid,跳过", authorGroup.Key);
                    continue;
                }

                try
                {
                    // 翻页拉取,覆盖到 3 天窗口外的视频为止(最多3页=60条兜底)
                    var latest = new List<Aweme>();
                    string cursor = "0";
                    for (int page = 0; page < 3; page++)
                    {
                        var data = await douyinHttpClientService.SyncUpderPostVideos("20", cursor, followed.SecUid, cookie.Cookies);
                        if (data?.AwemeList == null || !data.AwemeList.Any()) break;
                        latest.AddRange(data.AwemeList);
                        var oldest = data.AwemeList.Last();
                        // 列表按时间倒序,最旧一条已在窗口外即可停
                        if (DateTimeUtil.Convert10BitTimestamp(oldest.CreateTime) < cutoff) break;
                        if (data.HasMore != 1) break;
                        cursor = data.Cursor ?? (data.MaxCursor ?? "0");
                        await Task.Delay(_random.Next(2, 10) * 1000);
                    }

                    foreach (var video in authorGroup)
                    {
                        var item = latest.FirstOrDefault(a => a.AwemeId == video.AwemeId);
                        if (item?.Statistics == null) continue; // 博主已删或拉不到
                        var p = item.Statistics.PlayCount ?? 0;
                        var d = item.Statistics.DiggCount ?? 0;
                        var c = item.Statistics.CommentCount ?? 0;
                        var s = item.Statistics.ShareCount ?? 0;
                        var col = item.Statistics.CollectCount ?? 0;
                        if (video.PlayCount == p && video.DiggCount == d && video.CommentCount == c
                            && video.ShareCount == s && video.CollectCount == col)
                            continue; // 无变化
                        video.PlayCount = p; video.DiggCount = d; video.CommentCount = c;
                        video.ShareCount = s; video.CollectCount = col;
                        // B2:只更新统计5列——本Job从05:30加载实体到收尾可>1h,整实体更新会把
                        // 期间字幕队列写入的字幕列stale覆盖回null,触发无谓重转
                        await douyinVideoService.UpdateStatsFieldsAsync(video);
                        changed.Add(video);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[stats-backfill] 博主 {SecUid} 拉取失败,跳过", followed.UperName);
                }
                await Task.Delay(_random.Next(5, 15) * 1000);
            }

            Serilog.Log.Information("[stats-backfill] 库刷新完成 changed={Changed}/{Total}", changed.Count, targets.Count);

            // 飞书回写(未配置时 UpdateStatsAsync 内部返回0)
            if (changed.Any())
            {
                try
                {
                    var rows = await feishuBitableService.UpdateStatsAsync(config, changed);
                    Serilog.Log.Information("[stats-backfill] 飞书回写 {Rows} 行", rows);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[stats-backfill] 飞书回写失败(库已更新,不影响)");
                }
            }
        }
    }
}
