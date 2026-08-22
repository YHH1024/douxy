using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using dy.net.model.dto;
using dy.net.model.entity;
using Serilog;

namespace dy.net.service
{
    /// <summary>
    /// 飞书多维表格 API 客户端:tenant token 管理、月度Base定位/创建、每日表清空重写、批量写入。
    /// 编排逻辑(读数据/通知/结果落库)在 FeishuPushService,本类只做飞书 API。
    /// </summary>
    public class FeishuBitableService
    {
        public const string FEISHU_HTTP_CLIENT = "feishu";
        private const string FEISHU_HOST = "https://open.feishu.cn";
        private const int BATCH_SIZE = 200;              // 保守值(避开1254104)
        private const int BATCH_DELAY_MS = 300;
        private static readonly int[] RETRY_DELAYS = { 1000, 2000, 4000 };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly DouyinCommonService commonService;

        // tenant_access_token 缓存(2h TTL,提前5min刷新)
        private string _cachedToken;
        private DateTime _tokenExpireAt = DateTime.MinValue;

        public FeishuBitableService(IHttpClientFactory httpClientFactory, DouyinCommonService commonService)
        {
            _httpClientFactory = httpClientFactory;
            this.commonService = commonService;
        }

        /// <summary>推送入口:定位/创建本月Base → 定位/清空今日表 → 批量写入。返回含BaseUrl的结果。</summary>
        public async Task<FeishuPushResult> PushDailyAsync(AppConfig config, List<FeishuVideoRow> rows)
        {
            var baseToken = await EnsureMonthlyBaseAsync(config);
            var tableName = $"{DateTime.Now.Month}月{DateTime.Now.Day}日";
            var tableId = await EnsureDailyTableAsync(config, baseToken, tableName);
            await ClearTableAsync(config, baseToken, tableId);
            await BatchCreateAsync(config, baseToken, tableId, rows);
            return new FeishuPushResult
            {
                Success = true,
                Count = rows.Count,
                BaseUrl = $"https://feishu.cn/base/{baseToken}",
                Message = $"推送 {rows.Count} 条"
            };
        }

        // ==================== token ====================

        private async Task<string> GetTenantTokenAsync(AppConfig config)
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.Now < _tokenExpireAt)
                return _cachedToken;

            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/auth/v3/tenant_access_token/internal",
                new { app_id = config.FeishuAppId, app_secret = config.FeishuAppSecret });
            var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuTokenData>>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.Data?.Token))
                throw new Exception($"飞书token获取失败: code={body?.Code} msg={body?.Msg}");
            _cachedToken = body.Data.Token;
            _tokenExpireAt = DateTime.Now.AddSeconds(body.Data.Expire - 300);
            return _cachedToken;
        }

        private async Task<HttpClient> AuthedClientAsync(AppConfig config)
        {
            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", await GetTenantTokenAsync(config));
            return client;
        }

        private static void EnsureOk(FeishuResp<object> body, string action)
        {
            if (body?.Code != 0)
                throw new Exception($"飞书{action}失败: code={body?.Code} msg={body?.Msg}");
        }

        // ==================== Base ====================

        /// <summary>定位本月Base:缓存命中直接用,否则新建+加协作者+写缓存。月份变了自动滚动。</summary>
        private async Task<string> EnsureMonthlyBaseAsync(AppConfig config)
        {
            var month = $"{DateTime.Now:yyyy-M}";
            if (config.FeishuBaseMonthCache == month && !string.IsNullOrWhiteSpace(config.FeishuBaseTokenCache))
                return config.FeishuBaseTokenCache;

            var client = await AuthedClientAsync(config);
            var payload = string.IsNullOrWhiteSpace(config.FeishuFolderToken)
                ? new { name = $"抖小云同步数据-{DateTime.Now:yyyy年M月}" }
                : (object)new { name = $"抖小云同步数据-{DateTime.Now:yyyy年M月}", folder_token = config.FeishuFolderToken };
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/bitable/v1/apps", payload);
            var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuAppData>>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.Data?.App?.AppToken))
                throw new Exception($"飞书创建Base失败: code={body?.Code} msg={body?.Msg}");
            var appToken = body.Data.App.AppToken;
            Log.Information("[feishu] 新建月度Base {Token}", appToken);

            // 加协作者(失败不阻断推送,仅告警)
            try
            {
                var permResp = await client.PostAsJsonAsync(
                    $"{FEISHU_HOST}/open-apis/drive/v1/permissions/{appToken}/members?type=bitable",
                    new { member_type = "email", member_id = config.FeishuUserEmail, perm = "edit" });
                var permBody = await permResp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                if (permBody?.Code != 0)
                    Log.Warning("[feishu] 加协作者失败: code={Code} msg={Msg}", permBody?.Code, permBody?.Msg);
            }
            catch (Exception ex)
            {
                Log.Warning(ex, "[feishu] 加协作者异常(不阻断)");
            }

            config.FeishuBaseTokenCache = appToken;
            config.FeishuBaseMonthCache = month;
            await commonService.UpdateConfig(config);
            return appToken;
        }

        // ==================== Table ====================

        /// <summary>定位今日表:存在返回table_id,不存在按13列结构创建。</summary>
        private async Task<string> EnsureDailyTableAsync(AppConfig config, string baseToken, string tableName)
        {
            var client = await AuthedClientAsync(config);
            var listResp = await client.GetAsync($"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables?page_size=100");
            var listBody = await listResp.Content.ReadFromJsonAsync<FeishuResp<FeishuTableListData>>();
            if (listBody?.Code != 0)
                throw new Exception($"飞书获取表列表失败: code={listBody?.Code} msg={listBody?.Msg}");
            var existing = listBody.Data?.Items?.FirstOrDefault(t => t.Name == tableName);
            if (existing != null)
                return existing.TableId;

            var fields = new object[]
            {
                new { field_name = "同步时间", type = 5, property = new { date_formatter = "yyyy/M/d HH:mm" } },
                new { field_name = "发布时间", type = 5, property = new { date_formatter = "yyyy/M/d HH:mm" } },
                new { field_name = "同步类型", type = 3 },
                new { field_name = "博主", type = 1 },
                new { field_name = "视频类型", type = 3 },
                new { field_name = "视频标题", type = 1 },
                new { field_name = "CK名称", type = 3 },
                new { field_name = "播放", type = 2, property = new { formatter = "0" } },
                new { field_name = "点赞", type = 2, property = new { formatter = "0" } },
                new { field_name = "评论", type = 2, property = new { formatter = "0" } },
                new { field_name = "分享", type = 2, property = new { formatter = "0" } },
                new { field_name = "收藏", type = 2, property = new { formatter = "0" } },
                new { field_name = "字幕全文", type = 1 },
            };
            var createResp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables",
                new { table = new { name = tableName, fields } });
            var createBody = await createResp.Content.ReadFromJsonAsync<FeishuResp<FeishuCreateTableData>>();
            if (createBody?.Code != 0 || string.IsNullOrEmpty(createBody.Data?.TableId))
                throw new Exception($"飞书创建每日表失败: code={createBody?.Code} msg={createBody?.Msg}");
            Log.Information("[feishu] 新建每日表 {Name} {TableId}", tableName, createBody.Data.TableId);
            return createBody.Data.TableId;
        }

        // ==================== Records ====================

        /// <summary>清空表全部记录(分页拿record_id → batch_delete)。空表直接返回。</summary>
        private async Task ClearTableAsync(AppConfig config, string baseToken, string tableId)
        {
            var client = await AuthedClientAsync(config);
            string pageToken = null;
            var ids = new List<string>();
            do
            {
                var url = $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records?page_size=500"
                        + (pageToken == null ? "" : $"&page_token={pageToken}");
                var resp = await client.GetAsync(url);
                var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuRecordListData>>();
                if (body?.Code != 0)
                    throw new Exception($"飞书读取记录失败: code={body?.Code} msg={body?.Msg}");
                if (body.Data?.Items != null)
                    ids.AddRange(body.Data.Items.Select(r => r.RecordId));
                pageToken = body.Data?.HasMore == true ? body.Data.PageToken : null;
            } while (pageToken != null);

            if (ids.Count == 0) return;
            Log.Information("[feishu] 清空旧记录 {Count} 条", ids.Count);
            foreach (var chunk in ids.Chunk(200))
            {
                var delResp = await client.PostAsJsonAsync(
                    $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records/batch_delete",
                    new { records = chunk });
                var delBody = await delResp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                EnsureOk(delBody, "清空旧记录");
                await Task.Delay(BATCH_DELAY_MS);
            }
        }

        /// <summary>200条/批串行写入,1254291退避重试3次。</summary>
        private async Task BatchCreateAsync(AppConfig config, string baseToken, string tableId, List<FeishuVideoRow> rows)
        {
            if (rows.Count == 0) return;
            var client = await AuthedClientAsync(config);
            foreach (var chunk in rows.Chunk(BATCH_SIZE))
            {
                var records = chunk.Select(r => new
                {
                    fields = new Dictionary<string, object>
                    {
                        ["同步时间"] = r.SyncTimeMs,
                        ["发布时间"] = r.CreateTimeMs.HasValue ? r.CreateTimeMs.Value : r.SyncTimeMs,
                        ["同步类型"] = r.SyncType ?? string.Empty,
                        ["博主"] = r.Author ?? string.Empty,
                        ["视频类型"] = r.VideoKind ?? string.Empty,
                        ["视频标题"] = r.Title ?? string.Empty,
                        ["CK名称"] = r.DyUser ?? string.Empty,
                        ["播放"] = r.PlayCount,
                        ["点赞"] = r.DiggCount,
                        ["评论"] = r.CommentCount,
                        ["分享"] = r.ShareCount,
                        ["收藏"] = r.CollectCount,
                        ["字幕全文"] = r.Subtitle ?? string.Empty,
                    }
                }).ToList();

                Exception lastError = null;
                for (int attempt = 0; attempt <= RETRY_DELAYS.Length; attempt++)
                {
                    var resp = await client.PostAsJsonAsync(
                        $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records/batch_create",
                        new { records });
                    var body = await resp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                    if (body?.Code == 0) { lastError = null; break; }
                    if (body?.Code == 1254291 && attempt < RETRY_DELAYS.Length)
                    {
                        Log.Warning("[feishu] 写入限流,退避重试 {Attempt}", attempt + 1);
                        await Task.Delay(RETRY_DELAYS[attempt]);
                        lastError = new Exception($"飞书批量写入限流(重试{attempt + 1}次后仍失败)");
                        continue;
                    }
                    throw new Exception($"飞书批量写入失败: code={body?.Code} msg={body?.Msg}");
                }
                if (lastError != null) throw lastError;
                await Task.Delay(BATCH_DELAY_MS);
            }
        }
    }
}
