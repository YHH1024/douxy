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

        public FollowController(DouyinFollowService douyinFollowService, DouyinQuartzJobService douyinQuartzJobService,
            DouyinVideoService douyinVideoService)
        {
            this._douyinFollowService = douyinFollowService;
            _douyinQuartzJobService = douyinQuartzJobService;
            this.douyinVideoService = douyinVideoService;
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
        /// 视频字段在后(title 后跟 videoId)。支持 ?uperId= 按博主过滤、?limit= 限量(默认全量,先到先得按同步时间倒序)。
        /// sec_uid 由视频 AuthorId 关联 dy_follow.UperId 得到——视频表本身无此列,未关注的手动博主可能关联不上(secUid为null)。</summary>
        [HttpGet("open/videos")]
        [AllowAnonymous]
        public async Task<IActionResult> GetOpenVideos([FromQuery] string uperId, [FromQuery] int limit = 0)
        {
            var videos = await douyinVideoService.GetAllAsync();
            // 博主身份映射:UperId -> (douyinNo, secUid)
            var follows = await _douyinFollowService.GetPagedAllAsync();
            var uperMap = follows.GroupBy(f => f.UperId).ToDictionary(g => g.Key, g => g.First());

            var query = videos.OrderByDescending(v => v.SyncTime).AsEnumerable();
            if (!string.IsNullOrWhiteSpace(uperId))
                query = query.Where(v => v.AuthorId == uperId);
            if (limit > 0)
                query = query.Take(limit);

            return Ok(query.Select(v =>
            {
                uperMap.TryGetValue(v.AuthorId, out var f);
                return new
                {
                    uperName = f?.UperName ?? v.Author,
                    // 双路兜底:视频自身列(2026-08-31起入库随响应写入,喜欢/收藏来源也有) → dy_follow关联 → null
                    douyinNo = v.AuthorDouyinNo ?? f?.DouyinNo,
                    secUid = v.AuthorSecUid ?? f?.SecUid,
                    uperId = v.AuthorId,
                    title = v.VideoTitle,
                    videoId = v.AwemeId,
                    syncTime = v.SyncTime == default ? null : v.SyncTime.ToString("yyyy-MM-dd HH:mm:ss")
                };
            }).ToList());
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
