using System.Collections.Concurrent;
using System.Text;
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

        /// <summary>OAuth state 防 CSRF:发起时记录,回调时校验并移除(一次性)。容器重启丢失可接受(授权是交互式流程)。</summary>
        private static readonly ConcurrentDictionary<string, DateTime> _oauthStates = new();

        /// <summary>生成飞书用户授权页链接(前端新窗口打开)。</summary>
        [HttpGet("oauth/url")]
        public async Task<IActionResult> OAuthUrl()
        {
            var config = commonService.GetConfig();
            if (string.IsNullOrWhiteSpace(config?.FeishuAppId))
                return ApiResult.Fail("请先填写并保存 App ID / App Secret");
            var state = Guid.NewGuid().ToString("N");
            _oauthStates[state] = DateTime.Now;
            CleanupExpiredStates();
            var redirectUri = $"{Request.Scheme}://{Request.Host.Host}:{Request.Host.Port ?? 10101}/api/feishu/oauth/callback";
            var url = await bitableService.BuildAuthorizeUrlAsync(config, redirectUri, state);
            return ApiResult.Success(new { url = url.ToString() });
        }

        private static void CleanupExpiredStates()
        {
            var cutoff = DateTime.Now.AddMinutes(-10);
            foreach (var kv in _oauthStates.Where(kv => kv.Value < cutoff).ToList())
                _oauthStates.TryRemove(kv.Key, out _);
        }

        /// <summary>飞书授权回调:code 换 token 落库+清 Base 缓存。返回 HTML 结果页(浏览器直接打开,无 JWT)。</summary>
        [HttpGet("oauth/callback")]
        [AllowAnonymous]
        public async Task<IActionResult> OAuthCallback(string code, string state, string? error = null)
        {
            string title, detail;
            if (!string.IsNullOrEmpty(error))
            {
                title = "已取消授权";
                detail = $"飞书返回: {error}。未做任何变更,可回到设置页重新授权。";
            }
            else if (string.IsNullOrEmpty(code) || string.IsNullOrEmpty(state) || !_oauthStates.TryRemove(state, out _))
            {
                title = "授权失败";
                detail = "state 校验不通过(可能链接已过期或非本会话发起)。请回到设置页重新点击「授权飞书账号」。";
            }
            else
            {
                try
                {
                    var config = commonService.GetConfig();
                    var redirectUri = $"{Request.Scheme}://{Request.Host.Host}:{Request.Host.Port ?? 10101}/api/feishu/oauth/callback";
                    await bitableService.ExchangeCodeAsync(config, code, redirectUri);
                    title = "授权成功";
                    detail = "推送将以你的身份执行。回到设置页确认「文件夹token」已填写后,点「立即推送今天」即可在你的文件夹生成表格。";
                }
                catch (Exception ex)
                {
                    title = "授权失败";
                    detail = $"{ex.Message}。请回到设置页重试。";
                }
            }
            var safeTitle = HtmlEncode(title);
            var safeDetail = HtmlEncode(detail);
            var html = $@"<!DOCTYPE html><html><head><meta charset=""utf-8""><title>抖小云飞书授权</title></head>
<body style=""font-family:system-ui;padding:40px;text-align:center"">
<h2>{safeTitle}</h2><p style=""color:#555"">{safeDetail}</p>
<p style=""color:#999;font-size:13px"">本页面可关闭</p></body></html>";
            return Content(html, "text/html", Encoding.UTF8);
        }

        private static string HtmlEncode(string s) => System.Net.WebUtility.HtmlEncode(s ?? string.Empty);

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
            return ApiResult.Success(new
            {
                lastResult = config?.FeishuLastPushResult ?? string.Empty,
                oauth = new
                {
                    authorized = bitableService.HasUserAuth(config),
                    userTokenExpiresAt = config?.FeishuUserTokenExpiresAt,
                    refreshExpiresAt = config?.FeishuUserRefreshExpiresAt
                }
            });
        }
    }
}
