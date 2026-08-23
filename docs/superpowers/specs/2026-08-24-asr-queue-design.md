# ASR 异步转写队列 + 状态可视化 — 设计文档

日期：2026-08-23（深夜定稿，8-24 实施）
状态：已确认（用户批准；含两轮审查修正——控制器自查 2 项 + 独立审查 9 项）

## 背景与痛点（用户实测反馈）

1. 同步任务内逐条 HTTP 同步转写：18 条视频 × 3-4 分钟 = 同步阻塞 1 小时；
   容器重启/部署时正在转写的写回**静默丢失**（已实际发生：调试期间 recreate
   导致 9 条视频卡「未生成」，需手动补转）
2. 状态不可见：dysync 侧只有 已生成/失败/未生成 三态，「排队中/转换中」
   无法区分（ASR 内部有 _INFER_LOCK 排队但 dysync 看不见）
3. ASR 看板只见当前转写的一条，队列积压不可见
4. ASR 空闲自停（30min 无请求退出）与队列并存时：**队列任务全灭**

## 方案总览

dysync 从「同步阻塞式转写」改为「异步队列 + 扫库消费」：

```
同步任务: 入库即返回(不再转字幕) —— 18条从1小时变秒级
   ↓ (视频状态=待提交)
SubtitleQueueJob(每2分钟):
   扫库(48h内未生成,限100) → ASR健康检查(不通则拉起)
   → 未提交的上传文件 submit(幂等键) → 排队中
   → 已提交的查 status → 成功:拉结果写.srt/.txt+Path / 失败:StatusMsg / 404:重报(≤3次)
   ↓
前端五态展示 + ASR看板队列计数 + 飞书等待(条件不变)
```

## 已确认决策

| 决策点 | 选择 |
|---|---|
| 架构 | 异步队列+扫库消费（非只加状态展示） |
| 手动生成字幕 | 仍走同步接口（点完等结果） |
| 提交时机 | 扫库发现式（2分钟粒度） |
| 飞书等待 | 保留现有逻辑（10min 重查/5h 保险丝），判定条件不变 |

## 一、ASR 服务侧改动（D:\ASR-For-Dysync）

### 1.1 队列落盘持久化（修 Critical#1）

`asr_jobs.py`：
- `submit`/`_process` 完成/`_mark_failed` 时把 pending(0/1) 任务写
  `data/queue.json`（原子写：tmp+rename）；终态任务不落盘
- 启动时读 queue.json 恢复 status=0 的任务重新入队（status=1 的也重置为 0——
  进程死了doing必然中断）
- queue.json 记录 `{task_id, file_path, title, source, idempotency_key, retry_of}`

### 1.2 自停判定修正（修 Critical#1）

空闲自停条件从「30min 无请求」改为「30min 无请求 **且 队列空**」
（`_idle_exit` 检查 `asr_jobs.pending_count() == 0`）。队列有任务时永不自停。

### 1.3 新端点：文件上传式提交（修自查 Bug1 路径问题）

```
POST /api/transcribe/submit   (multipart: file, title?, source?, idempotency_key?)
  → 存临时文件 data/tmp/{uuid}.{ext}
  → asr_jobs.submit_file(tmp_path, title, source, idempotency_key)
  → {task_id, deduplicated: bool}
```

幂等（修 Critical#3）：`_jobs` 增加 idempotency_key 索引 dict；
重复 key 直接返回原 task_id（deduplicated=true），不再入队。

### 1.4 现有端点扩展

- `GET /api/asr/status`：**不改**（轮询方不读 result 字段即轻量；
  dysnc 成功后调用同一端点拉 result——大响应只在完成时拉一次，修 Important#6）
- `GET /api/jobs/list`：queued 计数已有；**新增 doing 计数**（现只有 infer 单条）

### 1.5 临时文件生命周期（修 Important#8）

worker 处理完（成功/失败）即删临时文件；启动时清扫 data/tmp/ 残留。

### 1.6 worker 处理链

复用现有 `_process_asr_job` 骨架，数据源从「下载 URL」改为「本地临时文件」：
抽音频（视频后缀）→ `_run_asr`（_INFER_LOCK 串行不变）→ 结果写回 _jobs。
任务记录（job_store）同步登记 source=dysync-queue。

## 二、dysync 侧改动（D:\dysync\dysync.net）

### 2.1 实体新列（CodeFirst 自动加）

```csharp
public long? AsrTaskId { get; set; }      // ASR task_id(null=未提交)
public int?  AsrTaskStatus { get; set; }   // 镜像:0排队/1转换中(终态不存此列)
public int    AsrRetryCount { get; set; }  // 404重报次数,≥3置失败
```

**加进 ConfigController.UpdateConfig 回填清单**？不需要——这是 dy_collect_video
列不是 dy_app_config；同步保存路径不涉及全列覆盖此表。

### 2.2 SubtitleQueueJob（新 Quartz Job）

- `[DisallowConcurrentExecution]`（修 Important#4）；每 2 分钟
  （cron `0 */2 * * * ?`）；注册仿飞书推送模式（InitSubtitleQueueJob，启动即注册，
  与开关无关——AutoGenSubtitle 关闭时 Job 内部自检退出）

每轮执行序：

```
1. config.AutoGenSubtitle != true → return
2. ASR health → 不通则 EnsureAsrRunningAsync(现有flag拉起);仍不通→return(下轮再试)
3. 查状态批: 库中 AsrTaskStatus∈{0,1} 的(全表,通常少量):
   GET status?task_id:
     status=0/1 → 更新镜像列(仅当变化)
     status=2   → 拉 result/result_detail → 写.srt/.txt(复用BuildSrtContent)
                  → Path/StatusMsg 落库;写回前若 Path已有值(手动先完成)→跳过写文件只清TaskId(修Important#5)
     status=3   → StatusMsg="ASR: "+error_msg
     404        → AsrRetryCount++; <3:清TaskId重报 / ≥3:StatusMsg="ASR任务丢失,重试超限"(修Critical#2)
4. 提交批: 库中 Path空+Msg空+TaskId空+SyncTime≥48h前(修Important#7/9) 限100:
   逐条读文件→POST submit(idempotency_key=video.Id)→存TaskId/Status=0
   (上传串行,每条间不delay——本地ASR无风控)
   文件不存在 → StatusMsg="Video file not found"(终态,不入队)
```

### 2.3 同步任务瘦身

`DouyinBasicSyncJob.SaveVideos` 删除 `GenerateSubtitlesForVideosAsync` 调用块
（含 AutoGenSubtitle 判断——交给队列 Job 自检）。日志改为「入库 N 条(字幕由队列处理)」。

### 2.4 状态判定（前端/推送/Job 统一语义）

| 库状态 | 语义 |
|---|---|
| SubtitleSavePath 有值 | 已生成 |
| SubtitleStatusMsg 有值 | 失败 |
| AsrTaskStatus=1 | 转换中 |
| AsrTaskStatus=0 | 排队中 |
| 全空 | 待提交 |

飞书等待条件**不改**：「Path空且Msg空」=未终态则等（待提交/排队/转换都命中，正确）。

## 三、前端改动

### 3.1 RecordTable 状态列五态

`subtitleStatusOf` 扩展（record 加 asrTaskId/asrTaskStatus 字段透出）：
- processing 拆两态：`asrTaskStatus===1` 转换中(蓝) / `===0` 排队中(橙,显示排队位置≈队列计数)
- generatingId(手动单条)保留，优先级最高
- 列表页轮询：有非终态行时 30s 自动刷新（现无轮询）——仅当当前页含非终态

### 3.2 ASR 看板

`/api/jobs/list` 已有 stats；顶部统计行加「队列中 N | 转换中 M」
（M=doing 数，1.4 新增）。前端 refreshJobs 已 3s 轮询，加两个格子即可。

## 四、吞吐与窗口核算

- 转写吞吐 ~20条/h（单条3min）；正常日增<40条 → 当天消化完
- 扫描窗口 48h：backlog 380条历史视频不会被捞（它们 SyncTime 老于 48h）；
  未来想补历史时临时调窗口（配置化否？**不配置**，常量，需要时改码重部署——YAGNI）
- 23:50 推送等待（至04:50=5h）兜底极端积压（100条×3min=5h 恰好边界，
  超限强推部分空字幕，次日重写补全——幂等安全）

## 五、错误处理汇总

| 场景 | 行为 |
|---|---|
| dysync 停机>2h(任务GC) | 恢复后 status 404 → 重报(≤3次) |
| ASR 自停时有队列任务 | 不自停(1.2) |
| ASR 崩溃重启 | queue.json 恢复 pending;doing重置为0重跑 |
| 提交后写库失败 | 幂等键查重,不重复转写(1.3) |
| 手动先完成,队列后到 | 队列写回检查 Path 已有→跳过文件写入只清 TaskId |
| 队列写回时写文件失败 | catch→StatusMsg="写回失败",RetryCount不动(下轮404路径重试) |
| 视频文件被删 | 提交批直接标 not found 终态 |
| ASR 长期不可用 | Job 每轮 return;飞书等待5h保险丝强推 |

## 六、测试计划（E2E）

1. ASR 单元：submit 幂等(同key两次→同task_id)/queue.json 断电恢复/队列非空不自停
2. 链路：同步入库 3 条 → 2min 内变「排队中」→ 逐条「转换中」→「已生成」(前端五态全程可观察)
3. 自愈：转写中 docker restart dysync → 恢复后状态续转不丢
4. 404 路径：手动删 ASR 的 _jobs 条目(或等2h) → dysnc 重报
5. 手动+队列并发：排队中手动生成 → 队列写回让位
6. 飞书等待：未终态时 23:50 等待(现有逻辑回归)
7. 回归：手动生成字幕/批量生成/ASR看板/设置页

## 七、明确不做（YAGNI）

- 队列优先级/取消/暂停
- 手动生成本身走队列
- 跨机队列(局域网分离部署时 dysync→远端ASR 走HTTP上传,天然兼容,无需额外设计)
- 扫描窗口配置化
- 转写失败自动重试(手动点=重试入口)
- 多 worker 并行(GPU 单卡瓶颈)
