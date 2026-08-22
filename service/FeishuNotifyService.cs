using System.Net.Http.Json;
using dy.net.model.entity;
using Serilog;

namespace dy.net.service
{
    /// <summary>飞书群机器人 webhook 通知。webhook 未配置时静默跳过。</summary>
    public class FeishuNotifyService
    {
        private readonly IHttpClientFactory _httpClientFactory;

        public FeishuNotifyService(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        public async Task SendAsync(AppConfig config, string text)
        {
            if (string.IsNullOrWhiteSpace(config?.FeishuNotifyWebhook))
                return;
            try
            {
                var client = _httpClientFactory.CreateClient(FeishuBitableService.FEISHU_HTTP_CLIENT);
                var resp = await client.PostAsJsonAsync(config.FeishuNotifyWebhook,
                    new { msg_type = "text", content = new { text } });
                var body = await resp.Content.ReadAsStringAsync();
                if (!resp.IsSuccessStatusCode || !body.Contains("\"code\":0"))
                    Log.Warning("[feishu] 通知发送异常: {Status} {Body}", resp.StatusCode, body);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[feishu] 通知发送失败(不阻断推送)");
            }
        }
    }
}
