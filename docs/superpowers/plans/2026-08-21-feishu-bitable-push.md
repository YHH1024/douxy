# 飞书多维表格定时推送 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** dysync 容器内新增 Quartz 每日任务，把当天同步记录（13 列含 ASR 字幕全文）推送到飞书多维表格（每月一个 Base、每日一张表、清空重写），完成后群机器人 webhook 通知结果。

**Architecture:** 纯后端 .NET 6 手写 HTTP 客户端直连飞书开放 API（System.Text.Json，零 NuGet 新增）；`FeishuBitableService`（API 客户端）/ `FeishuNotifyService`（webhook）/ `FeishuPushService`（编排）三层分离，Quartz Job 与手动 Controller 共用编排层；前端设置页加配置区块。

**Tech Stack:** .NET 6 / Quartz.NET / SqlSugar(CodeFirst) / System.Text.Json / Vue3 + antd / 飞书开放 API（tenant_access_token + bitable v1 + drive permission + 群机器人 webhook）

**Spec:** `docs/superpowers/specs/2026-08-21-feishu-bitable-push-design.md`

## Global Constraints

- **零 NuGet 新增依赖**（docker/NuGet 外网受限），JSON 只用 System.Text.Json，HTTP 只用 IHttpClientFactory
- 批量写入 **200 条/批**串行，批间 300ms；`1254291` 退避 1s/2s/4s 重试 3 次
- 幂等策略 = **清空重写**：每日表存在则先删全部记录再写入
- 13 列与 `VideoController.ExportTodayExcel` 完全一致（列名/顺序/字幕 >32000 截断）
- Base 命名 `抖小云同步数据-yyyy年M月`，表命名 `M月d日`
- 默认 cron `0 50 23 * * ?`（23:50）；`FeishuPushEnabled=false` 时任务不调度
- 本仓库无单元测试设施，每个任务的验证 = `dotnet build` 0 错误 + 指定检查；E2E 在 Task 8
- 构建用 `D:/dotnet-sdk/dotnet.exe`（不在 PATH，绝对路径调用）
- 改动部署遵循现行镜像流程（见 Task 8），前端 dist 有 docker cp 嵌套陷阱（Task 8 Step 4 有固定解法）

---

### Task 1: AppConfig 配置字段 + Feishu DTO

**Files:**
- Modify: `D:\dysync\dysync.net\model\entity\AppConfig.cs`（在 `AsrOverwriteExisting` 属性后追加，约 L164 后）
- Create: `D:\dysync\dysync.net\model\dto\FeishuDtos.cs`

**Interfaces:**
- Produces:
  - `AppConfig.FeishuPushEnabled(bool)`, `FeishuAppId/FeishuAppSecret/FeishuUserEmail/FeishuNotifyWebhook/FeishuFolderToken/FeishuPushCron/FeishuBaseTokenCache/FeishuBaseMonthCache/FeishuLastPushResult(string?)`
  - `FeishuVideoRow`（推送行数据）、`FeishuPushResult`（推送结果）——Task 2/4 消费
  - 内部 API 信封 `FeishuResp<T>` 等——仅 Task 2 用

- [ ] **Step 1: AppConfig 追加字段**

在 `AsrOverwriteExisting` 属性（L160-164）之后、类结束前追加：

```csharp
        /// <summary>飞书推送总开关</summary>
        public bool FeishuPushEnabled { get; set; }
        /// <summary>飞书自建应用 AppId</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuAppId { get; set; }
        /// <summary>飞书自建应用 AppSecret</summary>
        [SugarColumn(Length = 200, IsNullable = true)]
        public string FeishuAppSecret { get; set; }
        /// <summary>你的飞书邮箱(新建Base后自动加为协作者)</summary>
        [SugarColumn(Length = 200, IsNullable = true)]
        public string FeishuUserEmail { get; set; }
        /// <summary>飞书群机器人webhook(空则跳过通知)</summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string FeishuNotifyWebhook { get; set; }
        /// <summary>月度Base存放的文件夹token(空则应用根空间)</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuFolderToken { get; set; }
        /// <summary>推送时刻cron(默认 0 50 23 * * ?)</summary>
        [SugarColumn(Length = 50, IsNullable = true)]
        public string FeishuPushCron { get; set; }
        /// <summary>运行时缓存:本月Base token(程序自管理,不在设置页展示)</summary>
        [SugarColumn(Length = 100, IsNullable = true)]
        public string FeishuBaseTokenCache { get; set; }
        /// <summary>运行时缓存:缓存对应的月份yyyy-M(程序自管理)</summary>
        [SugarColumn(Length = 20, IsNullable = true)]
        public string FeishuBaseMonthCache { get; set; }
        /// <summary>上次推送结果展示(设置页只读展示)</summary>
        [SugarColumn(Length = 500, IsNullable = true)]
        public string FeishuLastPushResult { get; set; }
```

- [ ] **Step 2: 创建 FeishuDtos.cs**

```csharp
using System.Text.Json.Serialization;

namespace dy.net.model.dto
{
    /// <summary>推送到飞书的一行视频记录(与Excel导出13列一致)。</summary>
    public class FeishuVideoRow
    {
        public long SyncTimeMs { get; set; }
        public long? CreateTimeMs { get; set; }
        public string SyncType { get; set; }
        public string Author { get; set; }
        public string VideoKind { get; set; }
        public string Title { get; set; }
        public string DyUser { get; set; }
        public long PlayCount { get; set; }
        public long DiggCount { get; set; }
        public long CommentCount { get; set; }
        public long ShareCount { get; set; }
        public long CollectCount { get; set; }
        public string Subtitle { get; set; }
    }

    /// <summary>推送结果。</summary>
    public class FeishuPushResult
    {
        public bool Success { get; set; }
        public string Message { get; set; } = string.Empty;
        public int Count { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
    }

    // ===== 飞书 API 信封(仅 FeishuBitableService 内部使用) =====
    internal class FeishuResp<T>
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("msg")] public string Msg { get; set; }
        [JsonPropertyName("data")] public T Data { get; set; }
    }
    internal class FeishuTokenData
    {
        [JsonPropertyName("tenant_access_token")] public string Token { get; set; }
        [JsonPropertyName("expire")] public int Expire { get; set; }
    }
    internal class FeishuAppData
    {
        [JsonPropertyName("app")] public FeishuAppInfo App { get; set; }
    }
    internal class FeishuAppInfo
    {
        [JsonPropertyName("app_token")] public string AppToken { get; set; }
    }
    internal class FeishuTableListData
    {
        [JsonPropertyName("items")] public List<FeishuTableInfo> Items { get; set; }
    }
    internal class FeishuTableInfo
    {
        [JsonPropertyName("table_id")] public string TableId { get; set; }
        [JsonPropertyName("name")] public string Name { get; set; }
    }
    internal class FeishuCreateTableData
    {
        [JsonPropertyName("table_id")] public string TableId { get; set; }
    }
    internal class FeishuRecordListData
    {
        [JsonPropertyName("items")] public List<FeishuRecordInfo> Items { get; set; }
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
        [JsonPropertyName("page_token")] public string PageToken { get; set; }
    }
    internal class FeishuRecordInfo
    {
        [JsonPropertyName("record_id")] public string RecordId { get; set; }
    }
}
```

- [ ] **Step 3: 编译验证**

Run: `cd /d/dysync && D:/dotnet-sdk/dotnet.exe build dysync.net/dy.net.csproj -v q 2>&1 | tail -3`
Expected: `0 个错误`（`0 Error(s)`）

- [ ] **Step 4: Commit**

```bash
cd /d/dysync/dysync.net && git add model/entity/AppConfig.cs model/dto/FeishuDtos.cs
git commit -m "feat: 飞书推送配置字段+DTO定义"
```

---

### Task 2: FeishuBitableService（飞书 API 客户端）

**Files:**
- Create: `D:\dysync\dysync.net\service\FeishuBitableService.cs`
- Modify: `D:\dysync\dysync.net\extension\ServiceExtension.cs`（AddHttpClients 内，L353-356 ASR 客户端注册后追加）

**Interfaces:**
- Consumes: Task 1 的 `AppConfig.Feishu*` 字段、`FeishuVideoRow`、`FeishuPushResult`、DTO 信封；`DouyinCommonService.GetConfig()/UpdateConfig(AppConfig)`（写 Base 缓存用）
- Produces: `FeishuBitableService.PushDailyAsync(AppConfig, List<FeishuVideoRow>) → Task<FeishuPushResult>`；常量 `FEISHU_HTTP_CLIENT = "feishu"`

- [ ] **Step 1: 注册命名 HttpClient**

`ServiceExtension.cs` AddHttpClients 中，ASR 客户端注册（L353-356）之后追加：

```csharp
            services.AddHttpClient(FeishuBitableService.FEISHU_HTTP_CLIENT, client =>
            {
                client.Timeout = TimeSpan.FromSeconds(30);
            });
```

文件顶部 using 区需有 `using dy.net.service;`（若没有则加）。

- [ ] **Step 2: 创建 FeishuBitableService.cs**

```csharp
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
                    if (body.Code == 1254291 && attempt < RETRY_DELAYS.Length)
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
```

注意：`EnsureOk` 里 `FeishuResp<object>` 当 data 缺失时反序列化 `Data` 为 null 不报错（System.Text.Json 容忍），无需调整。

- [ ] **Step 3: 编译验证**

Run: `cd /d/dysync && D:/dotnet-sdk/dotnet.exe build dysync.net/dy.net.csproj -v q 2>&1 | tail -3`
Expected: 0 错误。若报 `List<T>.Chunk` 不存在 → .NET 6 有 `Enumerable.Chunk`（.NET 6 新增），确认 using System.Linq（隐式全局 using 已开则无碍；若报错则在文件顶部加 `using System.Linq;`）。

- [ ] **Step 4: Commit**

```bash
cd /d/dysync/dysync.net && git add service/FeishuBitableService.cs extension/ServiceExtension.cs
git commit -m "feat: 飞书多维表格API客户端(token/Base/表/记录批量写入)"
```

---

### Task 3: FeishuNotifyService + 字幕读取工具抽取

**Files:**
- Create: `D:\dysync\dysync.net\service\FeishuNotifyService.cs`
- Create: `D:\dysync\dysync.net\utils\SubtitleTextReader.cs`
- Modify: `D:\dysync\dysync.net\Controllers\VideoController.cs`（L678-712 两个私有静态方法删除，L651 调用点改）

**Interfaces:**
- Produces: `FeishuNotifyService.SendAsync(AppConfig, string) → Task`；`SubtitleTextReader.ReadAsync(string subtitleFullPath) → Task<string>`（Task 4 消费）
- Consumes: 无（`SubtitleTextReader` 逻辑从 VideoController 原样搬移）

- [ ] **Step 1: 创建 SubtitleTextReader.cs**

```csharp
using System.Text;

namespace dy.net.utils
{
    /// <summary>读取视频字幕文本:优先同名 .txt(纯文本),退化 .srt。失败/超长截断(>32000),返回安全文本。
    /// 供 Excel 导出(VideoController)与飞书推送(FeishuPushService)共用。</summary>
    public static class SubtitleTextReader
    {
        public static async Task<string> ReadAsync(string subtitleFullPath)
        {
            try
            {
                string contentPath = subtitleFullPath;
                string textSibling = Path.ChangeExtension(subtitleFullPath, ".txt");
                if (File.Exists(textSibling))
                {
                    contentPath = textSibling;
                }
                if (!File.Exists(contentPath))
                {
                    return string.Empty;
                }
                var text = await ReadContentAsync(contentPath);
                return text.Length > 32000 ? text.Substring(0, 32000) + "…" : text;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static async Task<string> ReadContentAsync(string filePath)
        {
            try
            {
                return await File.ReadAllTextAsync(filePath, Encoding.UTF8);
            }
            catch (DecoderFallbackException)
            {
                return await File.ReadAllTextAsync(filePath, Encoding.Default);
            }
        }
    }
}
```

- [ ] **Step 2: VideoController 改用工具类**

删除 L678-712 的 `ReadSubtitleTextAsync` 与 `ReadSubtitleContentAsync` 两个私有静态方法；L651 调用点：

```csharp
try { subtitle = await SubtitleTextReader.ReadAsync(Path.GetFullPath(v.SubtitleSavePath)); }
catch { subtitle = string.Empty; }
```

确认 VideoController 顶部有 `using dy.net.utils;`（没有则加）。

- [ ] **Step 3: 创建 FeishuNotifyService.cs**

```csharp
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
```

注：`FeishuNotifyService` 与 `FeishuBitableService` 都在 `dy.net.service` 命名空间，会被 `AddServicesFromNamespace("dy.net.service")` 自动注册，无需手动加 DI。

- [ ] **Step 4: 编译验证**

Run: `cd /d/dysync && D:/dotnet-sdk/dotnet.exe build dysync.net/dy.net.csproj -v q 2>&1 | tail -3`
Expected: 0 错误（重点确认 VideoController 无 `ReadSubtitleTextAsync` 残留引用）

- [ ] **Step 5: Commit**

```bash
cd /d/dysync/dysync.net && git add service/FeishuNotifyService.cs utils/SubtitleTextReader.cs Controllers/VideoController.cs
git commit -m "feat: 飞书群机器人通知服务;字幕读取抽取为公共工具"
```

---

### Task 4: FeishuPushService 编排 + Quartz 任务接线

**Files:**
- Create: `D:\dysync\dysync.net\service\FeishuPushService.cs`
- Create: `D:\dysync\dysync.net\job\FeishuDailyPushJob.cs`
- Modify: `D:\dysync\dysync.net\extension\ServiceExtension.cs`（AddQuartzService L281 后）
- Modify: `D:\dysync\dysync.net\service\DouyinQuartzJobService.cs`（类内追加方法，放 `StartFollowJobOnceAsync` L242-245 之后）
- Modify: `D:\dysync\dysync.net\Controllers\ConfigController.cs`（ReStartJob L381-387）
- Modify: `D:\dysync\dysync.net\Program.cs`（L268-269 InitOrReStartAllJobs 调用后）

**Interfaces:**
- Consumes: `FeishuBitableService.PushDailyAsync`、`FeishuNotifyService.SendAsync`、`SubtitleTextReader.ReadAsync`、`DouyinCommonService.GetConfig/UpdateConfig`、`DouyinVideoService.GetAllAsync()`
- Produces: `FeishuPushService.RunDailyPushAsync() → Task<FeishuPushResult>`（Task 5 Controller 消费）；`DouyinQuartzJobService.InitFeishuPushJob(AppConfig) → Task`

- [ ] **Step 1: 创建 FeishuPushService.cs**

```csharp
using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.utils;
using Serilog;

namespace dy.net.service
{
    /// <summary>飞书推送编排:读当天同步记录(13列含字幕) → 写多维表格 → 群通知 → 结果落库。
    /// Quartz 任务与手动触发(FeishuController)共用本方法。</summary>
    public class FeishuPushService
    {
        private readonly DouyinCommonService commonService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly FeishuBitableService bitableService;
        private readonly FeishuNotifyService notifyService;

        public FeishuPushService(DouyinCommonService commonService, DouyinVideoService douyinVideoService,
            FeishuBitableService bitableService, FeishuNotifyService notifyService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.bitableService = bitableService;
            this.notifyService = notifyService;
        }

        public async Task<FeishuPushResult> RunDailyPushAsync()
        {
            var config = commonService.GetConfig();
            if (config == null || !config.FeishuPushEnabled)
                return new FeishuPushResult { Success = false, Message = "飞书推送未开启" };
            if (string.IsNullOrWhiteSpace(config.FeishuAppId) || string.IsNullOrWhiteSpace(config.FeishuAppSecret))
                return new FeishuPushResult { Success = false, Message = "飞书AppId/AppSecret未配置" };

            FeishuPushResult result;
            try
            {
                var all = await douyinVideoService.GetAllAsync();
                var today = all.Where(v => v.SyncTime >= DateTime.Today).OrderBy(v => v.SyncTime).ToList();
                var rows = new List<FeishuVideoRow>();
                foreach (var v in today)
                {
                    string subtitle = string.Empty;
                    if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath))
                    {
                        subtitle = await SubtitleTextReader.ReadAsync(Path.GetFullPath(v.SubtitleSavePath));
                    }
                    rows.Add(new FeishuVideoRow
                    {
                        SyncTimeMs = new DateTimeOffset(v.SyncTime).ToUnixTimeMilliseconds(),
                        CreateTimeMs = v.CreateTime != default ? new DateTimeOffset(v.CreateTime).ToUnixTimeMilliseconds() : (long?)null,
                        SyncType = v.ViedoType.GetDesc(),
                        Author = v.Author ?? string.Empty,
                        VideoKind = $"{v.Tag1 ?? string.Empty} {v.Tag2 ?? string.Empty} {v.Tag3 ?? string.Empty}".Trim(),
                        Title = v.VideoTitle ?? string.Empty,
                        DyUser = v.DyUser ?? string.Empty,
                        PlayCount = v.PlayCount ?? 0,
                        DiggCount = v.DiggCount ?? 0,
                        CommentCount = v.CommentCount ?? 0,
                        ShareCount = v.ShareCount ?? 0,
                        CollectCount = v.CollectCount ?? 0,
                        Subtitle = subtitle,
                    });
                }
                result = await bitableService.PushDailyAsync(config, rows);
                Log.Information("[feishu] 推送完成 {Count} 条", result.Count);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[feishu] 推送失败");
                result = new FeishuPushResult { Success = false, Message = ex.Message };
            }

            var stamp = $"{DateTime.Now.Month}月{DateTime.Now.Day}日";
            var text = result.Success
                ? $"{stamp}抖小云同步数据已推送 {result.Count} 条 → {result.BaseUrl}"
                : $"{stamp}抖小云推送失败:{result.Message}";
            await notifyService.SendAsync(config, text);

            config.FeishuLastPushResult = $"{DateTime.Now:yyyy-MM-dd HH:mm} " +
                (result.Success ? $"成功 {result.Count}条" : $"失败 {result.Message}");
            await commonService.UpdateConfig(config);
            return result;
        }
    }
}
```

- [ ] **Step 2: 创建 FeishuDailyPushJob.cs**

```csharp
using dy.net.service;
using Quartz;

namespace dy.net.job
{
    /// <summary>飞书每日推送任务(Quartz 调度入口,逻辑全在 FeishuPushService)。</summary>
    [DisallowConcurrentExecution]
    public class FeishuDailyPushJob : IJob
    {
        private readonly FeishuPushService pushService;

        public FeishuDailyPushJob(FeishuPushService pushService)
        {
            this.pushService = pushService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            await pushService.RunDailyPushAsync();
        }
    }
}
```

- [ ] **Step 3: 注册 Job + 调度方法**

`ServiceExtension.cs` AddQuartzService 中 L281 `services.AddScoped<DouyinSeriesSyncJob>();` 后追加：

```csharp
            services.AddScoped<FeishuDailyPushJob>();
```

`DouyinQuartzJobService.cs` 在 `StartFollowJobOnceAsync` 方法（L242-245）之后追加：

```csharp
        /// <summary>
        /// 初始化/刷新/移除飞书每日推送任务(独立于JobConfigs管理,key不属VideoTypeEnum)。
        /// 未开启或配置缺失时删除已存在的任务。
        /// </summary>
        public async Task InitFeishuPushJob(model.entity.AppConfig config)
        {
            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = new JobKey("feishu.job.key.daily_push", DefaultJobGroup);
            var triggerKey = new TriggerKey("feishu.trigger.key.daily_push", DefaultJobGroup);

            if (config == null || !config.FeishuPushEnabled)
            {
                if (await scheduler.CheckExists(jobKey))
                {
                    await scheduler.DeleteJob(jobKey);
                    Log.Information("【quartz】飞书推送已关闭,移除定时任务");
                }
                return;
            }

            var cron = !string.IsNullOrWhiteSpace(config.FeishuPushCron) && CronExpression.IsValidExpression(config.FeishuPushCron)
                ? config.FeishuPushCron
                : "0 50 23 * * ?";

            if (await scheduler.CheckExists(jobKey))
                await scheduler.DeleteJob(jobKey);

            var jobDetail = JobBuilder.Create<FeishuDailyPushJob>()
                .WithIdentity(jobKey)
                .WithDescription("飞书多维表格每日推送")
                .DisallowConcurrentExecution()
                .Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .WithCronSchedule(cron)
                .Build();
            await scheduler.ScheduleJob(jobDetail, trigger);
            Log.Information("【quartz】飞书推送任务已调度,cron={Cron}", cron);
        }
```

- [ ] **Step 4: ConfigController + Program.cs 接线**

`ConfigController.cs` ReStartJob（L381-387）改为：

```csharp
        private void ReStartJob()
        {
            var config = commonService.GetConfig();
            if (config != null)
            {
                quartzJobService.InitOrReStartAllJobs(config.Cron.ToString());
                _ = quartzJobService.InitFeishuPushJob(config);   // fire-and-forget,同上行不阻塞前端
            }
        }
```

`Program.cs` L268-269 附近：

```csharp
                        var quartzJobService = services.GetRequiredService<DouyinQuartzJobService>();
                        await quartzJobService.InitOrReStartAllJobs(config?.Cron <= 0 ? "30" : config.Cron.ToString());
                        await quartzJobService.InitFeishuPushJob(config);
```

- [ ] **Step 5: 编译验证**

Run: `cd /d/dysync && D:/dotnet-sdk/dotnet.exe build dysync.net/dy.net.csproj -v q 2>&1 | tail -3`
Expected: 0 错误。注意 `GetDesc()` 扩展在 `dy.net.utils`（VideoTypeEnum 扩展），FeishuPushService 已 using 该命名空间。

- [ ] **Step 6: Commit**

```bash
cd /d/dysync/dysync.net && git add service/FeishuPushService.cs job/FeishuDailyPushJob.cs extension/ServiceExtension.cs service/DouyinQuartzJobService.cs Controllers/ConfigController.cs Program.cs
git commit -m "feat: 飞书每日推送编排+Quartz任务调度接线"
```

---

### Task 5: FeishuController（手动推送 + 状态查询）

**Files:**
- Create: `D:\dysync\dysync.net\Controllers\FeishuController.cs`

**Interfaces:**
- Consumes: `FeishuPushService.RunDailyPushAsync()`、`DouyinCommonService.GetConfig()`
- Produces: `POST /api/feishu/push/today`（[Authorize]，返回 FeishuPushResult）、`GET /api/feishu/status`（[Authorize]，返回 FeishuLastPushResult）——Task 6 前端消费

- [ ] **Step 1: 创建 FeishuController.cs**

```csharp
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
```

- [ ] **Step 2: 编译 + 接口冒烟（本地起服务可选，仅编译即可）**

Run: `cd /d/dysync && D:/dotnet-sdk/dotnet.exe build dysync.net/dy.net.csproj -v q 2>&1 | tail -3`
Expected: 0 错误。确认 `ApiResult.Success` 在 `dy.net.utils`（与 FollowController 用法一致）。

- [ ] **Step 3: Commit**

```bash
cd /d/dysync/dysync.net && git add Controllers/FeishuController.cs
git commit -m "feat: 飞书推送手动触发/状态查询接口"
```

---

### Task 6: 前端设置页「飞书推送」区块

**Files:**
- Modify: `D:\dysync\dysync.net\app\src\pages\set\AppSet.vue`（模板 L208「视频统计」表单项后、interface FormState L395 后、formState 默认值 L425 后、getConfig 映射 L505 后；script 加推送方法）
- Modify: `D:\dysync\dysync.net\app\src\store\coreapi.ts`（GetAsrHealth 附近加两个 API 并 export）

**Interfaces:**
- Consumes: `POST /api/feishu/push/today`、`GET /api/feishu/status`；配置字段走现有 `apiGetConfig/apiUpdateConfig`（camelCase 自动映射）
- Produces: 设置页可操作完整闭环

- [ ] **Step 1: coreapi.ts 加 API**

在 `GetAsrHealth` 函数后追加，并加入 return 导出列表：

```ts
  async function FeishuPushToday() {
    return http.request<any, Response<any>>('/api/feishu/push/today', 'post_json', {}).then(r => {
      return r.data
    })
  }
  async function GetFeishuStatus() {
    return http.request<any, Response<any>>('/api/feishu/status', 'get').then(r => {
      return r.data
    })
  }
```

导出列表（return 对象）中追加 `FeishuPushToday, GetFeishuStatus,`。

- [ ] **Step 2: AppSet.vue 模板加区块**

在「视频统计」表单项（L205-208）之后插入：

```html
        <a-form-item label="飞书推送" name="FeishuPushEnabled">
          <a-switch v-model:checked="formState.FeishuPushEnabled" />
          <span style="margin-left: 8px; color: #888; font-size: 12px">每天定时把同步记录推送到飞书多维表格</span>
        </a-form-item>
        <template v-if="formState.FeishuPushEnabled">
          <a-form-item label="App ID" name="FeishuAppId">
            <a-input v-model:value="formState.FeishuAppId" placeholder="飞书自建应用 app_id" />
          </a-form-item>
          <a-form-item label="App Secret" name="FeishuAppSecret">
            <a-input-password v-model:value="formState.FeishuAppSecret" placeholder="飞书自建应用 app_secret" />
          </a-form-item>
          <a-form-item label="飞书邮箱" name="FeishuUserEmail">
            <a-input v-model:value="formState.FeishuUserEmail" placeholder="新建多维表格后加你为协作者" />
          </a-form-item>
          <a-form-item label="群机器人webhook" name="FeishuNotifyWebhook">
            <a-input v-model:value="formState.FeishuNotifyWebhook" placeholder="推送结果通知(可空)" />
          </a-form-item>
          <a-form-item label="文件夹token" name="FeishuFolderToken">
            <a-input v-model:value="formState.FeishuFolderToken" placeholder="月度表格存放位置(可空,空则应用根空间)" />
          </a-form-item>
          <a-form-item label="推送cron" name="FeishuPushCron">
            <a-input v-model:value="formState.FeishuPushCron" placeholder="默认 0 50 23 * * ?(每天23:50)" style="width: 220px" />
          </a-form-item>
          <a-form-item label="推送状态">
            <a-space>
              <span>{{ feishuLastResult || '尚未推送' }}</span>
              <a-button size="small" :loading="feishuPushLoading" @click="handleFeishuPushToday">立即推送今天</a-button>
            </a-space>
          </a-form-item>
        </template>
```

- [ ] **Step 3: FormState 接口 + 默认值 + 加载映射**

interface FormState（L395 `AsrOverwriteExisting: boolean;` 后）追加：

```ts
  FeishuPushEnabled: boolean;
  FeishuAppId: string;
  FeishuAppSecret: string;
  FeishuUserEmail: string;
  FeishuNotifyWebhook: string;
  FeishuFolderToken: string;
  FeishuPushCron: string;
```

formState 默认值（L425 `AsrOverwriteExisting: false` 后）追加：

```ts
  FeishuPushEnabled: false,
  FeishuAppId: '',
  FeishuAppSecret: '',
  FeishuUserEmail: '',
  FeishuNotifyWebhook: '',
  FeishuFolderToken: '',
  FeishuPushCron: ''
```

getConfig 的 Object.assign（L505 `AsrOverwriteExisting: ...` 后）追加：

```ts
          FeishuPushEnabled: res.data.feishuPushEnabled || false,
          FeishuAppId: res.data.feishuAppId || '',
          FeishuAppSecret: res.data.feishuAppSecret || '',
          FeishuUserEmail: res.data.feishuUserEmail || '',
          FeishuNotifyWebhook: res.data.feishuNotifyWebhook || '',
          FeishuFolderToken: res.data.feishuFolderToken || '',
          FeishuPushCron: res.data.feishuPushCron || ''
```

同一 then 块内（`checkAsrHealth();` L509 附近）追加 `loadFeishuStatus();`。

- [ ] **Step 4: script 加状态与方法**

在 `checkAsrHealth` 方法附近（script setup 区）追加：

```ts
const feishuLastResult = ref('');
const feishuPushLoading = ref(false);
const loadFeishuStatus = () => {
  useApiStore().GetFeishuStatus().then((res) => {
    if (res.code === 0) feishuLastResult.value = res.data?.lastResult || '';
  }).catch(() => {});
};
const handleFeishuPushToday = () => {
  feishuPushLoading.value = true;
  useApiStore().FeishuPushToday().then((res) => {
    if (res.code === 0 && res.data?.success) {
      message.success(res.data.message || '推送成功');
    } else {
      message.error(res.data?.message || res.message || '推送失败');
    }
    loadFeishuStatus();
  }).catch((e) => {
    console.error('飞书推送失败:', e);
    message.error('推送请求失败');
  }).finally(() => {
    feishuPushLoading.value = false;
  });
};
```

确认 script 顶部已 import `ref`（已有，文件大量使用）。

- [ ] **Step 5: 前端构建验证**

Run: `cd /d/dysync/dysync.net/app && npm run build 2>&1 | tail -5`
Expected: `vue-tsc --noEmit` 无类型错误，vite build 成功产出 dist（约 20-30s）

- [ ] **Step 6: Commit**

```bash
cd /d/dysync/dysync.net && git add app/src/pages/set/AppSet.vue app/src/store/coreapi.ts
git commit -m "feat: 设置页飞书推送配置区块+立即推送按钮"
```

---

### Task 7: 用户准备文档（飞书自建应用配置指南）

**Files:**
- Create: `D:\dysync\dysync.net\docs\feishu-app-setup.md`

- [ ] **Step 1: 写文档**

内容（完整写入文件）：

````markdown
# 飞书自建应用配置指南（一次性）

> 目标：拿到 app_id / app_secret / 群机器人 webhook / 文件夹 token，填进抖小云设置页「飞书推送」。

## 1. 创建自建应用
1. 浏览器打开 [飞书开发者后台](https://open.feishu.cn/app) → 「创建企业自建应用」
2. 名称填「抖小云推送」，描述随意，创建后进入应用详情

## 2. 开通权限（scope）
「权限管理」→ 搜索并开通：
- **多维表格**：查看、评论、编辑和管理多维表格（`bitable:app`）
- **云文档**：查看、评论、编辑和管理云空间中所有文件（`drive:drive`）+ 云文档权限设置（`drive:drive:permissions`）

## 3. 发布版本
「版本管理与发布」→ 创建版本 → 提交发布（企业自建应用一般自动通过）
→ 应用详情页「凭证与基础信息」复制 **App ID** 和 **App Secret**

## 4.（可选）准备存放文件夹
1. 飞书 → 云文档 → 我的空间 → 新建文件夹「抖小云数据」
2. 打开文件夹 → 右上「...」→ 共享/协作者 → 添加协作者 → 搜索应用名「抖小云推送」→ 可编辑
3. 浏览器地址栏 `https://xxx.feishu.cn/drive/folder/fldcnXXXXXXXX`，`folder/` 后那串即 folder_token
（不配则月度表格建在应用根空间，功能不受影响）

## 5. 群机器人（推送结果通知）
1. 飞书群 → 群设置 → 群机器人 → 添加机器人 → 自定义机器人
2. 复制 webhook 地址（`https://open.feishu.cn/open-apis/bot/v2/hook/xxx`）

## 6. 填进抖小云
设置页 → 其他配置 → 打开「飞书推送」→ 填入 App ID / App Secret / 飞书邮箱 / webhook / 文件夹 token → 保存 → 点「立即推送今天」验收
````

- [ ] **Step 2: Commit**

```bash
cd /d/dysync/dysync.net && git add docs/feishu-app-setup.md
git commit -m "docs: 飞书自建应用一次性配置指南"
```

---

### Task 8: 部署到容器镜像 + E2E 验证

**Files:**
- 构建产物（不入 git）

**前置：** 用户已完成 Task 7 文档里的飞书侧准备（app_id/secret/webhook 在手）。

- [ ] **Step 1: 后端发布**

```bash
cd /d/dysync && D:/dotnet-sdk/dotnet.exe publish dysync.net/dy.net.csproj -c Release -r linux-x64 --self-contained false -o build-context/pub-feishu
```
Expected: 无错误，产出 `build-context/pub-feishu/dy.net.dll`

- [ ] **Step 2: 前端构建**

```bash
cd /d/dysync/dysync.net/app && npm run build
```
Expected: 产出 `app/dist`（与 build-context 无关，cp 时直接从 app/dist 拷）

- [ ] **Step 3: 备份 + docker cp 进容器**

```bash
docker exec dysync2026 sh -c 'cp /app/dy.net.dll /app/dy.net.dll.bak-feishu 2>/dev/null; true'
docker cp build-context/pub-feishu/dy.net.dll dysync2026:/app/dy.net.dll
docker cp dysync.net/app/dist dysync2026:/app/app/dist
```

- [ ] **Step 4: 展平 dist 嵌套（固定陷阱解法）**

```bash
docker exec dysync2026 sh -c 'cd /app/app/dist && if [ -d dist ]; then cp -r dist/* ./ && rm -rf dist; fi && ls /app/app/dist/index.html && ls /app/app/dist/assets/ | head -3'
```
Expected: `index.html` 在顶层、assets 下有 js chunk（**不带这步前端会 404**——Windows docker cp 会把内容放进 `/app/app/dist/dist`）

- [ ] **Step 5: commit 镜像 + 重建容器**

```bash
docker commit dysync2026 dysync:asr-local
docker compose up -d --force-recreate
docker inspect dysync2026 --format '{{.Image}}'
docker inspect dysync:asr-local --format '{{.Id}}'
```
Expected: 两个 ID 一致（restart≠recreate，必须 force-recreate）

- [ ] **Step 6: 冒烟验证（无需飞书配置的部分）**

```bash
# 1. 前端 200 + 新 chunk 含飞书字样
curl -s -o /dev/null -w "%{http_code}" http://localhost:10101/
curl -s http://localhost:10101/ | grep -o 'src="[^"]*index[^"]*\.js"' | head -1
# 2. 新配置列已进 sqlite（宿主直接查挂载的库文件）
"C:/Users/admin/miniconda3/envs/asr/python.exe" -c "import sqlite3; print([r[1] for r in sqlite3.connect('D:/dysync/data/db/dy.sqlite').execute('PRAGMA table_info(dy_app_config)') if 'eishu' in r[1]])"
```
Expected: 前端 200；sqlite 输出含 `['FeishuPushEnabled', 'FeishuAppId', ...]` 共 10 列（CodeFirst 启动自动加列；若缺列则手工 ALTER 并记录到 video-stats-fields 同款注意事项）

- [ ] **Step 7: E2E（需用户已在设置页填好飞书配置）**

1. 设置页打开「飞书推送」填配置 → 保存 → 点「立即推送今天」
2. 飞书确认：月度 Base「抖小云同步数据-2026年8月」出现、内有「M月d日」表、13 列类型正确、行数 = 当天同步数、字幕列有内容
3. 再点一次「立即推送今天」→ 行数不变（幂等验证）
4. 群消息收到「N 条 → 链接」
5. （用户已登录 lark-cli 时）`lark-cli base +record-list --base-token <token> --table-id <id> --limit 3` 抽查 3 条交叉验证
6. 检查 Quartz 调度：`docker logs dysync2026 2>&1 | grep 飞书` 应有「飞书推送任务已调度,cron=...」

- [ ] **Step 8: Commit（如有验证期代码修复）+ 更新记忆**

无代码变更则跳过 commit。把「飞书推送已上线、镜像已更新、配置方式、Quartz 调度检查方法」更新到项目记忆（新 memory 文件或并入 dysync-deployment）。

---

## Self-Review 记录

- **Spec 覆盖**：§3 配置项→Task 1/6；§4 表结构→Task 2 Step 2（13 字段）；§5.1 Base 定位→EnsureMonthlyBaseAsync；§5.2 清空重写→EnsureDailyTableAsync+ClearTableAsync；§5.3 批量/退避→BatchCreateAsync；§5.4 通知→Task 3；§5.5 token→GetTenantTokenAsync（含失效重试由外层异常→编排层失败通知兜底）；§6 错误处理→各层 try/通知；§7 验证→Task 8 Step 6/7；§8 准备清单→Task 7；§9 部署→Task 8；§10 范围外未纳入 ✓
- **类型一致性**：`PushDailyAsync/AppConfig/FeishuVideoRow/FeishuPushResult/RunDailyPushAsync/InitFeishuPushJob/ReadAsync/SendAsync` 跨 Task 签名一致；`FEISHU_HTTP_CLIENT` 定义于 Task 2、Task 3 引用 ✓
- **占位符**：无 TBD/TODO；所有代码步骤含完整代码 ✓
