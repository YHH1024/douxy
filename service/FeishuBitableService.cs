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
                BaseUrl = $"https://feishu.cn/base/{baseToken}", // 链接分享已开(tenant_editable),用户可直接打开
                Message = $"推送 {rows.Count} 条"
            };
        }

        /// <summary>
        /// 连通性测试(只读,不推送):①凭证换token ②读已缓存Base的表列表(bitable权限)
        /// ③群机器人发测试消息。逐项返回结果,单项失败不阻断后续检测。
        /// </summary>
        public async Task<List<FeishuTestItem>> TestConnectionAsync(AppConfig config, FeishuNotifyService notifyService)
        {
            var results = new List<FeishuTestItem>();

            // ① 凭证检测:强制刷新 token(避开缓存,验证 app_id/secret 与应用已发布)
            try
            {
                _cachedToken = null;
                await GetTenantTokenAsync(config);
                results.Add(new FeishuTestItem { Name = "凭证(App ID/Secret)", Ok = true, Message = "token 获取成功" });
            }
            catch (Exception ex)
            {
                results.Add(new FeishuTestItem { Name = "凭证(App ID/Secret)", Ok = false, Message = ex.Message });
                // token 都拿不到,后续检测必然失败
                results.Add(new FeishuTestItem { Name = "多维表格权限", Ok = false, Message = "跳过(凭证无效)" });
                results.Add(new FeishuTestItem { Name = "群机器人", Ok = false, Message = "跳过(凭证无效)" });
                return results;
            }

            // ② 表格权限:有缓存 Base 才测(读表列表验证 bitable scope);无缓存属正常,首次推送时验证
            try
            {
                if (!string.IsNullOrWhiteSpace(config.FeishuBaseTokenCache))
                {
                    var client = await AuthedClientAsync(config);
                    var resp = await client.GetAsync(
                        $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{config.FeishuBaseTokenCache}/tables?page_size=10");
                    var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuTableListData>>();
                    if (body?.Code == 0)
                        results.Add(new FeishuTestItem { Name = "多维表格权限", Ok = true, Message = $"可访问,共 {body.Data?.Items?.Count ?? 0} 张表" });
                    else
                        results.Add(new FeishuTestItem { Name = "多维表格权限", Ok = false, Message = $"code={body?.Code} {body?.Msg}(检查应用是否开通多维表格权限)" });
                }
                else
                {
                    results.Add(new FeishuTestItem { Name = "多维表格权限", Ok = true, Message = "首次推送时自动验证" });
                }
            }
            catch (Exception ex)
            {
                results.Add(new FeishuTestItem { Name = "多维表格权限", Ok = false, Message = ex.Message });
            }

            // ③ 群机器人:已填 webhook 才发测试消息(发到群里肉眼确认);未填不算失败
            try
            {
                if (!string.IsNullOrWhiteSpace(config.FeishuNotifyWebhook))
                {
                    var (sendOk, sendErr) = await notifyService.SendWithResultAsync(config,
                        $"抖小云测试消息({DateTime.Now:HH:mm:ss})——收到说明群机器人配置正确");
                    results.Add(sendOk
                        ? new FeishuTestItem { Name = "群机器人", Ok = true, Message = "测试消息已发送,请到群里确认" }
                        : new FeishuTestItem { Name = "群机器人", Ok = false, Message = sendErr });
                }
                else
                {
                    results.Add(new FeishuTestItem { Name = "群机器人", Ok = true, Message = "未配置webhook,跳过(不影响推送)" });
                }
            }
            catch (Exception ex)
            {
                results.Add(new FeishuTestItem { Name = "群机器人", Ok = false, Message = ex.Message });
            }

            return results;
        }

        // ==================== token ====================

        /// <summary>是否已有有效的用户授权(refresh_token 未过期即视为已授权,access 可刷新)。</summary>
        public bool HasUserAuth(AppConfig config)
            => !string.IsNullOrWhiteSpace(config.FeishuUserRefreshToken)
               && config.FeishuUserRefreshExpiresAt.HasValue
               && config.FeishuUserRefreshExpiresAt.Value > DateTime.Now;

        /// <summary>构造飞书用户授权页链接。scope 含 offline_access 才会返回 refresh_token。
        /// 2026-08 官方文档:新授权页在 accounts.feishu.cn,参数名 client_id(旧 open.feishu.cn/index?app_id= 已弃用,会报20029)。</summary>
        public Task<Uri> BuildAuthorizeUrlAsync(AppConfig config, string redirectUri, string state)
        {
            var scope = Uri.EscapeDataString("bitable:app drive:drive offline_access");
            var redirect = Uri.EscapeDataString(redirectUri);
            var url = $"https://accounts.feishu.cn/open-apis/authen/v1/authorize?client_id={config.FeishuAppId}&redirect_uri={redirect}&scope={scope}&prompt=consent&state={state}";
            return Task.FromResult(new Uri(url));
        }

        /// <summary>授权码换 token 并落库(含 access/refresh 过期时刻)。同时清 Base 缓存,触发下次推送在新身份的文件夹重建。</summary>
        public async Task<string> ExchangeCodeAsync(AppConfig config, string code, string redirectUri)
        {
            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/authen/v2/oauth/token", new
            {
                grant_type = "authorization_code",
                client_id = config.FeishuAppId,
                client_secret = config.FeishuAppSecret,
                code,
                redirect_uri = redirectUri
            });
            var body = await resp.Content.ReadFromJsonAsync<FeishuOAuthTokenResp>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.AccessToken))
                throw new Exception($"飞书授权码换token失败: code={body?.Code} {body?.Error} {body?.ErrorDescription}");
            await SaveUserTokensAsync(config, body);
            config.FeishuBaseTokenCache = null;
            config.FeishuBaseMonthCache = null;
            await commonService.UpdateConfig(config);
            return body.AccessToken;
        }

        /// <summary>落库 token 与过期时刻。</summary>
        private async Task SaveUserTokensAsync(AppConfig config, FeishuOAuthTokenResp body)
        {
            config.FeishuUserAccessToken = body.AccessToken;
            config.FeishuUserTokenExpiresAt = DateTime.Now.AddSeconds((body.ExpiresIn ?? 7200) - 300);
            if (!string.IsNullOrEmpty(body.RefreshToken))
            {
                config.FeishuUserRefreshToken = body.RefreshToken;
                config.FeishuUserRefreshExpiresAt = DateTime.Now.AddSeconds(body.RefreshExpiresIn ?? 604800);
            }
            await commonService.UpdateConfig(config);
        }

        /// <summary>获取用户token:未过期直接用;过期用refresh刷新(新refresh立即落库,旧的一次性作废);refresh也过期抛明确错误。</summary>
        private async Task<string> GetUserAccessTokenAsync(AppConfig config)
        {
            if (!string.IsNullOrEmpty(config.FeishuUserAccessToken) && config.FeishuUserTokenExpiresAt > DateTime.Now)
                return config.FeishuUserAccessToken;

            if (!HasUserAuth(config))
                throw new Exception("飞书用户授权已过期,请到设置页重新点击「授权飞书账号」");

            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/authen/v2/oauth/token", new
            {
                grant_type = "refresh_token",
                client_id = config.FeishuAppId,
                client_secret = config.FeishuAppSecret,
                refresh_token = config.FeishuUserRefreshToken
            });
            var body = await resp.Content.ReadFromJsonAsync<FeishuOAuthTokenResp>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.AccessToken))
            {
                // 失效的refresh清掉,让状态回到「未授权」而不是反复用死token重试
                config.FeishuUserAccessToken = null;
                config.FeishuUserRefreshToken = null;
                config.FeishuUserTokenExpiresAt = null;
                config.FeishuUserRefreshExpiresAt = null;
                await commonService.UpdateConfig(config);
                throw new Exception($"飞书用户授权已失效({body?.Error ?? body?.Code.ToString()}),请到设置页重新授权");
            }
            await SaveUserTokensAsync(config, body);
            return body.AccessToken;
        }

        private async Task<string> GetTenantTokenAsync(AppConfig config)
        {
            if (!string.IsNullOrEmpty(_cachedToken) && DateTime.Now < _tokenExpireAt)
                return _cachedToken;

            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/auth/v3/tenant_access_token/internal",
                new { app_id = config.FeishuAppId, app_secret = config.FeishuAppSecret });
            // 该接口是扁平响应(token在顶层无data包裹),不能用通用信封解析
            var body = await resp.Content.ReadFromJsonAsync<FeishuTokenResp>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body?.Token))
                throw new Exception($"飞书token获取失败: code={body?.Code} msg={body?.Msg}");
            _cachedToken = body.Token;
            _tokenExpireAt = DateTime.Now.AddSeconds(body.Expire - 300);
            return _cachedToken;
        }

        /// <summary>带鉴权客户端:用户身份优先(文件建在用户文件夹),无用户授权回落应用身份(现有行为)。</summary>
        private async Task<HttpClient> AuthedClientAsync(AppConfig config)
        {
            var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
            var token = HasUserAuth(config)
                ? await GetUserAccessTokenAsync(config)
                : await GetTenantTokenAsync(config);
            client.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
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

            // Base 存放文件夹:用户身份=必须建在用户自己的文件夹(个人版飞书用户文件夹只能以用户身份写入);
            // 应用身份=FolderToken 优先,未配置则应用自建专属文件夹
            string folderToken;
            if (HasUserAuth(config))
            {
                if (string.IsNullOrWhiteSpace(config.FeishuFolderToken))
                    throw new Exception("用户授权模式下必须在设置页填写文件夹token(你自己的文件夹,地址栏 folder/ 后那串)");
                folderToken = config.FeishuFolderToken;
                Log.Information("[feishu] 用户身份模式,Base建在用户文件夹 {Folder}", folderToken);
            }
            else
            {
                folderToken = config.FeishuFolderToken;
                if (string.IsNullOrWhiteSpace(folderToken))
                {
                    folderToken = await EnsureAutoFolderAsync(client, config);
                    Log.Information("[feishu] 月度Base将建在专属文件夹 {Folder}", folderToken);
                }
            }

            var payload = string.IsNullOrWhiteSpace(folderToken)
                ? new { name = $"抖小云同步数据-{DateTime.Now:yyyy年M月}" }
                : (object)new { name = $"抖小云同步数据-{DateTime.Now:yyyy年M月}", folder_token = folderToken };
            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/bitable/v1/apps", payload);
            var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuAppData>>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.Data?.App?.AppToken))
                throw new Exception($"飞书创建Base失败: code={body?.Code} msg={body?.Msg}");
            var appToken = body.Data.App.AppToken;
            Log.Information("[feishu] 新建月度Base {Token}", appToken);

            // 组织内链接可编辑:个人版无法给文件夹加应用协作者,靠链接让用户能直接打开Base(失败不阻断)
            // 用户身份下Base归属本人,无需链接分享
            if (!HasUserAuth(config))
            {
                try
                {
                    var shareReq = new HttpRequestMessage(HttpMethod.Patch,
                        $"{FEISHU_HOST}/open-apis/drive/v1/permissions/{appToken}/public?type=bitable")
                    { Content = JsonContent.Create(new { link_share_entity = "tenant_editable" }) };
                    var shareResp = await client.SendAsync(shareReq);
                    var shareBody = await shareResp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                    if (shareBody?.Code != 0)
                        Log.Warning("[feishu] 设置链接分享失败: code={Code} msg={Msg}", shareBody?.Code, shareBody?.Msg);
                }
                catch (Exception ex)
                {
                    Log.Warning(ex, "[feishu] 设置链接分享异常(不阻断)");
                }
            }

            // 加协作者(失败不阻断推送,仅告警);邮箱未填则跳过;用户身份下Base归属本人,无需加协作者
            if (!HasUserAuth(config) && !string.IsNullOrWhiteSpace(config.FeishuUserEmail))
            {
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
            }

            config.FeishuBaseTokenCache = appToken;
            config.FeishuBaseMonthCache = month;
            await commonService.UpdateConfig(config);
            return appToken;
        }

        /// <summary>定位/创建专属文件夹「抖小云同步数据」:缓存命中直接用,否则 drive/v1 create_folder 建在应用根空间并记缓存。文件夹本身不支持public链接共享,逐个Base设。</summary>
        private async Task<string> EnsureAutoFolderAsync(HttpClient client, AppConfig config)
        {
            if (!string.IsNullOrWhiteSpace(config.FeishuAutoFolderToken))
                return config.FeishuAutoFolderToken;

            var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/drive/v1/files/create_folder",
                new { name = "抖小云同步数据", folder_token = "" });
            var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuFolderData>>();
            if (body?.Code != 0 || string.IsNullOrEmpty(body.Data?.Token))
                throw new Exception($"飞书创建文件夹失败: code={body?.Code} msg={body?.Msg}");
            Log.Information("[feishu] 新建专属文件夹 {Token}", body.Data.Token);
            config.FeishuAutoFolderToken = body.Data.Token;
            await commonService.UpdateConfig(config);
            return body.Data.Token;
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
                new { field_name = "同步时间", type = 5, property = new { date_formatter = "yyyy/MM/dd HH:mm" } },
                new { field_name = "发布时间", type = 5, property = new { date_formatter = "yyyy/MM/dd HH:mm" } },
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

        /// <summary>统计回填:把变更视频的最新五项统计回写到它们当初推送的日期表原行(标题精确匹配)。
        /// 只处理本月 Base 内的表(跨月旧表跳过);飞书未配置/无缓存返回0。</summary>
        public async Task<int> UpdateStatsAsync(AppConfig config, List<DouyinVideo> changed)
        {
            if (changed == null || changed.Count == 0) return 0;
            if (string.IsNullOrWhiteSpace(config?.FeishuBaseTokenCache)) return 0;

            var client = await AuthedClientAsync(config);
            var baseToken = config.FeishuBaseTokenCache;

            // 列出本月 Base 全部表,建表名→table_id 映射
            var listResp = await client.GetAsync($"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables?page_size=100");
            var listBody = await listResp.Content.ReadFromJsonAsync<FeishuResp<FeishuTableListData>>();
            if (listBody?.Code != 0) throw new Exception($"飞书获取表列表失败: code={listBody?.Code} msg={listBody?.Msg}");
            var tableMap = (listBody.Data?.Items ?? new List<FeishuTableInfo>())
                .GroupBy(t => t.Name).ToDictionary(g => g.Key, g => g.First().TableId);

            int updated = 0, unmatched = 0;
            // 按视频的入库日期分组 →「M月d日」表
            foreach (var group in changed.GroupBy(v => v.SyncTime.Date))
            {
                var tableName = $"{group.Key.Month}月{group.Key.Day}日";
                if (!tableMap.TryGetValue(tableName, out var tableId))
                {
                    unmatched += group.Count();
                    continue;
                }

                // 分页读回该表全部行(record_id → 标题)
                var rowTitles = new Dictionary<string, string>(); // record_id → 标题
                string pageToken = null;
                do
                {
                    var url = $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records?page_size=500"
                            + (pageToken == null ? "" : $"&page_token={pageToken}");
                    var resp = await client.GetAsync(url);
                    var body = await resp.Content.ReadFromJsonAsync<FeishuResp<FeishuRecordFullData>>();
                    if (body?.Code != 0) throw new Exception($"飞书读取记录失败: code={body?.Code} msg={body?.Msg}");
                    foreach (var item in body.Data?.Items ?? new List<FeishuRecordFull>())
                    {
                        var title = ReadTextField(item.Fields, "视频标题");
                        if (!string.IsNullOrWhiteSpace(title)) rowTitles[item.RecordId] = title;
                    }
                    pageToken = body.Data?.HasMore == true ? body.Data.PageToken : null;
                } while (pageToken != null);

                // 标题 → record_id(同表标题唯一性足够;重复取第一行)
                var titleToRecord = rowTitles.GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);

                var updates = new List<object>();
                foreach (var video in group)
                {
                    if (video.VideoTitle == null || !titleToRecord.TryGetValue(video.VideoTitle, out var recordId))
                    {
                        unmatched++;
                        continue;
                    }
                    updates.Add(new
                    {
                        record_id = recordId,
                        fields = new Dictionary<string, object>
                        {
                            ["播放"] = video.PlayCount ?? 0,
                            ["点赞"] = video.DiggCount ?? 0,
                            ["评论"] = video.CommentCount ?? 0,
                            ["分享"] = video.ShareCount ?? 0,
                            ["收藏"] = video.CollectCount ?? 0,
                        }
                    });
                }

                foreach (var chunk in updates.Chunk(200))
                {
                    var upResp = await client.PostAsJsonAsync(
                        $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records/batch_update",
                        new { records = chunk });
                    var upBody = await upResp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                    if (upBody?.Code == 1254291)
                    {
                        await Task.Delay(RETRY_DELAYS[0]);
                        upResp = await client.PostAsJsonAsync(
                            $"{FEISHU_HOST}/open-apis/bitable/v1/apps/{baseToken}/tables/{tableId}/records/batch_update",
                            new { records = chunk });
                        upBody = await upResp.Content.ReadFromJsonAsync<FeishuResp<object>>();
                    }
                    EnsureOk(upBody, "统计回写");
                    updated += chunk.Length;
                    await Task.Delay(BATCH_DELAY_MS);
                }
            }
            Log.Information("[feishu] 统计回写完成 updated={Updated} unmatched={Unmatched}", updated, unmatched);
            return updated;
        }

        /// <summary>从飞书 fields(JsonElement)里取文本字段值(富文本 [{text}] 拼接;纯字符串直取)。</summary>
        private static string ReadTextField(JsonElement fields, string name)
        {
            if (fields.ValueKind != JsonValueKind.Object || !fields.TryGetProperty(name, out var v)) return null;
            if (v.ValueKind == JsonValueKind.String) return v.GetString();
            if (v.ValueKind == JsonValueKind.Array)
            {
                var sb = new System.Text.StringBuilder();
                foreach (var seg in v.EnumerateArray())
                {
                    if (seg.ValueKind == JsonValueKind.Object && seg.TryGetProperty("text", out var t))
                        sb.Append(t.GetString());
                }
                return sb.ToString();
            }
            return null;
        }
    }
}
