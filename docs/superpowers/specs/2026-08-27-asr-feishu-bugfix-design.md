# ASR 队列与飞书推送链路修复设计（2026-08-27）

> 来源：双链路代码审查（飞书 12 项 + ASR 队列 12 项，去误报后合并）。本设计只覆盖打包确认的 8 条（4 高危 + 4 中危），低危 5 条择机另办。
> 附带实现「字幕补漏」：失败标记不再永久挡住重试，文件恢复后自动补转。

## 背景：审查结论摘要

**飞书侧**（FeishuBitableService / FeishuPushService / VideoStatsBackfillJob）：
- H1 并发刷新 refresh_token 互相作废（刷新无锁；refresh 一次性；并发者把对方新 token 清库 → 用户被迫重新授权）
- H2 字幕等待跨午夜后 `DateTime.Today` 变新一天，推送的是次日 0 条，昨天数据永久丢失；跨月还把空表建进新月 Base
- H3 Base 探活把网络异常也当"被删"处理 → 重复建 Base（8/25 同类事故的另一入口）
- M3 今日记录 `VideoSavePath` 为空的永远停在"待字幕"态 → 飞书等待循环每晚硬等满 5h 保险丝
- M6 `/tmp/backfill-diag.txt` 调试残留，无 /tmp 环境直接炸回填 Job

**ASR 队列侧**（SubtitleQueueJob / LocalAsrSubtitleService）：
- H4 手动"生成字幕"（同步等待，分钟级）期间实体无标记，2 分钟队列 Job 把同一视频再提交；两边整实体 UpdateOne，后写者用 stale 数据回滚对方的字幕字段
- M1 队列路径 status=2 但文本为空（纯音乐视频）→ 写空 .srt 并标"已生成"终态（同步路径有此检查，队列路径漏了）
- M2 写回磁盘失败清 TaskId 不计数 → 磁盘故障时无限重转烧 GPU
- M4 ASR status=3 一律立即终态，瞬时故障（显存临时不足等）不重试；且 `Video file not found.` 标记永久挡住重试——即使文件后来回来了

**审查中已核实排除的误报**（不再处理）：ASR 服务端队列/幂等/落盘恢复均存在于 D:\ASR-For-Dysync（审查代理误看了 tools/asr-webapp 旧目录）；字幕 32000 截断存在于 SubtitleTextReader.cs:24；Quartz 为 SQLite 持久化。

## 修复设计

### 第 1 块：飞书 token/推送（FeishuBitableService / FeishuPushService）

**H1 并发刷新互斥**（FeishuBitableService）：
- 实例字段 `private readonly SemaphoreSlim _tokenGate = new(1,1);`
- `GetUserAccessTokenAsync` 全程持锁；进入后**重读 config**（commonService.GetConfig()）复查 `FeishuUserTokenExpiresAt`——若并发者已刷新则直接返回新 token（double-check）
- 清库分支收窄：仅当响应 code 非 0 且错误明确指向 refresh 失效（如 invalid_grant/20029 类）才清 4 列；清库前校验库里 refresh_token 仍等于本次请求所用值，不等则跳过清库（对方已更新）
- `_cachedToken/_tokenExpireAt`（tenant 路径）纳入同一把锁（顺带解决低危#10）

**H2 推送日期固定**（FeishuPushService.RunDailyPushAsync）：
- 方法入口 `var pushDate = DateTime.Today;`
- 字幕等待筛选 `v.SyncTime >= pushDate`（:58 替换）
- 读取今日数据 `v.SyncTime >= pushDate`（:89 替换）
- 日表名与月度 Base 月份传递：PushDailyAsync 增加 `DateTime pushDate` 参数，内部表名改用 pushDate；EnsureMonthlyBaseAsync 月份同步改（FeishuBitableService.cs:249 `$"{DateTime.Now:yyyy-M}"` → pushDate）
- 保持 Now 的三处（有意不改）：deadline 保险丝（:52）、等待循环条件（:55）、LastPushResult 时间戳与 stamp（:128,136）——这些就该用真实时钟

**H3 探活误判收窄**（FeishuBitableService.EnsureMonthlyBaseAsync）：
- try 内 `probeBody?.Code == 0` → 返回缓存（现状保留）
- `Code != 0` → 走重建（现状保留，这是真被删）
- **catch（网络异常/超时）→ 改为 throw 终止本次推送**，日志 `[feishu] 缓存Base探测网络异常,本次推送终止`。宁可不推不建重复 Base；下次推送自然重试

### 第 2 块：字幕队列状态机（SubtitleQueueJob / LocalAsrSubtitleService）

**H4 手动/队列互斥**：
- LocalAsrSubtitleService 加静态 `ConcurrentDictionary<string, SemaphoreSlim> _videoGates`
- 手动 `GenerateSubtitleAsync`：进入时 `Wait(0)` 抢 videoId 锁，抢不到返回 `(false, "该视频正在处理中(手动/队列),请稍后")`；finally 释放
- SubtitleQueueJob：case 2 写回前抢同一把锁（Wait(0)），抢不到本轮跳过该条
- 队列 Job 全部 `UpdateOne(v)` 改为局部更新：新增 DouyinVideoService.UpdateSubtitleFieldsAsync(videoId, 局部字段)——只 UPDATE SubtitleSavePath/SubtitleStatusMsg/SubtitleCreateTime/AsrTaskId/AsrTaskStatus/AsrRetryCount 六列，杜绝整实体 stale 覆盖

**M1 空文本终态**：case 2 中 `string.IsNullOrWhiteSpace(srt)` → 不落盘，写 `SubtitleStatusMsg = "ASR returned empty content."`，清 AsrTaskId/Status，不写 SubtitleSavePath

**M2 写回失败计数**：写文件 catch 内 `AsrRetryCount++`；`>= 3` 置 `SubtitleStatusMsg = "字幕写回失败(重试超限)"` 终态；否则仅清 TaskId 下轮重试。与 404 路径共用计数

**M4 失败有限重试 + 补漏扫**：
- case 3（ASR 报错）：`AsrRetryCount++`；`< 3` → 清 AsrTaskId/Status、**不写 StatusMsg**（下轮自动重新提交，瞬时故障自愈）；`>= 3` → 置 `SubtitleStatusMsg = "ASR: {err}(重试超限)"` 终态
- 第 ② 步提交条件放宽：原 `IsNullOrWhiteSpace(v.SubtitleStatusMsg)` 改为 `(IsNullOrWhiteSpace(v.SubtitleStatusMsg) || (v.SubtitleStatusMsg == "Video file not found." && File.Exists(v.VideoSavePath)))`——文件恢复后自动清标记重转（提交前清空该字段即可，复用既有提交链路）
- 手动"重新生成字幕"行为不变（overwrite 语义保持）

### 第 3 块：小修（VideoStatsBackfillJob / SubtitleQueueJob）

**M3 空路径终态**：第 ② 步开头对 `SyncTime >= 当天起点 && IsNullOrWhiteSpace(VideoSavePath) && IsNullOrWhiteSpace(SubtitleSavePath) && IsNullOrWhiteSpace(SubtitleStatusMsg)` 的记录写 `SubtitleStatusMsg = "no video file"`——飞书等待循环不再被拖满 5h
**M6 调试残留**：删除 VideoStatsBackfillJob.cs 两处共 4 行 `/tmp/backfill-diag.txt` AppendAllText

### 明确不做（本轮）

- M5 清空后写入失败的补偿（留观察）、M7 统计回写按标题匹配错行（需结构变更，单独设计）
- 低危 5 条：seq 复用窗口、长任务 token 续期、表列表翻页、head-of-line、refresh 兜底值
- ASR 服务端（asr_jobs.py）零改动——队列架构经核实是健全的

## 验证计划（端到端，无单测基建）

1. **H1**：部署后并发触发两次 `POST /api/Feishu/test`（或 test+push/today），验证无"未授权"错误、库中 token 仍有效
2. **H2**：临时把 FeishuPushCron 调到 23:58 且造一条"转换中"记录（或直接代码审查确认 pushDate 贯穿），观察跨 00:00 后推送的仍是当天表名与当天数据
3. **H3**：代码走查确认 catch 分支 throw；线上观察一次网络抖动不再产生重复 Base
4. **H4**：手动点"生成字幕"的同一瞬间等队列 Job 触发（2 分钟窗口内必现重叠），验证手动返回"处理中"提示或队列跳过，最终 .srt 内容与视频一致
5. **M1**：提交一个纯音乐视频文件验证标"empty content"而非空 .srt
6. **M2**：把某视频目录设只读后触发转写，验证 3 轮后终态不再重转
7. **M4 补漏**：造一条 `Video file not found.` 记录 → 把文件放回原路径 → 等 2 分钟验证自动重新提交并转写成功
8. **M3**：INSERT 一条今日 VideoSavePath 为空的记录 → 手动 push/today 验证不再等待
9. 部署链：amd64 本机 commit + force-recreate + 冒烟；arm64 交叉编译重建 + QEMU 冒烟 + docker save 导出 tar（既定流程）

## 影响面

- 改动文件：FeishuBitableService.cs、FeishuPushService.cs、SubtitleQueueJob.cs、LocalAsrSubtitleService.cs、DouyinVideoService.cs（新增局部更新方法）、VideoStatsBackfillJob.cs（删调试行）
- 配置/表结构：零变更（不新增列；AsrRetryCount 复用现有列）
- ASR 服务端 / 便携包：零改动
- 兼容性：M4 放宽提交条件对存量失败记录的影响——只有 `Video file not found.` 且文件已恢复的会被重扫，其余失败标记不动；存量 `AsrRetryCount` 有值的记录在新逻辑下计数继续累加（语义不变）
