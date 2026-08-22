using dy.net.model.dto;
using dy.net.service;
using dy.net.utils;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace dy.net.Controllers
{
    /// <summary>飞书推送手动触发与状态查询。定时调度见 DouyinQuartzJobService.InitFeishuPushJob。</summary>
    [Route("api/[controller]")]
    [ApiController]
    [Authorize]
    public class FeishuController : ControllerBase
    {
        private readonly FeishuPushService pushService;
        private readonly DouyinCommonService commonService;

        public FeishuController(FeishuPushService pushService, DouyinCommonService commonService)
        {
            this.pushService = pushService;
            this.commonService = commonService;
        }

        /// <summary>立即推送今天(幂等:当天表清空重写,重复调用不产生重复行)。</summary>
        [HttpPost("push/today")]
        public async Task<IActionResult> PushToday()
        {
            var result = await pushService.RunDailyPushAsync();
            return ApiResult.Success(result);
        }

        /// <summary>上次推送结果(设置页展示)。</summary>
        [HttpGet("status")]
        public IActionResult Status()
        {
            var config = commonService.GetConfig();
            return ApiResult.Success(new { lastResult = config?.FeishuLastPushResult ?? string.Empty });
        }
    }
}
