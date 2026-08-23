# 视频统计 3 天自动回填 + 飞书回写 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 关注博主新视频发布后 3 天内每天 05:30 自动刷新五项统计（更库 + 回写飞书原日期表原行）。

**Architecture:** 新 Quartz Job `VideoStatsBackfillJob`（扫库→按博主拉主页→AwemeId 匹配更库→变更列表传给飞书回写）；`FeishuBitableService.UpdateStatsAsync`（按 SyncTime 定位日期表→标题匹配行→batch_update）；注册仿飞书推送任务模式。

**Tech Stack:** .NET 6，无新依赖。

**Spec:** `docs/superpowers/specs/2026-08-23-video-stats-backfill-design.md`

## Global Constraints

- 编译验证：`D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "` 期望 `0`
- 部署速查：publish（-r linux-x64 --self-contained false -o D:/dysync/build-context/pub）→ `docker cp .../dy.net.dll dysync2026:/app/dy.net.dll` → `docker commit dysync2026 dysync:asr-local` → cwd D:/dysync `docker compose up -d --force-recreate`。必须 recreate
- join 链（已验证）：`DouyinVideo.AuthorId`（author uid）→ `DouyinFollowService.GetByUperId(uperId, myUid)` 内部 `GetBySecUId(uperId, myId)`（按 UperId 查，见 DouyinFollowRepository.cs:76）→ `DouyinFollowed.SecUid` → `douyinHttpClientService.SyncUpderPostVideos("20", cursor, secUid, cookie.Cookies)`
- `DouyinVideo.CreateTime` 是 DateTime（发布时间）；`SyncTime` DateTime（入库时间，用于定位飞书表）
- 飞书 batch_update：`POST /open-apis/bitable/v1/apps/{base}/tables/{tbl}/records/batch_update`，body `{"records":[{"record_id":"recX","fields":{...}}]}`，响应信封同 batch_create（FeishuResp）；限流 1254291 退避复用 RETRY_DELAYS
- 读回记录 fields 的「视频标题」是富文本 `[{text:...}]` 数组（拼 text 取值）；「播放」等统计列是数字
- 每任务完成 git commit（cwd=D:/dysync/dysync.net，分支 asr-windows-test）

---

### Task 1: FeishuBitableService.UpdateStatsAsync（飞书回写）

**Files:**
- Modify: `dysync.net/service/FeishuBitableService.cs`（BatchCreateAsync 方法后追加）

**Interfaces:**
- Produces: `public async Task<int> UpdateStatsAsync(AppConfig config, List<dy.net.model.entity.DouyinVideo> changed)`——Task 2 的 Job 调用；返回成功更新行数
- Consumes: 现有 `AuthedClientAsync`、`FEISHU_HOST`、`BATCH_DELAY_MS`、`RETRY_DELAYS`、`EnsureOk`；DTO 需扩展（本任务内做）

- [ ] **Step 1: DTO 扩展（FeishuDtos.cs 末尾追加）**

```csharp
    /// <summary>records 读回的完整行(回填匹配用):record_id + fields。</summary>
    internal class FeishuRecordFullData
    {
        [JsonPropertyName("items")] public List<FeishuRecordFull> Items { get; set; }
        [JsonPropertyName("has_more")] public bool HasMore { get; set; }
        [JsonPropertyName("page_token")] public string PageToken { get; set; }
    }
    internal class FeishuRecordFull
    {
        [JsonPropertyName("record_id")] public string RecordId { get; set; }
        [JsonPropertyName("fields")] public JsonElement Fields { get; set; }
    }
```

注意：文件需 `using System.Text.Json;`（已有）+ `using System.Text.Json.Serialization;`（已有）。`Fields` 用 JsonElement 保留原始结构。

- [ ] **Step 2: UpdateStatsAsync 实现（FeishuBitableService.cs，BatchCreateAsync 后）**

```csharp
        /// <summary>统计回填:把变更视频的最新五项统计回写到它们当初推送的日期表原行(标题精确匹配)。
        /// 只处理本月 Base 内的表(跨月旧表跳过);飞书未配置/无缓存返回0。</summary>
        public async Task<int> UpdateStatsAsync(config_type config, List<dy.net.model.entity.DouyinVideo> changed)
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
                }
            }
            Log.Information("[feishu] 统计回写完成 updated={Updated} unmatched={Unmatched}", updated, unmatched);
            return updated;
        }

        /// <summary>从飞书 fields(JsonElement)里取文本字段值(富文本 [{text}] 拼接;纯字符串直取)。</summary>
        private static string ReadTextField(JsonElement fields, string name)
        {
            if (!fields.ValueKind == JsonValueKind.Object || !fields.TryGetProperty(name, out var v)) return null;
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
```

**重要**：上面 `config_type` 是占位说明——实际签名用真实类型：
`public async Task<int> UpdateStatsAsync(AppConfig config, List<DouyinVideo> changed)`
（AppConfig 在 dy.net.model.entity，DouyinVideo 同命名空间——FeishuBitableService.cs 已 using dy.net.model.entity）
另注意 `!fields.ValueKind == JsonValueKind.Object` 是笔误，正确写法：`fields.ValueKind != JsonValueKind.Object`。
需确认文件头已有 `using System.Text.Json;`（已有，JsonElement 用它）。

- [ ] **Step 3: 编译验证**（期望 0 error）

- [ ] **Step 4: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): UpdateStatsAsync——按标题匹配行回写五项统计到原日期表"
```

---

### Task 2: VideoStatsBackfillJob + 注册

**Files:**
- Create: `dysync.net/job/VideoStatsBackfillJob.cs`
- Modify: `dysync.net/service/DouyinQuartzJobService.cs`（InitFeishuPushJob 方法后加 InitVideoStatsBackfillJob）
- Modify: `dysync.net/Program.cs`（InitFeishuPushJob 调用后追加一行）
- Modify: `dysync.net/extension/ServiceExtension.cs`（AddQuartzService 的 AddScoped 区，FeishuDailyPushJob 行后）

**Interfaces:**
- Consumes: Task 1 的 `FeishuBitableService.UpdateStatsAsync(AppConfig, List<DouyinVideo>)`
- Consumes: `DouyinFollowService.GetByUperId(string uperId, string myUid)`（DouyinFollowRepository.cs:76 按 UperId 查）、`douyinHttpClientService.SyncUpderPostVideos("20", cursor, secUid, cookie.Cookies)`、`douyinCookieService.GetOpendCookiesAsync(...)`、`douyinVideoService.UpdateOne(video)`、`douyinCommonService.GetConfig()`
- Produces: 注册的 Quartz 任务 key `stats.backfill.job.key.daily`，cron `0 30 5 * * ?`

- [ ] **Step 1: VideoStatsBackfillJob.cs（新文件，完整代码）**

```csharp
using dy.net.model.dto;
using dy.net.model.entity;
using dy.net.service;
using Quartz;

namespace dy.net.job
{
    /// <summary>关注博主视频统计回填:每天05:30刷新「发布≤3天」的关注视频五项统计
    /// (更库+回写飞书原日期表原行)。幂等,无新增副作用。</summary>
    [DisallowConcurrentExecution]
    public class VideoStatsBackfillJob : IJob
    {
        private readonly DouyinCommonService commonService;
        private readonly DouyinVideoService douyinVideoService;
        private readonly DouyinCookieService douyinCookieService;
        private readonly DouyinHttpClientService douyinHttpClientService;
        private readonly DouyinFollowService douyinFollowService;
        private readonly FeishuBitableService feishuBitableService;
        private readonly Random _random = new();

        public VideoStatsBackfillJob(DouyinCommonService commonService, DouyinVideoService douyinVideoService,
            DouyinCookieService douyinCookieService, DouyinHttpClientService douyinHttpClientService,
            DouyinFollowService douyinFollowService, FeishuBitableService feishuBitableService)
        {
            this.commonService = commonService;
            this.douyinVideoService = douyinVideoService;
            this.douyinCookieService = douyinCookieService;
            this.douyinHttpClientService = douyinHttpClientService;
            this.douyinFollowService = douyinFollowService;
            this.feishuBitableService = feishuBitableService;
        }

        public async Task Execute(IJobExecutionContext context)
        {
            var config = commonService.GetConfig();
            if (config == null) return;

            var cutoff = DateTime.Now.AddDays(-3);
            var targets = (await douyinVideoService.GetAllAsync())
                .Where(v => v.ViedoType == VideoTypeEnum.dy_follows && v.CreateTime >= cutoff)
                .ToList();
            if (!targets.Any())
            {
                Serilog.Log.Debug("[stats-backfill] 无发布≤3天的关注视频,跳过");
                return;
            }

            var cookies = await douyinCookieService.GetOpendCookiesAsync(
                x => !string.IsNullOrWhiteSpace(x.UpSavePath));
            var cookie = cookies?.FirstOrDefault();
            if (cookie == null)
            {
                Serilog.Log.Debug("[stats-backfill] 无可用Cookie(需配置关注视频存储路径),跳过");
                return;
            }

            var changed = new List<DouyinVideo>();
            // 按博主分组拉主页最新数据
            foreach (var authorGroup in targets.GroupBy(v => v.AuthorId))
            {
                var followed = await douyinFollowService.GetByUperId(authorGroup.Key, cookie.MyUserId);
                if (followed == null || string.IsNullOrWhiteSpace(followed.SecUid))
                {
                    Serilog.Log.Debug("[stats-backfill] 博主 {Author} 不在关注表或无SecUid,跳过", authorGroup.Key);
                    continue;
                }

                try
                {
                    // 翻页拉取,覆盖到 3 天窗口外的视频为止(最多3页=60条兜底)
                    var latest = new List<Aweme>();
                    string cursor = "0";
                    for (int page = 0; page < 3; page++)
                    {
                        var data = await douyinHttpClientService.SyncUpderPostVideos("20", cursor, followed.SecUid, cookie.Cookies);
                        if (data?.AwemeList == null || !data.AwemeList.Any()) break;
                        latest.AddRange(data.AwemeList);
                        var oldest = data.AwemeList.Last();
                        // 列表按时间倒序,最旧一条已在窗口外即可停
                        if (DateTimeUtil.Convert10BitTimestamp(oldest.CreateTime) < cutoff) break;
                        if (data.HasMore != 1) break;
                        cursor = data.Cursor ?? (data.MaxCursor ?? "0");
                        await Task.Delay(_random.Next(2, 10) * 1000);
                    }

                    foreach (var video in authorGroup)
                    {
                        var item = latest.FirstOrDefault(a => a.AwemeId == video.AwemeId);
                        if (item?.Statistics == null) continue; // 博主已删或拉不到
                        var p = item.Statistics.PlayCount ?? 0;
                        var d = item.Statistics.DiggCount ?? 0;
                        var c = item.Statistics.CommentCount ?? 0;
                        var s = item.Statistics.ShareCount ?? 0;
                        var col = item.Statistics.CollectCount ?? 0;
                        if (video.PlayCount == p && video.DiggCount == d && video.CommentCount == c
                            && video.ShareCount == s && video.CollectCount == col)
                            continue; // 无变化
                        video.PlayCount = p; video.DiggCount = d; video.CommentCount = c;
                        video.ShareCount = s; video.CollectCount = col;
                        await douyinVideoService.UpdateOne(video);
                        changed.Add(video);
                    }
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[stats-backfill] 博主 {SecUid} 拉取失败,跳过", followed.UperName);
                }
                await Task.Delay(_random.Next(5, 15) * 1000);
            }

            Serilog.Log.Information("[stats-backfill] 库刷新完成 changed={Changed}/{Total}", changed.Count, targets.Count);

            // 飞书回写(未配置时 UpdateStatsAsync 内部返回0)
            if (changed.Any())
            {
                try
                {
                    var rows = await feishuBitableService.UpdateStatsAsync(config, changed);
                    Serilog.Log.Information("[stats-backfill] 飞书回写 {Rows} 行", rows);
                }
                catch (Exception ex)
                {
                    Serilog.Log.Error(ex, "[stats-backfill] 飞书回写失败(库已更新,不影响)");
                }
            }
        }
    }
}
```

注意：`Aweme`/`DateTimeUtil` 的 using 若编译报错则补（Aweme 在 dy.net.model.response，DateTimeUtil 在 dy.net.utils——参照 DouyinFollowedSyncJob.cs 的 using 区）。`video.PlayCount == p` 比较 long? 与 long 会隐式转换，可行。

- [ ] **Step 2: DouyinQuartzJobService 注册**

`InitFeishuPushJob` 方法后追加：

```csharp
        /// <summary>
        /// 注册关注视频统计回填任务(每天05:30,发布≤3天视频刷新统计+回写飞书)。与飞书推送配置无关,无条件注册。
        /// </summary>
        public async Task InitVideoStatsBackfillJob()
        {
            var scheduler = await _schedulerFactory.GetScheduler();
            var jobKey = new JobKey("stats.backfill.job.key.daily", DefaultJobGroup);
            var triggerKey = new TriggerKey("stats.backfill.trigger.key.daily", DefaultJobGroup);

            if (await scheduler.CheckExists(jobKey))
                await scheduler.DeleteJob(jobKey);

            var jobDetail = JobBuilder.Create<VideoStatsBackfillJob>()
                .WithIdentity(jobKey)
                .WithDescription("关注视频统计回填")
                .DisallowConcurrentExecution()
                .Build();
            var trigger = TriggerBuilder.Create()
                .WithIdentity(triggerKey)
                .WithCronSchedule("0 30 5 * * ?")
                .Build();
            await scheduler.ScheduleJob(jobDetail, trigger);
            Log.Information("【quartz】统计回填任务已调度,cron=0 30 5 * * ?");
        }
```

- [ ] **Step 3: Program.cs 调用**

`await feishuQuartzService.InitFeishuPushJob(config);` 之后追加：

```csharp
                    // 关注视频统计回填(与Cookie/飞书配置无关)
                    var backfillQuartzService = services.GetRequiredService<DouyinQuartzJobService>();
                    await backfillQuartzService.InitVideoStatsBackfillJob();
```

- [ ] **Step 4: DI 注册（ServiceExtension.AddQuartzService）**

`services.AddScoped<FeishuDailyPushJob>();` 行后追加：

```csharp
            services.AddScoped<VideoStatsBackfillJob>();
```

- [ ] **Step 5: 编译验证**（期望 0 error）

- [ ] **Step 6: 部署 + 验证任务已注册**

部署（Global Constraints 速查）后：

```bash
docker logs dysync2026 2>&1 | grep -a "统计回填" | head -2
PYTHONIOENCODING=utf-8 python -c "
import sqlite3, datetime
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
for r in con.execute(\"SELECT TRIGGER_NAME, CRON_EXPRESSION FROM QRTZ_CRON_TRIGGERS WHERE TRIGGER_NAME LIKE '%backfill%'\"):
    print(r)
con.close()"
```
Expected: 日志含「统计回填任务已调度」；QRTZ_CRON_TRIGGERS 有 stats.backfill 触发器 cron `0 30 5 * * ?`

- [ ] **Step 7: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat: 关注视频统计3天自动回填Job+注册(每天05:30,更库+回写飞书)"
```

---

### Task 3: E2E 验证

**Files:** 无代码改动

- [ ] **Step 1: 造回填场景**

当前库无 dy_follows 视频（全类型1）。造法：取一条类型1视频临时改成 dy_follows（ViedoType=3）+ CreateTime=昨天 + SyncTime=今天（让它同时命中回填窗口和飞书今日表）。它的 AuthorId 需在 dy_follow 表有对应 UperId——取 dy_follow 里 OpenSync=1 的博主（真实关注，能拉到主页数据），把测试视频 AuthorId 改成该博主 UperId：

```bash
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
uper = con.execute('SELECT UperId, UperName FROM dy_follow WHERE OpenSync=1 LIMIT 1').fetchone()
row = con.execute(\"SELECT Id, AwemeId, VideoTitle FROM dy_collect_video LIMIT 1\").fetchone()
con.execute(\"UPDATE dy_collect_video SET ViedoType=3, AuthorId=?, CreateTime=datetime('now','-1 day'), SyncTime=datetime('now','localtime') WHERE Id=?\", (uper[0], row[0]))
con.commit()
print('测试视频:', row[2][:20], '| 伪装博主:', uper[1], '| Id:', row[0])
con.close()"
```

- [ ] **Step 2: 先推飞书（当日表有这行）+ 触发回填**

cron 临时改近（改 Job 常量重部署，或直接手动调：给 Job 加临时代码不值得——**用 Quartz 触发**：把容器里触发器 fire 时间改近不可行，改用「重启容器后 cron 已到点」窗口或临时改 cron 常量为当前+3分钟重新部署，验证后改回 `0 30 5 * * ?` 再部署）。流程：

1. 手动推送今天（POST /api/feishu/push/today）——当日表有测试行（统计为原值）
2. 临时 cron 部署 → 到点 Job 跑 → 观察：
   - 库:该视频统计是否被博主真实数据刷新（SELECT 验证）
   - 飞书:当日表该行统计列变化（API 读回对比）
3. 恢复:ViedoType=1/AuthorId 原值/CreateTime/SyncTime 原值;cron 改回 0 30 5 * * ? 重新部署;验证 QRTZ 表

- [ ] **Step 3: 记录验证结果到 memory**

---

## Self-Review 结论

- Spec 覆盖：Job 扫描/分组/拉取/更库（Task2 Step1）、飞书定位表/匹配行/batch_update（Task1）、注册+Program+DI（Task2 Step2-4）、幂等/无变化跳过（Task2 Step1 的比较逻辑）、错误处理（各 catch/跳过分支）✅
- 占位符：Task1 Step2 代码里标注了两处占位说明（config_type 与笔误提示）——实现者需按说明落地真实代码，已给出正确形态 ✅
- 类型一致性：UpdateStatsAsync(AppConfig, List<DouyinVideo>) 在 T1 定义 T2 调用一致；GetByUperId(uperId, myUid) 签名与 DouyinFollowService.cs:116 一致；AwemeId/AwemeList/HasMore/Cursor/MaxCursor 字段与 SyncUpderPostVideos 返回的 DouyinVideoInfoResponse 一致（DouyinFollowedSyncJob 同款用法）✅
