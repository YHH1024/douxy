using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.service;
using dy.net.utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace dy.net.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FollowController : ControllerBase
    {
        private readonly DouyinFollowService _douyinFollowService;
        private readonly DouyinQuartzJobService _douyinQuartzJobService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly DouyinCommonService commonService;

        public FollowController(DouyinFollowService douyinFollowService, DouyinQuartzJobService douyinQuartzJobService,
            DouyinVideoService douyinVideoService, DouyinCommonService commonService)
        {
            this._douyinFollowService = douyinFollowService;
            _douyinQuartzJobService = douyinQuartzJobService;
            this.douyinVideoService = douyinVideoService;
            this.commonService = commonService;
        }

        /// <summary>博主ID清单(免登录,内网机器直接拉):返回JSON数组,含 UperName/UperId/SecUid/DouyinNo/LastSyncTime。
        /// 用途:NAS 部署后供内网其他系统取博主账号ID与sec_uid。</summary>
        [HttpGet("open/uperids")]
        [AllowAnonymous]
        public async Task<IActionResult> GetUperIds()
        {
            var list = await _douyinFollowService.GetPagedAllAsync();
            return Ok(list.Select(f => new
            {
                uperName = f.UperName,
                uperId = f.UperId,
                douyinNo = f.DouyinNo,
                secUid = f.SecUid,
                openSync = f.OpenSync,
                lastSyncTime = f.LastSyncTime == default ? null : f.LastSyncTime.ToString("yyyy-MM-dd HH:mm:ss")
            }).ToList());
        }

        /// <summary>视频清单(免登录,内网机器直接拉):每条一个视频,博主身份字段在前(uperName/douyinNo/secUid/uperId),
        /// 视频字段在后(title 后跟 videoId)。支持多维度筛选(全部可选,组合使用):
        /// uperId=博主ID精确 | uperName=博主名模糊 | syncStart/syncEnd=同步时间区间 | createStart/createEnd=发布时间区间
        /// minPlay/maxPlay minDigg/maxDigg minComment/maxComment minShare/maxShare minCollect/maxCollect=五项统计区间
        /// viedoType=来源(1喜欢2收藏3关注6合集7短剧5自定义收藏,数字) | keyword=标题关键词模糊
        /// 分页(2026-09-02起):默认每页100条、按同步时间从近到远;翻页传pageIndex;全量传pageSize=0(慎用,响应大);
        ///   pageSize上限500 | orderBy=syncTime(默认)|createTime|digg|play|collect 均倒序
        /// withSubtitle=true 才返回字幕全文(每条要读盘,全量模式下尤其慢,默认不带)
        /// secUid 双路兜底:视频自身列 → dy_follow 关联 → null。</summary>
        [HttpGet("open/videos")]
        [AllowAnonymous]
        public async Task<IActionResult> GetOpenVideos(
            [FromQuery] string uperId, [FromQuery] string uperName, [FromQuery] string keyword,
            [FromQuery] string syncStart, [FromQuery] string syncEnd,
            [FromQuery] string createStart, [FromQuery] string createEnd,
            [FromQuery] long? minPlay, [FromQuery] long? maxPlay,
            [FromQuery] long? minDigg, [FromQuery] long? maxDigg,
            [FromQuery] long? minComment, [FromQuery] long? maxComment,
            [FromQuery] long? minShare, [FromQuery] long? maxShare,
            [FromQuery] long? minCollect, [FromQuery] long? maxCollect,
            [FromQuery] int? viedoType,
            [FromQuery] bool withSubtitle = false,
            [FromQuery] int pageIndex = 1, [FromQuery] int pageSize = 100,
            [FromQuery] string orderBy = "syncTime")
        {
            var videos = await douyinVideoService.GetAllAsync();
            // 内网播放链接基数:优先config的LanBaseUrl,未配置回落当前请求Host(对方系统从哪访问就用哪个地址)
            var config = commonService.GetConfig();
            var lanBase = !string.IsNullOrWhiteSpace(config?.LanBaseUrl)
                ? config.LanBaseUrl
                : $"{Request.Scheme}://{Request.Host}";
            // 博主身份映射:UperId -> (douyinNo, secUid, UperName)
            var follows = await _douyinFollowService.GetPagedAllAsync();
            var uperMap = follows.GroupBy(f => f.UperId).ToDictionary(g => g.Key, g => g.First());
            // 博主名筛选:先解出命中的UperId集合(与uperId参数叠加)
            HashSet<string> nameMatchIds = null;
            if (!string.IsNullOrWhiteSpace(uperName))
                nameMatchIds = follows.Where(f => !string.IsNullOrEmpty(f.UperName) && f.UperName.Contains(uperName))
                    .Select(f => f.UperId).ToHashSet();

            DateTime? TryParse(string s) => string.IsNullOrWhiteSpace(s) ? null : (DateTime.TryParse(s, out var d) ? d : (DateTime?)null);

            IEnumerable<DouyinVideo> query = videos;
            if (!string.IsNullOrWhiteSpace(uperId))
                query = query.Where(v => v.AuthorId == uperId);
            if (nameMatchIds != null)
                query = query.Where(v => v.AuthorId != null && nameMatchIds.Contains(v.AuthorId));
            if (!string.IsNullOrWhiteSpace(keyword))
                query = query.Where(v => !string.IsNullOrEmpty(v.VideoTitle) && v.VideoTitle.Contains(keyword));
            var sStart = TryParse(syncStart); var sEnd = TryParse(syncEnd);
            if (sStart.HasValue) query = query.Where(v => v.SyncTime >= sStart.Value);
            if (sEnd.HasValue) query = query.Where(v => v.SyncTime <= sEnd.Value);
            var cStart = TryParse(createStart); var cEnd = TryParse(createEnd);
            if (cStart.HasValue) query = query.Where(v => v.CreateTime >= cStart.Value);
            if (cEnd.HasValue) query = query.Where(v => v.CreateTime <= cEnd.Value);
            if (minPlay.HasValue) query = query.Where(v => (v.PlayCount ?? 0) >= minPlay.Value);
            if (maxPlay.HasValue) query = query.Where(v => (v.PlayCount ?? 0) <= maxPlay.Value);
            if (minDigg.HasValue) query = query.Where(v => (v.DiggCount ?? 0) >= minDigg.Value);
            if (maxDigg.HasValue) query = query.Where(v => (v.DiggCount ?? 0) <= maxDigg.Value);
            if (minComment.HasValue) query = query.Where(v => (v.CommentCount ?? 0) >= minComment.Value);
            if (maxComment.HasValue) query = query.Where(v => (v.CommentCount ?? 0) <= maxComment.Value);
            if (minShare.HasValue) query = query.Where(v => (v.ShareCount ?? 0) >= minShare.Value);
            if (maxShare.HasValue) query = query.Where(v => (v.ShareCount ?? 0) <= maxShare.Value);
            if (minCollect.HasValue) query = query.Where(v => (v.CollectCount ?? 0) >= minCollect.Value);
            if (maxCollect.HasValue) query = query.Where(v => (v.CollectCount ?? 0) <= maxCollect.Value);
            if (viedoType.HasValue && Enum.IsDefined(typeof(VideoTypeEnum), viedoType.Value))
                query = query.Where(v => (int)v.ViedoType == viedoType.Value);

            // 排序(白名单字段,默认同步时间倒序)
            query = (orderBy ?? "").ToLower() switch
            {
                "createtime" => query.OrderByDescending(v => v.CreateTime),
                "digg" => query.OrderByDescending(v => v.DiggCount ?? 0),
                "play" => query.OrderByDescending(v => v.PlayCount ?? 0),
                "collect" => query.OrderByDescending(v => v.CollectCount ?? 0),
                _ => query.OrderByDescending(v => v.SyncTime),
            };

            var total = query.Count();
            // 分页语义(2026-09-02):默认100条/页;pageSize=0显式全量;上限500。
            // 默认分页而非全量——全量含字幕曾实测37s(同步读盘×5700条),3并发75s拖垮线程池
            if (pageSize > 0)
            {
                if (pageSize > 500) pageSize = 500;
                query = query.Skip((pageIndex - 1) * pageSize).Take(pageSize);
            }

            var pageList = query.ToList();
            var data = new List<object>(pageList.Count);
            foreach (var v in pageList)
            {
                uperMap.TryGetValue(v.AuthorId, out var f);
                data.Add(new
                {
                    uperName = f?.UperName ?? v.Author,
                    // 双路兜底:视频自身列(2026-08-31起入库随响应写入,喜欢/收藏来源也有) → dy_follow关联 → null
                    douyinNo = v.AuthorDouyinNo ?? f?.DouyinNo,
                    secUid = v.AuthorSecUid ?? f?.SecUid,
                    uperId = v.AuthorId,
                    title = v.VideoTitle,
                    videoId = v.AwemeId,
                    playCount = v.PlayCount ?? 0,
                    diggCount = v.DiggCount ?? 0,
                    commentCount = v.CommentCount ?? 0,
                    shareCount = v.ShareCount ?? 0,
                    collectCount = v.CollectCount ?? 0,
                    viedoType = v.ViedoType.ToString(),
                    syncTime = v.SyncTime == default ? null : v.SyncTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    createTime = v.CreateTime == default ? null : v.CreateTime.ToString("yyyy-MM-dd HH:mm:ss"),
                    // 2026-09-02 补齐对齐飞书18列:以下四项让本接口单请求=飞书多维表格同款数据
                    id = v.Id,                                          // 库内雪花Id(内网播放接口的键)
                    lanPlayUrl = string.IsNullOrWhiteSpace(lanBase) || string.IsNullOrWhiteSpace(v.VideoSavePath)
                        ? string.Empty
                        : $"{lanBase.TrimEnd('/')}/api/video/play/{v.Id}", // 免登录流式播放直链
                    playUrl = string.IsNullOrWhiteSpace(v.AwemeId) ? string.Empty : $"https://www.douyin.com/video/{v.AwemeId}",
                    dyUser = v.DyUser ?? string.Empty,                   // CK名称(哪个账号同步的)
                    // 字幕全文默认不带(逐条读盘慢);withSubtitle=true 显式要才读——异步await,不再同步阻塞线程
                    subtitle = withSubtitle && !string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                        ? await dy.net.utils.SubtitleTextReader.ReadAsync(v.SubtitleSavePath)
                        : string.Empty
                });
            }

            return Ok(new
            {
                total,
                pageIndex = pageSize > 0 ? pageIndex : (int?)null,
                pageSize = pageSize > 0 ? pageSize : (int?)null,
                data
            });
        }



        /// <summary>
        /// 分页查询
        /// </summary>
        /// <returns>分页结果</returns>
        [HttpPost("paged")]
        public async Task<IActionResult> GetPagedAsync(
           FollowRequestDto dto)
        {
            if (string.IsNullOrWhiteSpace(dto.MySelfId))
            {
                return ApiResult.Success(new { });
            }
            var (list, totalCount) = await _douyinFollowService.GetPagedAsync(dto);

            return ApiResult.Success(new
            {
                data = list,
                total = totalCount,
                pageIndex = dto.PageIndex,
                pageSize = dto.PageSize
            });
        }
        /// <summary>
        /// 重新同步-单次
        /// </summary>
        /// <returns></returns>
        [HttpGet("sync")]
        public async Task<IActionResult> SyncFollowList()
        {
            //后台异步
            _douyinQuartzJobService.StartFollowJobOnceAsync();
            await Task.Delay(1000);
            return ApiResult.Success();
        }
        [HttpPost("add")]
        public async Task<IActionResult> AddFollow(DouyinFollowed followed)
        {
            if (string.IsNullOrWhiteSpace(followed.mySelfId))
            {
                return ApiResult.Fail("请先配置抖音授权信息，抖音授权配置里面填写你的uid");
            }
            var res = await _douyinFollowService.AddAsync(followed);
            return ApiResult.SuccOrFail(res, "", res ? "" : "添加失败,或者已存在相同secuid和uid");
        }

        /// <summary>
        /// 修改关注同步状态
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPost("openOrCloseSync")]
        public async Task<IActionResult> OpenOrCloseSync(FollowUpdateDto dto)
        {

            if (dto.OpenSync)
            {
                if (!string.IsNullOrWhiteSpace(dto.SavePath))
                {
                    if (!DouyinFileNameHelper.IsValidWithoutSpecialChars(dto.SavePath))
                    {
                        return ApiResult.Fail("请输入有效文件夹名称（字母数字中文简体）");
                    }

                    if (dto.SavePath.Length > 20)
                    {
                        return ApiResult.Fail("请输入有效文件夹名称（最长20）");
                    }
                }
            }
            var result = await _douyinFollowService.OpenOrCloseSync(dto);
            return ApiResult.SuccOrFail(result, result);
        }

        /// <summary>
        /// 修改关注全量同步状态
        /// </summary>
        /// <param name="dto"></param>
        /// <returns></returns>
        [HttpPost("openOrCloseFullSync")]
        public async Task<IActionResult> OpenOrCloseFullSync(FollowUpdateDto dto)
        {
            return await OpenOrCloseSync(dto);
        }

       /// <summary>
       /// 删除关注对象
       /// </summary>
       /// <param name="dto"></param>
       /// <returns></returns>
        [HttpPost("delete")]
        public async Task<IActionResult> DeleteFollow(FollowUpdateDto dto)
        {
            var result = await _douyinFollowService.DeleteFollow(dto);
            return ApiResult.SuccOrFail(result, result);
        }
    }
}
