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
                var (_, _) = await SendWithResultAsync(config, text);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[feishu] 通知发送失败(不阻断推送)");
            }
        }

        /// <summary>发送并返回真实结果(连通性测试用):(是否成功,失败原因)。成功时静默。</summary>
        public async Task<(bool Ok, string Error)> SendWithResultAsync(AppConfig config, string text)
        {
            var client = _httpClientFactory.CreateClient(FeishuBitableService.FEISHU_HTTP_CLIENT);
            var resp = await client.PostAsJsonAsync(config.FeishuNotifyWebhook,
                new { msg_type = "text", content = new { text } });
            var body = await resp.Content.ReadAsStringAsync();
            var ok = false;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(body);
                ok = doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.GetInt32() == 0;
            }
            catch { ok = false; }
            if (resp.IsSuccessStatusCode && ok)
                return (true, null);
            Log.Warning("[feishu] 通知发送异常: {Status} {Body}", resp.StatusCode, body);
            return (false, $"HTTP {resp.StatusCode} {body}");
        }
    }
}
