# 飞书推送等待字幕就绪 + ASR 异常告警 — 设计文档

日期：2026-08-23
状态：已确认（用户批准设计）

## 背景与目标

飞书每日推送（23:50）时，部分视频的 ASR 字幕尚未生成完（ASR 空闲自停冷启动 25s、
长视频转写耗时、ASR 故障），推上去的字幕列是空的，且推送是清空重写幂等——
当天不会再补。

用户需求：**字幕没转完就先不推，等 ASR 全部转完再推**；ASR 出问题时通过飞书群机器人
发告警，推送继续等待（不放弃）。

## 已确认的决策

| 决策点 | 选择 |
|---|---|
| 未就绪视频处理 | 推迟整个推送（不是只推就绪的） |
| 等待上限 | 等到全部就绪（实现上加 5 小时保险丝，见下） |
| 检查频率 | 每 10 分钟重查 |
| ASR 异常 | 群机器人告警 + 继续等待 |
| 手动「立即推送今天」 | 不等待，立即推当前状态 |

## 关键现状（代码事实）

- 字幕生成在同步任务内 await（`DouyinBasicSyncJob.SaveVideos` →
  `GenerateSubtitlesForVideosAsync` 逐条 await）——正常情况下同步结束字幕已终态
- **状态判定无独立字段**：`SubtitleSavePath` 有值=已生成；`SubtitleStatusMsg` 有值
  且 Path 空=失败（终态）；两者都空=未生成/在转（前端 `subtitleStatusOf` 同逻辑）
- 推送编排：`FeishuPushService.RunDailyPushAsync`（`SemaphoreSlim _pushGate` 防并发）
- Quartz：`FeishuDailyPushJob`，`DisallowConcurrentExecution`，cron 默认 `0 50 23 * * ?`
- 告警通道：`FeishuNotifyService.SendAsync`（webhook 空=跳过，吞异常）

## 设计

### 1. 就绪判定（新私有方法）

```
HasPendingSubtitlesAsync(today):
    return today.Any(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                       && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg));
```

未就绪 = 无字幕路径且无失败信息（还在转或待转）。**失败（StatusMsg 有值）不算未就绪**
——转写已终态失败，永远等不到，推上去字幕列为空是正确行为。

### 2. 等待循环（仅定时任务路径）

`RunDailyPushAsync` 增加参数 `bool waitForSubtitles = false`：

- `FeishuDailyPushJob` 调用时传 `true`（定时）
- `FeishuController.PushToday` 不传（手动，默认 false，行为不变）

等待逻辑（在 `_pushGate` 获取之后、读数据之后）：

```
deadline = DateTime.Now + 5h          // 保险丝:最多等到 04:50,防 Quartz 堵死/永不推
asrAlarmSent = false
while (HasPendingSubtitles(today) && DateTime.Now < deadline):
    Log.Information("[feishu] 字幕未全部就绪,10分钟后重查(截止 {deadline})")
    asrOk = CheckHealthAsync(config)   // 复用 LocalAsrSubtitleService
    if (!asrOk.Success):
        consecutiveFail++              // 连续 2 次失败才告警(避开 25s 冷启动误报)
        if (consecutiveFail >= 2 && !asrAlarmSent && webhook 已配):
            SendAsync("ASR 服务不可用,今日飞书推送暂停等待中,请检查 ASR 服务")
            asrAlarmSent = true        // 同一晚只告警一次
    else:
        consecutiveFail = 0
    await Task.Delay(10 min)           // 每轮之间;期间不重新读库(见「数据快照」)
    重新加载 today(见下)
```

**5 小时保险丝的理由**：用户选「等到全部就绪」，但 Quartz 任务
`DisallowConcurrentExecution` + 30 分钟级 cron 意味着挂起的推送会阻塞后续触发；
若某视频永远无法就绪（如 ASR 崩溃没重启），无上限等待 = 当天永不推送。
5 小时（23:50→04:50）覆盖最长的转写排队场景，到点强制推当前状态。

**数据快照**：每轮重查时**重新从库里加载今日记录**（`GetAllAsync` + 过滤今日），
因为等待期间可能有新视频入库/字幕状态更新。循环内重新计算 rows 与 pending。

### 3. 告警细节

- 走现有 `FeishuNotifyService.SendAsync`（webhook 未配则静默跳过——与推送通知一致）
- 文案：`【抖小云】ASR 服务不可用({错误信息}),今日飞书推送暂停等待中`
- 恢复后不发「已恢复」通知（YAGNI，推送结果本身就是恢复信号）
- 告警不抛异常、不影响等待循环

### 4. 依赖注入

`FeishuPushService` 构造函数增加 `LocalAsrSubtitleService`（容器已注册，直接注入）。

## 改动文件

| 文件 | 改动 |
|---|---|
| `service/FeishuPushService.cs` | `waitForSubtitles` 参数 + 就绪检查 + 等待循环 + ASR 告警 |
| `job/FeishuDailyPushJob.cs` | 调用处传 `waitForSubtitles: true`（1 行） |
| Controllers/FeishuController.cs | 不改（默认参数即手动行为） |

前端、设置页、配置项：**零改动**。

## 错误处理汇总

| 场景 | 行为 |
|---|---|
| 等待中手动点「立即推送」 | `_pushGate` 互斥——定时任务持锁等待期间，手动推送直接返回「已有推送任务进行中,稍后再试」（现有行为，无需改） |
| CheckHealthAsync 抛异常 | catch 计入连续失败（与返回 false 同等处理） |
| 等待中新视频入库 | 每轮重查数据库，新视频纳入就绪判定（若其字幕未就绪也一起等） |
| 等待超 5h | 强制推送当前状态，日志记「等待超时,强制推送」 |
| webhook 未配 | 告警静默跳过，等待逻辑照常 |

## 测试计划（E2E 为主，无测试基建）

1. 造 2 条今日数据：1 条有字幕、1 条两字段全空（未就绪）→ 手动推送（应**立即推**，
   不等待——验证手动路径不受影响）→ 清掉表格
2. 模拟定时：直接调 `RunDailyPushAsync(waitForSubtitles: true)` 的等价路径——
   项目无单测，通过临时把 Job 里的等待条件打到日志验证（或等当晚 23:50 真实触发）
3. ASR 告警：停掉 ASR（杀 python）+ 造未就绪数据 + 配 webhook → 触发定时推送 →
   群里应收到告警（此项依赖用户配 webhook，可后验）
4. 回归：正常路径（全部就绪）推送行为与现状完全一致

## 明确不做（YAGNI）

- 设置页开关（自动字幕+推送开启的用户，此行为即预期）
- 「已恢复」通知
- 等待期间的状态展示（设置页「推送状态」只显示最终结果）
- 手动推送等待选项
- 按视频粒度的补推（清空重写幂等已覆盖：次日推送自然带上全部字幕）
