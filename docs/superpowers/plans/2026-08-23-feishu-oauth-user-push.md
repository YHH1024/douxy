# 飞书 OAuth 用户授权推送 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 飞书推送支持以用户身份（user_access_token）执行，多维表格直接建在用户自己的文件夹里。

**Architecture:** OAuth authorization_code 自动回调拿 token，落库 4 个新自管列；`FeishuBitableService` 的 token 层改为「用户身份优先、自动刷新、过期抛明确错误」，建 Base 走用户 FolderToken；无用户 token 时完整回落现有应用身份行为。

**Tech Stack:** .NET 6（现有 dy.net）、Vue3+AntD（AppSet.vue）、飞书 OAuth v2 端点（`/open-apis/authen/v2/oauth/token`）。

**Spec:** `docs/superpowers/specs/2026-08-23-feishu-oauth-user-push-design.md`

## Global Constraints

- 项目**无测试基建**：每任务的验证 = `dotnet publish` 编译零 error + 按需部署容器 + curl 冒烟（登录路由 `POST /api/Auth/Login`，返回顶层 `token`）
- 部署速查（后端改动）：`D:/dotnet-sdk/dotnet.exe publish dysync.net/dy.net.csproj -c Release -r linux-x64 --self-contained false -o D:/dysync/build-context/pub` → `docker cp D:/dysync/build-context/pub/dy.net.dll dysync2026:/app/dy.net.dll` → `docker commit dysync2026 dysync:asr-local` → `docker compose up -d --force-recreate`（cwd=D:/dysync）。**改 DLL 后必须 recreate，restart 不加载新镜像**
- 前端改动部署：`cd D:/dysync/dysync.net/app && npm run build` → docker cp dist（**嵌套陷阱**：cp 后检查并展平 `cd /app/app/dist && rm -rf assets index.html logo.png && cp -r dist/* ./ && rm -rf dist`）→ commit → recreate。容器内 `docker exec` 用 `//app/...` 双斜杠防 Git Bash 路径转换
- **程序自管配置列必须加进 `ConfigController.UpdateConfig` 回填清单**（历史坑：全列覆盖冲掉缓存导致重复建 Base）
- 飞书 token 响应是**扁平结构**（code/access_token 在顶层，无 data 包裹），与通用信封 FeishuResp 不同
- refresh_token **一次性**：刷新返回的新值必须立即落库
- DB 检查统一用：`PYTHONIOENCODING=utf-8 python -c "import sqlite3; ..."` 连 `D:/dysync/data/db/dy.sqlite` 表 `dy_app_config`（单行列结构）
- 每任务完成即 git commit（cwd=D:/dysync/dysync.net，分支 asr-windows-test）

---

### Task 1: AppConfig 实体 + UpdateConfig 回填（4 个新列）

**Files:**
- Modify: `dysync.net/model/entity/AppConfig.cs`（FeishuLastPushResult 字段后追加）
- Modify: `dysync.net/Controllers/ConfigController.cs`（UpdateConfig 回填块）

**Interfaces:**
- Produces: 实体属性 `FeishuUserAccessToken / FeishuUserRefreshToken / FeishuUserTokenExpiresAt / FeishuUserRefreshExpiresAt`（string×2、DateTime?×2），后续任务直接引用这些属性名

- [ ] **Step 1: AppConfig 加 4 个属性**

在 `FeishuAutoFolderToken` 属性之后（`} }` 前）追加：

```csharp
        /// <summary>用户授权token(OAuth user_access_token,程序自管理)</summary>
        [SugarColumn(Length = 4000, IsNullable = true, ColumnDataType = "TEXT")]
        public string FeishuUserAccessToken { get; set; }
        /// <summary>用户授权刷新token(一次性,每次刷新后更新,程序自管理)</summary>
        [SugarColumn(Length = 4000, IsNullable = true, ColumnDataType = "TEXT")]
        public string FeishuUserRefreshToken { get; set; }
        /// <summary>用户授权token过期时刻(程序自管理)</summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? FeishuUserTokenExpiresAt { get; set; }
        /// <summary>用户授权刷新token过期时刻(程序自管理)</summary>
        [SugarColumn(IsNullable = true)]
        public DateTime? FeishuUserRefreshExpiresAt { get; set; }
```

注意：SqlSugar 对 sqlite 的 `Length=4000` 可能生成 varchar(4000)；显式 `ColumnDataType = "TEXT"` 保证长 token（1~2KB）存得下。

- [ ] **Step 2: UpdateConfig 回填清单扩到 8 项**

`ConfigController.cs` 的 `UpdateConfig` 方法里，现有回填块（`config.FeishuLastPushResult = current.FeishuLastPushResult;` 之后）追加：

```csharp
                config.FeishuUserAccessToken = current.FeishuUserAccessToken;
                config.FeishuUserRefreshToken = current.FeishuUserRefreshToken;
                config.FeishuUserTokenExpiresAt = current.FeishuUserTokenExpiresAt;
                config.FeishuUserRefreshExpiresAt = current.FeishuUserRefreshExpiresAt;
```

同步更新注释「回填这4个」→「回填这8个」。

- [ ] **Step 3: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 4: 部署并验证 CodeFirst 自动建列**

部署（见 Global Constraints 速查）后：

```bash
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
cols = [r[1] for r in con.execute('PRAGMA table_info(dy_app_config)')]
need = ['FeishuUserAccessToken','FeishuUserRefreshToken','FeishuUserTokenExpiresAt','FeishuUserRefreshExpiresAt']
print('缺失:', [c for c in need if c not in cols] or '无,全部存在')
con.close()"
```
Expected: `缺失: 无,全部存在`（容器启动后 CodeFirst 自动 ALTER）

- [ ] **Step 5: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): AppConfig增加OAuth用户token4列+UpdateConfig回填防冲"
```

---

### Task 2: OAuth DTO + token 层（获取/刷新/身份选择）

**Files:**
- Modify: `dysync.net/model/dto/FeishuDtos.cs`（文件末尾追加）
- Modify: `dysync.net/service/FeishuBitableService.cs`（token 区）

**Interfaces:**
- Consumes: Task 1 的 4 个 AppConfig 属性
- Produces: `FeishuBitableService` 公有方法 `Task<string> ExchangeCodeAsync(AppConfig config, string code, string redirectUri)`（授权回调用，返回用户 access_token 并落库）、`Task<Uri> BuildAuthorizeUrlAsync(AppConfig config, string redirectUri, string state)`（生成授权链接）、`bool HasUserAuth(AppConfig config)`、私有 `GetUserAccessTokenAsync(AppConfig config)`（含刷新）、`AuthedClientAsync` 行为变更为用户优先

- [ ] **Step 1: DTO 追加**

`FeishuDtos.cs` 末尾（最后一个类后）追加：

```csharp
    /// <summary>OAuth user_access_token 响应(扁平结构,无data包裹;refresh字段仅授予offline_access时返回)。</summary>
    internal class FeishuOAuthTokenResp
    {
        [JsonPropertyName("code")] public int Code { get; set; }
        [JsonPropertyName("access_token")] public string AccessToken { get; set; }
        [JsonPropertyName("expires_in")] public int? ExpiresIn { get; set; }
        [JsonPropertyName("refresh_token")] public string RefreshToken { get; set; }
        [JsonPropertyName("refresh_token_expires_in")] public int? RefreshExpiresIn { get; set; }
        [JsonPropertyName("error")] public string Error { get; set; }
        [JsonPropertyName("error_description")] public string ErrorDescription { get; set; }
    }
```

- [ ] **Step 2: FeishuBitableService token 区改造**

在 `GetTenantTokenAsync` 方法前插入三个方法，并修改 `AuthedClientAsync`：

```csharp
        /// <summary>是否已有有效的用户授权(refresh_token 未过期即视为已授权,access 可刷新)。</summary>
        public bool HasUserAuth(AppConfig config)
            => !string.IsNullOrWhiteSpace(config.FeishuUserRefreshToken)
               && config.FeishuUserRefreshExpiresAt.HasValue
               && config.FeishuUserRefreshExpiresAt.Value > DateTime.Now;

        /// <summary>构造飞书用户授权页链接。scope 含 offline_access 才会返回 refresh_token。</summary>
        public Task<Uri> BuildAuthorizeUrlAsync(AppConfig config, string redirectUri, string state)
        {
            var scope = Uri.EscapeDataString("bitable:app drive:drive offline_access");
            var redirect = Uri.EscapeDataString(redirectUri);
            var url = $"{FEISHU_HOST}/open-apis/authen/v1/index?app_id={config.FeishuAppId}&redirect_uri={redirect}&scope={scope}&state={state}";
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
            return body.AccessToken;
        }

        /// <summary>落库 token 与过期时刻,并清 Base 缓存(切身份后旧 Base 不再复用)。</summary>
        private async Task SaveUserTokensAsync(AppConfig config, FeishuOAuthTokenResp body)
        {
            config.FeishuUserAccessToken = body.AccessToken;
            config.FeishuUserTokenExpiresAt = DateTime.Now.AddSeconds((body.ExpiresIn ?? 7200) - 300);
            if (!string.IsNullOrEmpty(body.RefreshToken))
            {
                config.FeishuUserRefreshToken = body.RefreshToken;
                config.FeishuUserRefreshExpiresAt = DateTime.Now.AddSeconds(body.RefreshExpiresIn ?? 604800);
            }
            config.FeishuBaseTokenCache = null;
            config.FeishuBaseMonthCache = null;
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
```

注意 `SaveUserTokensAsync` 里清 Base 缓存对刷新路径同样正确（刷新不应清缓存——见 Step 3 修正）。

- [ ] **Step 3: 修正——刷新路径不能清 Base 缓存**

Step 2 的 `SaveUserTokensAsync` 在**刷新**时也会清 `FeishuBaseTokenCache`，导致每天推送都重建 Base。修正：把清缓存逻辑从 `SaveUserTokensAsync` 挪到 `ExchangeCodeAsync` 尾部：

```csharp
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
```

`ExchangeCodeAsync` 在 `SaveUserTokensAsync(config, body)` 之后、`return` 之前加：

```csharp
            config.FeishuBaseTokenCache = null;
            config.FeishuBaseMonthCache = null;
            await commonService.UpdateConfig(config);
```

（两次 UpdateConfig 可接受——授权是低频交互操作，不值得合并优化）

- [ ] **Step 4: AuthedClientAsync 用户优先**

把现有 `AuthedClientAsync` 整体替换为：

```csharp
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
```

- [ ] **Step 5: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 6: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): OAuth用户token层——授权码交换/自动刷新/用户身份优先"
```

---

### Task 3: 建 Base 逻辑按身份分流

**Files:**
- Modify: `dysync.net/service/FeishuBitableService.cs`（`EnsureMonthlyBaseAsync`）

**Interfaces:**
- Consumes: Task 2 的 `HasUserAuth`
- Produces: 用户身份时 Base 强制建在 `config.FeishuFolderToken`；应用身份行为不变（EnsureAutoFolderAsync + 链接分享）

- [ ] **Step 1: 文件夹解析与分享逻辑分流**

`EnsureMonthlyBaseAsync` 中，把现在的文件夹解析块：

```csharp
            // Base 存放文件夹:用户配置的 FolderToken 优先;未配置则用应用自建的专属文件夹「抖小云同步数据」
            // (个人版飞书用户自己的文件夹加不了应用协作者写不进,自建文件夹是唯一可写的集中存放处)
            var folderToken = config.FeishuFolderToken;
            if (string.IsNullOrWhiteSpace(folderToken))
            {
                folderToken = await EnsureAutoFolderAsync(client, config);
                Log.Information("[feishu] 月度Base将建在专属文件夹 {Folder}", folderToken);
            }
```

替换为：

```csharp
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
```

- [ ] **Step 2: 链接分享块加身份条件**

「组织内链接可编辑」的 try 块整体包进 `if (!HasUserAuth(config)) { ... }`（用户身份下归属本人，无需分享）。加协作者的 `if (!string.IsNullOrWhiteSpace(config.FeishuUserEmail))` 块同样包进 `if (!HasUserAuth(config))`。缩进调整后确保编译通过。

- [ ] **Step 3: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 4: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): 建Base按身份分流——用户身份强制用户文件夹,跳过分享/协作者"
```

---

### Task 4: OAuth 端点（url/callback/status 扩展）

**Files:**
- Modify: `dysync.net/Controllers/FeishuController.cs`

**Interfaces:**
- Consumes: Task 2 的 `BuildAuthorizeUrlAsync / ExchangeCodeAsync / HasUserAuth`
- Produces: `GET api/feishu/oauth/url`→`{data:{url}}`；`GET api/feishu/oauth/callback?code&state`→HTML 结果页（AllowAnonymous）；`GET api/feishu/status` 响应新增 `oauth:{authorized,userTokenExpiresAt,refreshExpiresAt}`

- [ ] **Step 1: 加 using 与 state 字典**

文件头 using 区追加 `using System.Collections.Concurrent;`、`using System.Text;`。类内加静态字段与常量：

```csharp
        /// <summary>OAuth state 防 CSRF:发起时记录,回调时校验并移除(一次性)。容器重启丢失可接受(授权是交互式流程)。</summary>
        private static readonly ConcurrentDictionary<string, DateTime> _oauthStates = new();
```

- [ ] **Step 2: 授权链接端点**

```csharp
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
```

注意 redirectUri 用**实际请求的 Host**（localhost 或 LAN IP 都支持，无需多条配置——但飞书后台的重定向 URL 列表需含对应条目，首次用 localhost 即可）。

- [ ] **Step 3: 回调端点**

```csharp
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
            var html = $@"<!DOCTYPE html><html><head><meta charset=""utf-8""><title>抖小云飞书授权</title></head>
<body style=""font-family:system-ui;padding:40px;text-align:center"">
<h2>{title}</h2><p style=""color:#555"">{detail}</p>
<p style=""color:#999;font-size:13px"">本页面可关闭</p></body></html>";
            return Content(html, "text/html", Encoding.UTF8);
        }
```

- [ ] **Step 4: status 扩展**

现有 `Status()` 的返回对象扩展：

```csharp
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
```

- [ ] **Step 5: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 6: 部署 + 冒烟（oauth/url 未授权路径）**

部署后（速查见 Global Constraints）：

```bash
TOKEN=$(curl -s -X POST http://localhost:10101/api/Auth/Login -H "Content-Type: application/json" -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
curl -s "http://localhost:10101/api/feishu/oauth/url" -H "Authorization: Bearer $TOKEN" | python -m json.tool
curl -s "http://localhost:10101/api/feishu/status" -H "Authorization: Bearer $TOKEN" | python -m json.tool
```
Expected: `/oauth/url` 返回 `data.url` 以 `https://open.feishu.cn/open-apis/authen/v1/index?app_id=cli_...` 开头；`/status` 返回 `oauth.authorzed=false`（注意序列化大小写，实际看输出）

再测 state 校验负路径（期望 HTML「state 校验不通过」）：

```bash
curl -s "http://localhost:10101/api/feishu/oauth/callback?code=x&state=fake" | head -c 200
```

- [ ] **Step 7: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): OAuth授权url/callback端点+status暴露授权状态"
```

---

### Task 5: 前端——授权按钮与状态显示

**Files:**
- Modify: `dysync.net/app/src/store/coreapi.ts`（GetFeishuStatus 后追加）
- Modify: `dysync.net/app/src/pages/set/AppSet.vue`（飞书推送区，约 210-250 行）

**Interfaces:**
- Consumes: Task 4 的 `/api/feishu/oauth/url`、扩展后的 `/status`
- Produces: 设置页「授权飞书账号」按钮（新窗口开授权页）+ 授权状态行

- [ ] **Step 1: coreapi.ts 加 API 方法**

在 `FeishuTestConnection` 函数后追加：

```typescript
  async function FeishuOauthUrl() {
    return http.request<any, Response<any>>('/api/feishu/oauth/url', 'get').then(r => {
      return r;
    }).finally(() => {
    });
  }
```

并在文件底部 return 对象的 `FeishuTestConnection,` 后加一行 `FeishuOauthUrl,`。

- [ ] **Step 2: AppSet.vue 模板加授权区**

在「文件夹token」form-item（约 227-229 行）之后、「推送时间」之前插入：

```html
          <a-form-item label="账号授权" name="FeishuOauth">
            <a-space direction="vertical" :size="4" style="width: 100%">
              <a-space>
                <a-button size="small" :loading="feishuOauthLoading" @click="handleFeishuOauth">授权飞书账号</a-button>
                <span v-if="feishuOauthInfo.authorized" style="color: #52c41a; font-size: 13px">
                  已授权（刷新凭证至 {{ feishuOauthInfo.refreshExpiresAt }}）
                </span>
                <span v-else style="color: #999; font-size: 13px">未授权（推送将以应用身份执行，表格建在应用空间）</span>
              </a-space>
              <span style="color: #999; font-size: 12px">
                授权后推送以你的身份执行，多维表格直接建在上面的「文件夹token」文件夹里。首次使用需在飞书开发者后台→安全设置→重定向 URL 添加：
                http://localhost:10101/api/feishu/oauth/callback
              </span>
            </a-space>
          </a-form-item>
```

- [ ] **Step 3: AppSet.vue script 加状态与方法**

在 `feishuTestItems` 声明附近追加：

```typescript
const feishuOauthLoading = ref(false);
const feishuOauthInfo = ref<{ authorized: boolean; refreshExpiresAt?: string }>({ authorized: false });
const handleFeishuOauth = () => {
  feishuOauthLoading.value = true;
  useApiStore().FeishuOauthUrl().then((res: any) => {
    if (res?.data?.url) window.open(res.data.url, '_blank');
    else message.error(res?.message || '生成授权链接失败');
  }).finally(() => {
    feishuOauthLoading.value = false;
  });
};
```

（`message` 若该文件未引入 antd 的 message，检查现有 import；没有则用 `import { message } from 'ant-design-vue';` 或改用已有的提示方式——先 grep 确认）

现有 `loadFeishuStatus()` 的 then 分支里追加授权信息解析（原逻辑保留）：

```typescript
        const oauth = res.data?.oauth;
        feishuOauthInfo.value = {
          authorized: !!(oauth && oauth.authorized),
          refreshExpiresAt: oauth && oauth.refreshExpiresAt ? String(oauth.refreshExpiresAt).replace('T', ' ').slice(0, 16) : undefined
        };
```

- [ ] **Step 4: 前端构建验证**

```bash
cd D:/dysync/dysync.net/app && npm run build 2>&1 | tail -3
```
Expected: vite build 无 error（vue-tsc 通过）

- [ ] **Step 5: 部署前端 + 浏览器冒烟**

按 Global Constraints 前端部署速查（注意 dist 展平陷阱）。部署后浏览器打开 http://localhost:10101 → 设置 → 飞书推送区应显示「授权飞书账号」按钮与「未授权」状态文案。

- [ ] **Step 6: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): 设置页授权按钮+授权状态显示"
```

---

### Task 6: E2E——授权 + 用户文件夹推送 + 旧数据清理

**Files:** 无代码改动（操作与验证）

**Interfaces:**
- Consumes: 全部前置任务

- [ ] **Step 1: 用户操作——飞书后台配重定向 URL**

用户在浏览器：[飞书开发者后台](https://open.feishu.cn/app) → 应用 `cli_aaf731644f385d23` → 开发配置 → **安全设置** → 重定向 URL → 添加 `http://localhost:10101/api/feishu/oauth/callback` → 保存。（**可能需要新建版本发布**才生效，与开权限时同理）

- [ ] **Step 2: 用户操作——设置页授权**

设置页 → 飞书推送 → 确认「文件夹token」= `JQcsfjNxel3SsYdpmAscGM3FnZf`（用户原「抖小云数据」文件夹；空则填入）→ 点「授权飞书账号」→ 新窗口同意授权 → 回调页显示「授权成功」。

- [ ] **Step 3: 验证落库与缓存清空**

```bash
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
r = con.execute('SELECT length(FeishuUserAccessToken), length(FeishuUserRefreshToken), FeishuUserTokenExpiresAt, FeishuUserRefreshExpiresAt, FeishuBaseTokenCache FROM dy_app_config LIMIT 1').fetchone()
print('access长度:', r[0], '| refresh长度:', r[1], '| access过期:', r[2], '| refresh过期:', r[3], '| Base缓存:', r[4])
con.close()"
```
Expected: token 长度 >0，Base缓存=None

- [ ] **Step 4: 手动推送验证用户文件夹落位**

临时造一条今日数据（同 8/22 验证法）→ 设置页「立即推送今天」或 curl `POST /api/feishu/push/today` → 用户打开自己的「抖小云数据」文件夹确认出现「抖小云同步数据-2026年8月」Base 且归属为自己 → API 读回行数据正确：

```bash
# 临时造数据(测完还原)
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
row = con.execute(\"SELECT Id,SyncTime FROM dy_collect_video WHERE SubtitleSavePath!='' ORDER BY SyncTime DESC LIMIT 1\").fetchone()
con.execute('UPDATE dy_collect_video SET SyncTime=? WHERE Id=?', ('2026-08-23 12:00:00', row[0])); con.commit()
print('已临时改:', row[0], '原:', row[1]); con.close()"
# 推送
TOKEN=$(curl -s -X POST http://localhost:10101/api/Auth/Login -H "Content-Type: application/json" -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
curl -s -X POST http://localhost:10101/api/feishu/push/today -H "Authorization: Bearer $TOKEN" | python -m json.tool
```

- [ ] **Step 5: 幂等复推 + SyncTime 还原**

再推一次（期望 success:true 同样行数），然后还原：

```bash
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
con.execute(\"UPDATE dy_collect_video SET SyncTime='2026-08-20 13:00:24.8377043' WHERE Id='2090302912038219776'\"); con.commit(); con.close()
print('已还原')"
```

- [ ] **Step 6: 清理应用空间旧数据**

用户文件夹验证通过后，删除应用身份时代的数据（用宿主 curl + tenant token，流程同 8/22 清理）：

```bash
# 取 tenant token(应用身份)
APPID=$(PYTHONIOENCODING=utf-8 python -c "import sqlite3; con=sqlite3.connect(r'D:/dysync/data/db/dy.sqlite'); print(con.execute('SELECT FeishuAppId FROM dy_app_config LIMIT 1').fetchone()[0]); con.close()")
SECRET=$(PYTHONIOENCODING=utf-8 python -c "import sqlite3; con=sqlite3.connect(r'D:/dysync/data/db/dy.sqlite'); print(con.execute('SELECT FeishuAppSecret FROM dy_app_config LIMIT 1').fetchone()[0]); con.close()")
TOK=$(curl -s -X POST https://open.feishu.cn/open-apis/auth/v3/tenant_access_token/internal -H "Content-Type: application/json" -d "{\"app_id\":\"$APPID\",\"app_secret\":\"$SECRET\"}" | python -c "import sys,json;print(json.load(sys.stdin)['tenant_access_token'])")
# 删旧Base与应用自建文件夹
curl -s -X DELETE "https://open.feishu.cn/open-apis/drive/v1/files/LVMAbgoqSaYMWysKxVIczawNnth?type=bitable" -H "Authorization: Bearer $TOK"
curl -s -X DELETE "https://open.feishu.cn/open-apis/drive/v1/files/JulCfSkJolMwuxdCPKNcM37UnCg?type=folder" -H "Authorization: Bearer $TOK"
# 清应用文件夹缓存列
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
con.execute(\"UPDATE dy_app_config SET FeishuAutoFolderToken='' WHERE 1=1\"); con.commit(); con.close()
print('AutoFolderToken 已清')"
```

注意：新 Base 的 app_token 以推送结果 `baseUrl` 为准（`https://feishu.cn/base/<token>`）；上面 LVMA 是切换前的旧值，直接可删。

- [ ] **Step 7: 更新记忆与文档**

把 E2E 结果（授权流程、新 Base token、用户确认可见）写入 memory `feishu-bitable-push.md` 增补段；`docs/feishu-app-setup.md` 增补「账号授权（推荐）」一节说明重定向 URL 配置与授权按钮。

---

## Self-Review 结论

- **Spec 覆盖**：授权流程(T2/T4)、token 管理(T2)、建 Base 分流(T3)、4列+回填(T1)、前端(T5)、通知文案增强——spec「通知」节要求授权过期时群通知带提示：**缺口**，在 Task 2 Step 2 的 `GetUserAccessTokenAsync` 抛出的异常 message 已含「请到设置页重新授权」，`FeishuPushService` 失败路径会把 `result.Message` 传入 `notifyService.SendAsync`（现有代码 `text = $"...推送失败:{result.Message}"`），通知文案自动带上——已覆盖，无需改代码
- **占位符扫描**：无 TBD/TODO；所有代码步骤含完整代码
- **类型一致性**：`HasUserAuth/BuildAuthorizeUrlAsync/ExchangeCodeAsync` 在 T2 定义、T3/T4 消费，签名一致；前端 `FeishuOauthUrl` 与 Task 4 端点路径一致
