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
        private readonly FeishuBitableService bitableService;
        private readonly FeishuNotifyService notifyService;

        public FeishuController(FeishuPushService pushService, DouyinCommonService commonService,
            FeishuBitableService bitableService, FeishuNotifyService notifyService)
        {
            this.pushService = pushService;
            this.commonService = commonService;
            this.bitableService = bitableService;
            this.notifyService = notifyService;
        }

        /// <summary>立即推送今天(幂等:当天表清空重写,重复调用不产生重复行)。</summary>
        [HttpPost("push/today")]
        public async Task<IActionResult> PushToday()
        {
            var result = await pushService.RunDailyPushAsync();
            return ApiResult.Success(result);
        }

        /// <summary>连通性测试(只读):凭证/多维表格权限/群机器人逐项检测,单项失败不阻断。</summary>
        [HttpPost("test")]
        public async Task<IActionResult> TestConnection()
        {
            var config = commonService.GetConfig();
            if (string.IsNullOrWhiteSpace(config?.FeishuAppId) || string.IsNullOrWhiteSpace(config.FeishuAppSecret))
            {
                return ApiResult.Success(new List<FeishuTestItem>
                {
                    new() { Name = "凭证(App ID/Secret)", Ok = false, Message = "请先填写 App ID / App Secret 并保存" }
                });
            }
            var items = await bitableService.TestConnectionAsync(config, notifyService);
            return ApiResult.Success(items);
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
