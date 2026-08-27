# ASR 队列与飞书链路修复 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 修复双链路审查确认的 8 个 bug（4 高危 + 4 中危），顺带实现字幕补漏（文件恢复后自动重转）。

**Architecture:** 三块防御性修补：①飞书 token 刷新互斥 + 推送日期固定 + 探活误判收窄；②字幕队列状态机（并发互斥/空文本终态/写回计数/失败有限重试+补漏扫）；③两个小修（空路径终态 + 删调试残留）。零表结构变更，ASR 服务端零改动。

**Tech Stack:** .NET 6 + SqlSugar（SQLite）+ Quartz.NET + Vue3 前端（本计划不动前端）

**设计文档:** `docs/superpowers/specs/2026-08-27-asr-feishu-bugfix-design.md`

## Global Constraints

- .NET 6（SDK 在 `D:\dotnet-sdk\dotnet.exe`，用绝对路径调用）；SqlSugar 局部更新用 `Updateable(entity).UpdateColumns(...)` 或 `IgnoreColumns`
- 项目无单测基建——每个任务的"验证"是**编译通过 + 部署后端到端**，不用 TDD 红绿循环
- 后端部署链（每任务完成后可选做，最后统一做）：`D:/dotnet-sdk/dotnet.exe publish -c Release -r linux-x64 -o <dir>` → `docker cp <dir>/dy.net.dll dysync2026:/app/` → commit + recreate；arm64 最后统一重建
- 每任务一个 commit，消息格式 `fix(scope): 描述`
- 不碰 ASR 服务端（D:\ASR-For-Dysync）与前端（app/src）

---

### Task 1: H1 — 飞书用户 token 并发刷新互斥

**Files:**
- Modify: `service/FeishuBitableService.cs`（GetUserAccessTokenAsync :178-207、GetTenantTokenAsync :209 附近、类字段区）

**Interfaces:**
- Produces: `SemaphoreSlim _tokenGate`（实例字段，Task 1 内部使用）；`GetUserAccessTokenAsync(AppConfig)` 签名不变

- [ ] **Step 1: 加锁字段与 double-check 刷新**

在类字段区（`_cachedToken` 声明附近）加：

```csharp
/// <summary>用户token刷新互斥:refresh_token一次性,并发刷新会互相作废(后到者清掉先到者的新token)。</summary>
private readonly SemaphoreSlim _tokenGate = new(1, 1);
```

改写 `GetUserAccessTokenAsync`（整体替换 :178-207）：

```csharp
/// <summary>获取用户token:未过期直接用;过期用refresh刷新。全程持锁+进锁重读config复查(double-check),
/// 防并发刷新互相作废;仅refresh确证失效才清库,清库前校验库里token仍是本请求所用的那个。</summary>
private async Task<string> GetUserAccessTokenAsync(AppConfig config)
{
    if (!string.IsNullOrEmpty(config.FeishuUserAccessToken) && config.FeishuUserTokenExpiresAt > DateTime.Now)
        return config.FeishuUserAccessToken;

    if (!HasUserAuth(config))
        throw new Exception("飞书用户授权已过期,请到设置页重新点击「授权飞书账号」");

    await _tokenGate.WaitAsync();
    try
    {
        // double-check:等锁期间并发者可能已刷新完——重读config,新token未过期直接用
        var latest = commonService.GetConfig();
        if (latest != null && !string.IsNullOrEmpty(latest.FeishuUserAccessToken)
            && latest.FeishuUserTokenExpiresAt > DateTime.Now)
        {
            CopyUserTokens(latest, config);
            return latest.FeishuUserAccessToken;
        }
        var refreshUsed = config.FeishuUserRefreshToken;

        var client = _httpClientFactory.CreateClient(FEISHU_HTTP_CLIENT);
        var resp = await client.PostAsJsonAsync($"{FEISHU_HOST}/open-apis/authen/v2/oauth/token", new
        {
            grant_type = "refresh_token",
            client_id = config.FeishuAppId,
            client_secret = config.FeishuAppSecret,
            refresh_token = refreshUsed
        });
        var body = await resp.Content.ReadFromJsonAsync<FeishuOAuthTokenResp>();
        if (body?.Code != 0 || string.IsNullOrEmpty(body.AccessToken))
        {
            // 仅refresh确证失效才清库;清库前校验库里还是本请求所用的token——不等说明并发者已换新,不清(避免误杀对方成果)
            var codeOrError = body?.Error ?? body?.Code.ToString();
            var current = commonService.GetConfig();
            if (current != null && current.FeishuUserRefreshToken == refreshUsed)
            {
                current.FeishuUserAccessToken = null;
                current.FeishuUserRefreshToken = null;
                current.FeishuUserTokenExpiresAt = null;
                current.FeishuUserRefreshExpiresAt = null;
                await commonService.UpdateConfig(current);
                CopyUserTokens(current, config);
            }
            throw new Exception($"飞书用户授权已失效({codeOrError}),请到设置页重新授权");
        }
        await SaveUserTokensAsync(config, body);
        return body.AccessToken;
    }
    finally { _tokenGate.Release(); }
}

/// <summary>把最新token字段同步回调用方持有的config引用(免得调用方后续用旧值覆盖落库)。</summary>
private static void CopyUserTokens(AppConfig from, AppConfig to)
{
    to.FeishuUserAccessToken = from.FeishuUserAccessToken;
    to.FeishuUserRefreshToken = from.FeishuUserRefreshToken;
    to.FeishuUserTokenExpiresAt = from.FeishuUserTokenExpiresAt;
    to.FeishuUserRefreshExpiresAt = from.FeishuUserRefreshExpiresAt;
}
```

注意：确认 `commonService` 字段存在于本类（`FeishuBitableService` 构造函数注入了 `DouyinCommonService`；若无则检查构造函数注入清单，SaveUserTokensAsync :174 已在用 `commonService.UpdateConfig`，说明字段存在）。

- [ ] **Step 2: tenant token 缓存纳入同一把锁**

`GetTenantTokenAsync`（:209 起）整体包进 `_tokenGate.WaitAsync()/try/finally Release`（body 不变，只加锁包裹），`_cachedToken/_tokenExpireAt` 读写随锁保护。

- [ ] **Step 3: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
```
Expected: `0 Error`（warning 可忽略，项目存量 warning 多）

- [ ] **Step 4: Commit**

```bash
cd /d/dysync/dysync.net && git add service/FeishuBitableService.cs && git commit -m "fix(feishu): 用户token并发刷新互斥——double-check+清库前校验,防互相作废"
```

---

### Task 2: H2 — 推送日期固定 pushDate（跨午夜不丢当天数据）

**Files:**
- Modify: `service/FeishuPushService.cs`（RunDailyPushAsync :32-149）
- Modify: `service/FeishuBitableService.cs`（PushDailyAsync :36-51、EnsureMonthlyBaseAsync :247-249）

**Interfaces:**
- Produces: `PushDailyAsync(AppConfig config, List<FeishuVideoRow> rows, DateTime pushDate)`（新签名，第三参=推送日期；唯一调用点在 FeishuPushService.cs:119）
- Produces: `EnsureMonthlyBaseAsync(AppConfig config, DateTime pushDate)`（私有方法加参）

- [ ] **Step 1: RunDailyPushAsync 固定 pushDate**

方法体开头（`try {` 之后、字幕等待之前）加：

```csharp
var pushDate = DateTime.Today; // 跨午夜保护:等待最长到04:50,期间Today会变新一天——筛选/表名/Base月份全用入口快照
```

替换两处筛选：
- :58 `.Where(v => v.SyncTime >= DateTime.Today)` → `.Where(v => v.SyncTime >= pushDate)`
- :89 `all.Where(v => v.SyncTime >= DateTime.Today)` → `all.Where(v => v.SyncTime >= pushDate)`

**保持不动**（有意）：:52 deadline、:55 while 条件、:128 stamp、:136 LastPushResult——都用真实时钟。

- [ ] **Step 2: PushDailyAsync/EnsureMonthlyBaseAsync 加参**

FeishuBitableService `PushDailyAsync`（:36-51）：

```csharp
public async Task<FeishuPushResult> PushDailyAsync(AppConfig config, List<FeishuVideoRow> rows, DateTime pushDate)
{
    var baseToken = await EnsureMonthlyBaseAsync(config, pushDate);
    var tableName = $"{pushDate.Month}月{pushDate.Day}日";
    // ...后续三行不变
```

`EnsureMonthlyBaseAsync`（:247 起）签名改 `(AppConfig config, DateTime pushDate)`，:249 `var month = $"{DateTime.Now:yyyy-M}";` → `var month = $"{pushDate:yyyy-M}";`。

调用点 FeishuPushService.cs:119 改：

```csharp
result = await bitableService.PushDailyAsync(config, rows, pushDate);
```

- [ ] **Step 3: 编译 + Commit**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
git add service/FeishuPushService.cs service/FeishuBitableService.cs && git commit -m "fix(feishu): 推送日期入口快照——字幕等待跨午夜后不再推错天/跨月建错Base"
```

---

### Task 3: H3 — Base 探活网络异常不再触发重建

**Files:**
- Modify: `service/FeishuBitableService.cs`（EnsureMonthlyBaseAsync 探活段 :251-268）

**Interfaces:** 无新接口

- [ ] **Step 1: catch 分支改 throw**

把探活段的：

```csharp
catch (Exception ex)
{
    Log.Warning(ex, "[feishu] 缓存Base探测异常,将重建");
}
```

改为：

```csharp
catch (Exception ex)
{
    // 网络异常≠Base被删:误重建会产生重复Base(8/25事故同类)。宁可不推,下次推送重试。
    throw new Exception($"缓存Base探测网络异常,本次推送终止: {ex.Message}");
}
```

（try 内 `Code != 0 → Log.Warning + 落到下方重建` 的真被删路径保持不变。）

- [ ] **Step 2: 编译 + Commit**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
git add service/FeishuBitableService.cs && git commit -m "fix(feishu): Base探活网络异常终止推送而非重建,杜绝网络抖动重复建Base"
```

---

### Task 4: 局部更新方法 + H4 并发互斥

**Files:**
- Modify: `repository/DouyinVideoRepository.cs`（类内追加方法）
- Modify: `service/DouyinVideoService.cs`（:101 UpdateOne 后追加）
- Modify: `service/LocalAsrSubtitleService.cs`（类字段区 + GenerateSubtitleAsync 入口/出口）
- Modify: `job/SubtitleQueueJob.cs`（case 2 写回段）

**Interfaces:**
- Produces: `Task<bool> DouyinVideoRepository.UpdateSubtitleFieldsAsync(DouyinVideo video)` —— SqlSugar `Updateable(video).UpdateColumns(x => new { x.SubtitleSavePath, x.SubtitleStatusMsg, x.SubtitleCreateTime, x.AsrTaskId, x.AsrTaskStatus, x.AsrRetryCount })`（按 Id 定位）
- Produces: `Task<bool> DouyinVideoService.UpdateSubtitleFieldsAsync(DouyinVideo video)` —— 透传仓库方法
- Produces: `static LocalAsrSubtitleService.TryAcquireVideoGate(string videoId)` / `ReleaseVideoGate(string videoId, SemaphoreSlim gate)` —— 进程内按视频互斥

- [ ] **Step 1: 仓库+服务层局部更新**

DouyinVideoRepository 类内追加：

```csharp
/// <summary>只更新字幕/ASR相关6列(按Id),杜绝整实体stale覆盖其余字段。</summary>
public async Task<bool> UpdateSubtitleFieldsAsync(DouyinVideo video)
{
    return await Db.Updateable(video)
        .UpdateColumns(x => new { x.SubtitleSavePath, x.SubtitleStatusMsg, x.SubtitleCreateTime, x.AsrTaskId, x.AsrTaskStatus, x.AsrRetryCount })
        .ExecuteCommandAsync() > 0;
}
```

DouyinVideoService 在 UpdateOne（:101-104）后追加：

```csharp
/// <summary>只更新字幕/ASR相关6列(队列Job专用,防整实体覆盖)。</summary>
public async Task<bool> UpdateSubtitleFieldsAsync(DouyinVideo video)
{
    return await _dyCollectVideoRepository.UpdateSubtitleFieldsAsync(video);
}
```

- [ ] **Step 2: 视频级互斥门**

LocalAsrSubtitleService 字段区（:19-20 附近）加：

```csharp
/// <summary>按视频Id的进程内互斥:手动转写与队列Job并发处理同一视频时,后到者让路。</summary>
private static readonly ConcurrentDictionary<string, SemaphoreSlim> _videoGates = new();

/// <summary>尝试进入视频处理门(非阻塞)。返回null=有人在处理中。</summary>
public static SemaphoreSlim TryAcquireVideoGate(string videoId)
{
    if (string.IsNullOrWhiteSpace(videoId)) return new SemaphoreSlim(1, 1); // 无Id不互斥(防御)
    var gate = _videoGates.GetOrAdd(videoId, _ => new SemaphoreSlim(1, 1));
    return gate.Wait(0) ? gate : null;
}

public static void ReleaseVideoGate(string videoId, SemaphoreSlim gate)
{
    try { gate?.Release(); } catch (ObjectDisposedException) { }
}
```

确认文件顶部已有 `using System.Collections.Concurrent;`，没有则加。

- [ ] **Step 3: 手动路径挂门**

`GenerateSubtitleAsync`（:202 起）在 `if (video == null)` 校验之后立即插入：

```csharp
var gate = TryAcquireVideoGate(video.Id);
if (gate == null)
    return (false, "该视频正在处理中(手动/队列另一路径),请稍后再试", string.Empty);
try
{
    // ↓ 原方法体全部缩进进来(从 config 判空到最后的 return)
```

方法末尾追加 `finally { ReleaseVideoGate(video.Id, gate); }`。即整个原方法体包进 try/finally。

- [ ] **Step 4: 队列 case 2 写回挂门 + 局部更新**

SubtitleQueueJob case 2（:54-76）改为：写回前 `var gate = LocalAsrSubtitleService.TryAcquireVideoGate(v.Id); if (gate == null) break;`（跳过本轮该条，下轮再看），try/finally 释放；段内三处 `douyinVideoService.UpdateOne(v)` 全部换 `douyinVideoService.UpdateSubtitleFieldsAsync(v)`。改后完整段：

```csharp
case 2: // 成功:写回(手动先完成则让位)
    if (!string.IsNullOrWhiteSpace(v.SubtitleSavePath)) { v.AsrTaskId = null; v.AsrTaskStatus = null; await douyinVideoService.UpdateSubtitleFieldsAsync(v); break; }
    var gate = LocalAsrSubtitleService.TryAcquireVideoGate(v.Id);
    if (gate == null) break; // 手动路径正在处理,本轮跳过
    try
    {
        var srt = LocalAsrSubtitleService.BuildSrtContentFrom(text, segs);
        if (string.IsNullOrWhiteSpace(srt))
        {
            // M1:空文本(纯音乐/VAD全滤)不落空文件,标终态可手动重试
            v.SubtitleStatusMsg = "ASR returned empty content.";
            v.AsrTaskId = null; v.AsrTaskStatus = null;
            await douyinVideoService.UpdateSubtitleFieldsAsync(v);
            break;
        }
        var srtPath = Path.ChangeExtension(v.VideoSavePath, ".srt");
        var txtPath = Path.ChangeExtension(v.VideoSavePath, ".txt");
        try
        {
            await File.WriteAllTextAsync(srtPath, srt, System.Text.Encoding.UTF8);
            if (!string.IsNullOrWhiteSpace(text)) await File.WriteAllTextAsync(txtPath, text, System.Text.Encoding.UTF8);
            v.SubtitleSavePath = srtPath;
            v.SubtitleStatusMsg = "Subtitle generated via ASR queue.";
            v.SubtitleCreateTime = DateTime.Now;
            v.AsrTaskId = null; v.AsrTaskStatus = null; v.AsrRetryCount = 0;
            await douyinVideoService.UpdateSubtitleFieldsAsync(v);
        }
        catch (Exception)
        {
            // M2:写回失败计数,超限终态,不再无限重转
            v.AsrRetryCount += 1;
            v.AsrTaskId = null; v.AsrTaskStatus = null;
            if (v.AsrRetryCount >= 3) v.SubtitleStatusMsg = "字幕写回失败(重试超限)";
            await douyinVideoService.UpdateSubtitleFieldsAsync(v);
        }
    }
    finally { LocalAsrSubtitleService.ReleaseVideoGate(v.Id, gate); }
    break;
```

- [ ] **Step 5: 编译 + Commit**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
git add repository/DouyinVideoRepository.cs service/DouyinVideoService.cs service/LocalAsrSubtitleService.cs job/SubtitleQueueJob.cs && git commit -m "fix(asr): 手动/队列转写按视频互斥+队列改6列局部更新;空文本不落盘;写回失败计数超限终态"
```

---

### Task 5: M4 — ASR 失败有限重试 + 补漏扫（文件恢复自动重转）

**Files:**
- Modify: `job/SubtitleQueueJob.cs`（case 3 段 :77-81、第②步提交过滤 :100-105）

**Interfaces:** 无新接口（复用 AsrRetryCount 列）

- [ ] **Step 1: case 3 改有限重试**

:77-81 原段替换为：

```csharp
case 3: // ASR侧失败:瞬时故障(显存临时不足等)有限重试,超限才终态
    v.AsrRetryCount += 1;
    v.AsrTaskId = null; v.AsrTaskStatus = null;
    if (v.AsrRetryCount >= 3)
        v.SubtitleStatusMsg = $"ASR: {err}(重试超限)";
    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
    break;
```

- [ ] **Step 2: 提交过滤放宽（补漏）**

:100-104 的过滤条件，把

```csharp
.Where(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
    && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)
```

改为（其余三条件不动）：

```csharp
.Where(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
    && (string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)
        || (v.SubtitleStatusMsg == "Video file not found." && !string.IsNullOrWhiteSpace(v.VideoSavePath) && File.Exists(v.VideoSavePath))))
```

提交循环内、`QueueSubmitAsync` 之前，对命中补漏条件的先清标记：

```csharp
if (v.SubtitleStatusMsg == "Video file not found.")
{
    v.SubtitleStatusMsg = null; // 文件已恢复,清失败标记重新入队(字幕补漏)
    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
}
```

（原 `if (!File.Exists(...)) { v.SubtitleStatusMsg = "Video file not found."; ... }` 段保留——文件仍不在的会重新标回去，无害。）

- [ ] **Step 3: 编译 + Commit**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
git add job/SubtitleQueueJob.cs && git commit -m "fix(asr): ASR失败有限重试(3次)+文件恢复自动清not-found标记重转(字幕补漏)"
```

---

### Task 6: M3 + M6 — 空路径终态 + 删调试残留

**Files:**
- Modify: `job/SubtitleQueueJob.cs`（第②步开头）
- Modify: `job/VideoStatsBackfillJob.cs`（:44-45、:66-67）

**Interfaces:** 无

- [ ] **Step 1: 空路径今日记录标终态**

第②步 `var toSubmit = ...` 之前插入：

```csharp
// M3:无文件路径的今日记录标终态——否则飞书字幕等待永远等不到它,每晚硬等满5h保险丝
var todayStart = DateTime.Today;
var noFile = all.Where(v => v.SyncTime >= todayStart
    && string.IsNullOrWhiteSpace(v.VideoSavePath)
    && string.IsNullOrWhiteSpace(v.SubtitleSavePath)
    && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)).ToList();
foreach (var v in noFile)
{
    v.SubtitleStatusMsg = "no video file";
    await douyinVideoService.UpdateSubtitleFieldsAsync(v);
}
```

- [ ] **Step 2: 删调试残留**

删除 VideoStatsBackfillJob.cs 两处共 4 行（含 AppendAllText 调用与紧随的闭合）：

```csharp
System.IO.File.AppendAllText("/tmp/backfill-diag.txt",
    $"{DateTime.Now:HH:mm:ss} targets={targets.Count}\n");
```

```csharp
System.IO.File.AppendAllText("/tmp/backfill-diag.txt",
    $"{DateTime.Now:HH:mm:ss} author={authorGroup.Key} followed={(followed != null)} changed={changed.Count}\n");
```

- [ ] **Step 3: 编译 + Commit**

```bash
D:/dotnet-sdk/dotnet.exe build /d/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | tail -3
git add job/SubtitleQueueJob.cs job/VideoStatsBackfillJob.cs && git commit -m "fix(asr): 今日无文件记录标终态防推送硬等5h;删backfill调试残留"
```

---

### Task 7: 部署 amd64 + 端到端验证

**Files:** 无代码改动（构建产物部署）

- [ ] **Step 1: publish + 部署**

```bash
D:/dotnet-sdk/dotnet.exe publish /d/dysync/dysync.net/dy.net.csproj -c Release -r linux-x64 --self-contained false -o /d/dysync/build-context/pub-fix0827
docker cp /d/dysync/build-context/pub-fix0827/dy.net.dll dysync2026:/app/dy.net.dll
docker commit dysync2026 dysync:asr-local
cd /d/dysync && docker compose up -d --force-recreate
```

- [ ] **Step 2: 验证镜像一致 + 服务起活**

```bash
docker inspect dysync2026 --format '{{.Image}}'   # 应等于下一条
docker inspect dysync:asr-local --format '{{.Id}}'
curl -s http://localhost:10101/ -o /dev/null -w "%{http_code}\n"   # 200
```

- [ ] **Step 3: 端到端验证（按设计文档验证计划，重点 4 项）**

1. **M4 补漏**：本机 sqlite 造一条 not-found 记录→放回文件→2 分钟内自动重转（日志 `[subtitle-queue]`）
2. **M3**：INSERT 今日 VideoSavePath='' 记录 → push/today 不再等待（直接推）
3. **H1**：连续快速两次 `POST /api/Feishu/test`，第二次不报未授权
4. **H4**：手动点「生成字幕」同时观察 2 分钟内队列日志不重复提交同一视频

- [ ] **Step 4: Commit（如有验证中的微调）**

```bash
git add -A && git commit -m "chore: 0827修复部署验证通过"   # 仅当有改动
```

---

### Task 8: arm64 重建 + 导出 NAS tar

**Files:** 无代码改动

- [ ] **Step 1: 交叉编译进 pub-arm64 + 重建镜像**

```bash
D:/dotnet-sdk/dotnet.exe publish /d/dysync/dysync.net/dy.net.csproj -c Release -r linux-arm64 --self-contained false -o /d/dysync/build-context/pub-arm64
# 确认 pub-arm64/appsettings.json 存在（上次教训：缺它容器起不来）
cd /d/dysync/build-context/pub-arm64 && docker build -f Dockerfile -t dysync:asr-arm64 --platform linux/arm64 . 2>&1 | tail -2
```

- [ ] **Step 2: QEMU 冒烟（含真实库挂 volume）**

```bash
docker volume rm armtest >/dev/null 2>&1; docker volume create armtest >/dev/null
MSYS_NO_PATHCONV=1 docker run --rm -v "D:\dysync\data\db:/src:ro" -v armtest:/dst alpine cp /src/dy.sqlite /dst/
MSYS_NO_PATHCONV=1 docker run -d --name arm-smoke -p 10199:10101 -v armtest:/app/db --platform linux/arm64 dysync:asr-arm64
sleep 25 && curl -s http://localhost:10199/ -o /dev/null -w "%{http_code}\n"   # 200
docker rm -f arm-smoke; docker volume rm armtest
```

- [ ] **Step 3: 导出 tar（覆盖旧名）**

```bash
cd /d/dysync && rm -f dysync-asr-arm64-0827.tar && docker save dysync:asr-arm64 -o dysync-asr-arm64-0828.tar
ls -la dysync-asr-arm64-0828.tar   # ~258MB
```

- [ ] **Step 4: 提醒用户 NAS 更新流程**

告知：`docker load` → 删旧容器 → 用**与现有数据目录一致**的 compose up（⚠️ 8/27 教训：只换 image，不改动 volumes）→ 验证播放+follow 分页+日期筛选。

---

## Self-Review 结论

- Spec 覆盖：H1→T1、H2→T2、H3→T3、H4→T4、M1→T4(Step4)、M2→T4(Step4)、M4→T5、M3→T6、M6→T6 ✅
- 类型一致：`UpdateSubtitleFieldsAsync(DouyinVideo)` 三层同名同参；`TryAcquireVideoGate/ReleaseVideoGate` 静态方法名在 T4 定义并自用 ✅
- 无占位符：所有代码块完整可抄 ✅
