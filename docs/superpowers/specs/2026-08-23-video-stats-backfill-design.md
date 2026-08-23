# 视频统计数据 3 天自动回填 + 飞书回写 — 设计文档

日期：2026-08-23
状态：已确认（用户批准；需求场景经三轮澄清：博主当天新视频的播放量太小无参考价值，发布 3 天后的数据才有效，需自动更新回当天推送的飞书日期表）

## 背景与目标

关注博主的新视频在发布当天即被同步下载（统计快照 v1）并当晚推飞书。但播放量/点赞等
数据需要发酵，当天快照无质量区分价值。需求：**发布后 3 天内每天自动刷新统计**——
更新本地库 + **回写到该视频当初推送的飞书日期表的同一行**（如 8/23 的视频 A，
其播放量在 8/24、8/25、8/26 逐日变新，8/26 后定格）。用户打开任何历史日期表看到的
都是每条视频的最新统计。

## 已确认决策

| 决策点 | 选择 |
|---|---|
| 触发方式 | 定时自动（新 Quartz 任务，每天 05:30） |
| 回填窗口 | 发布后 3 天（CreateTime ≥ 3 天前的不刷） |
| 数据去向 | 更新本地库 + 回写飞书原日期表的原行 |
| 范围 | 仅 dy_follows（关注博主视频）；喜欢/收藏不动 |

## 设计

### ① VideoStatsBackfillJob（新建 `job/VideoStatsBackfillJob.cs`）

Quartz IJob，cron `0 30 5 * * ?`（独立注册，仿 FeishuDailyPushJob 模式：
`DouyinQuartzJobService.InitVideoStatsBackfillJob(config)`，Program.cs 调用，
key=`stats.backfill.job.key.daily`）。`DisallowConcurrentExecution`。

流程：
1. 取目标：`douyinVideoService.GetAllAsync()` 过滤
   `ViedoType == VideoTypeEnum.dy_follows && CreateTime >= DateTime.Now.AddDays(-3)`
   （空则直接返回）
2. 按 `AuthorId` 分组。每组取任一记录的 AuthorId 作为博主标识
3. 对每个博主：`SyncUpderPostVideos("20", "0", secUid, cookie.Cookies)` 拉主页第一页
   （20 条足够覆盖 3 天发布量；抖音主页列表按时间倒序，3 天发超 20 条的极少——
   第一页若最旧一条 CreateTime 仍在 3 天窗口内则翻页，最多 3 页兜底）
   - secUid 来源：目标视频所属博主需从 `douyinFollowService` 按 AuthorId 查
     `DouyinFollowed.SecUid`（关注视频必然在关注列表）；查不到的博主跳过并记日志
   - cookie：`GetOpendCookiesAsync(x => !string.IsNullOrWhiteSpace(x.UpSavePath))` 取第一个
4. 按 `AwemeId` 匹配目标视频：五项统计有任一变化 → 更新库（`UpdateOne`）→ 加入
   「已变更列表」（带该视频的 SyncTime，供飞书回写定位）
5. 博主间 `Task.Delay(random 5~15s)` 防风控
6. 变更列表非空 → 调 `FeishuPushService` 新方法 `BackfillFeishuStatsAsync(changed)`

### ② FeishuBitableService.UpdateStatsAsync（新方法）

```
Task<int> UpdateStatsAsync(AppConfig config, List<DouyinVideo> changed)
```

- 未开启推送/未授权（无 Base 缓存）→ 直接返回 0（记日志）
- 按 `SyncTime.Date` 分组 → 表名 `{M}月{d}日` → 在本月 Base 的表列表里找
  （只可能命中本月；跨月的旧表在旧 Base 里，当前 Base 缓存只指向本月——
  跨月场景：3 天窗口意味着最多回看 3 天，月初 1-3 日会引用上月 Base。
  处理：按月分组，月份≠本月缓存时跳过并日志说明「跨月旧表不回写」（可接受，
  3 天窗口仅月初 3 天受影响））
- 每张目标表：分页读回全部记录（`GET records?page_size=500`，HasMore 翻页），
  每行 fields 的「视频标题」（富文本取 [{text}] 拼接）与视频 `VideoTitle` 精确匹配
  → 命中的 record_id 收集 `(record_id, 新统计五项)`
- `POST .../records/batch_update`（body: records=[{record_id, fields:{播放,点赞,评论,分享,收藏}}]，
  一次请求 ≤200 条，复用 BATCH_DELAY_MS/限流退避常量）
- 返回成功更新的行数

行匹配键：**视频标题精确匹配**（同表内）。标题理论上可能重复（同博主同名视频），
取匹配到的第一行（可接受误差；不引入 record_id 持久化——避免 schema 变更，
且每日表行数小（≤几十），读回匹配成本可忽略）。

### ③ 注册与启动

`DouyinQuartzJobService`：
- 常量 jobKey/triggerKey（`stats.backfill.job.key.daily` / `stats.backfill.trigger.key.daily`）
- `InitVideoStatsBackfillJob(AppConfig config)`：PushEnabled 且飞书已配置才调度？
  **不对**——回填分两层：更库（不依赖飞书）+ 飞书回写（依赖）。任务**无条件注册**
  （飞书未配置时只更库，回写自动跳过）。cron 固定 `0 30 5 * * ?`，暂不做配置项（YAGNI）
- Program.cs 启动时在 `InitFeishuPushJob(config)` 后调用
- `AddScoped<VideoStatsBackfillJob>`（ServiceExtension.AddQuartzService 注册区）

### 数据流总结

```
每天 05:30 VideoStatsBackfillJob
  → 库: dy_follows 且 CreateTime≥-3d 的视频, 按博主分组
  → 拉博主主页第一页(必要时翻页≤3) → AwemeId 匹配 → 五项统计变化者更库
  → changed 按SyncTime.Date分组 → 定位「M月d日」表(仅本月Base) → 标题匹配行
  → batch_update 改写五项统计列
```

## 错误处理

| 场景 | 行为 |
|---|---|
| 无符合条件的视频 | 日志一行，结束 |
| 博主不在关注表（已取关）/secUid 查不到 | 跳过该博主，日志 |
| 拉主页失败（网络/风控） | 该博主跳过（记 Error），继续下一博主 |
| 飞书未配置/未授权/无 Base 缓存 | 库照更，回写跳过（日志一行） |
| 飞书表不存在（当天没推送过/推送失败过） | 该组跳过 |
| 标题匹配不到行 | 该视频跳过，计数「未匹配」（日志） |
| batch_update 限流 1254291 | 复用推送的重试退避 |
| Job 异常 | Quartz 捕获记日志，次日重试（数据是幂等刷新，无累积风险） |

## 测试计划（无测试基建，E2E）

1. 造数据：把一条 dy_follows 视频的 CreateTime 改成昨天、统计五项改小
2. 临时把 job cron 调近（改代码常量或等 05:30）触发，观察：
   - 库中该视频统计被刷新（拉真实博主数据）
   - 飞书对应日期表该行统计列变化
3. 负路径：改一条不存在于飞书表的记录 → 验证跳过不报错
4. 还原测试数据

## 明确不做（YAGNI）

- 回填窗口/时刻的设置页配置（常量写死 3 天/05:30）
- 喜欢列表/收藏视频的回填（有现成手动接口）
- record_id 持久化（标题匹配足够）
- 跨月 Base 的历史表回写（3 天窗口仅月初 1-3 日受影响）
- 「数据有变化」的 diff 通知/webhook 通知回填结果
