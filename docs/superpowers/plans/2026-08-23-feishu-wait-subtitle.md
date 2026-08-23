# 飞书推送等待字幕就绪 + ASR 告警 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 定时飞书推送在今日视频字幕未全部就绪时推迟推送（每 10 分钟重查、5h 保险丝、ASR 异常群告警），手动推送不受影响。

**Architecture:** `FeishuPushService.RunDailyPushAsync` 加 `waitForSubtitles` 参数；就绪判定=今日记录中存在「SubtitleSavePath 空且 SubtitleStatusMsg 空」；等待循环每轮重读库并探 ASR health（连续 2 次失败告警一次）；`FeishuDailyPushJob` 传 true，Controller 不传（默认 false）。

**Tech Stack:** .NET 6（现有 dy.net），无新依赖。

**Spec:** `docs/superpowers/specs/2026-08-23-feishu-wait-subtitle-design.md`

## Global Constraints

- 项目**无测试基建**：每任务验证 = `D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "` 期望 `0` + 部署后 curl 冒烟
- 部署速查（后端）：`D:/dotnet-sdk/dotnet.exe publish D:/dysync/dysync.net/dy.net.csproj -c Release -r linux-x64 --self-contained false -o D:/dysync/build-context/pub` → `docker cp D:/dysync/build-context/pub/dy.net.dll dysync2026:/app/dy.net.dll` → `docker commit dysync2026 dysync:asr-local` → cwd D:/dysync 下 `docker compose up -d --force-recreate`。**必须 recreate**
- 就绪判定逐字：`string.IsNullOrWhiteSpace(v.SubtitleSavePath) && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)` 为未就绪；**StatusMsg 有值（失败终态）不阻塞**
- 手动推送路径行为必须与现状完全一致（默认参数即达成）
- 等待循环在 `_pushGate` 锁内（现有 SemaphoreSlim），循环内每轮重读库（`douyinVideoService.GetAllAsync()` + `SyncTime >= DateTime.Today` 过滤）
- ASR health 用 `LocalAsrSubtitleService.CheckHealthAsync(AppConfig config)`（返回 `(bool Success, string Message, string ServiceUrl)`）；连续 2 次失败才告警；一晚最多告警 1 次；告警走 `FeishuNotifyService.SendAsync`（webhook 空自动跳过）
- 每任务完成即 git commit（cwd=D:/dysync/dysync.net，分支 asr-windows-test）

---

### Task 1: FeishuPushService 等待逻辑

**Files:**
- Modify: `dysync.net/service/FeishuPushService.cs`

**Interfaces:**
- Consumes: `LocalAsrSubtitleService.CheckHealthAsync(AppConfig)` 返回 `(bool Success, string Message, string ServiceUrl)`（已存在）；DI 按命名空间自动注册，构造函数直接加参数即可
- Produces: `RunDailyPushAsync(bool waitForSubtitles = false)`——Task 2 的 Job 调用传 `waitForSubtitles: true`；Controller 现有调用 `RunDailyPushAsync()` 不变

- [ ] **Step 1: 构造函数注入 LocalAsrSubtitleService**

类字段区（现有 4 个 private readonly 之后）加：

```csharp
        private readonly LocalAsrSubtitleService asrSubtitleService;
```

构造函数参数追加 `LocalAsrSubtitleService asrSubtitleService`（放 notifyService 后），并在体内 `this.asrSubtitleService = asrSubtitleService;`。

- [ ] **Step 2: RunDailyPushAsync 加参数与等待循环**

签名改为（其余不变）：

```csharp
        public async Task<FeishuPushResult> RunDailyPushAsync(bool waitForSubtitles = false)
```

方法体内、`try {` 之后、`var all = await douyinVideoService.GetAllAsync();` **之前**插入等待块：

```csharp
                // 字幕等待(仅定时任务):今日存在字幕在转/待转的视频时推迟推送,直到全部终态或超保险丝。
                // 失败(StatusMsg有值)是终态不阻塞;手动推送 waitForSubtitles=false 跳过整段。
                if (waitForSubtitles)
                {
                    var deadline = DateTime.Now.AddHours(5); // 保险丝:最多等5小时(23:50→04:50),防ASR彻底故障时当天永不推送
                    int consecutiveAsrFail = 0;
                    bool asrAlarmSent = false;
                    while (DateTime.Now < deadline)
                    {
                        var pendingCheck = (await douyinVideoService.GetAllAsync())
                            .Where(v => v.SyncTime >= DateTime.Today);
                        if (!pendingCheck.Any(v => string.IsNullOrWhiteSpace(v.SubtitleSavePath)
                                                 && string.IsNullOrWhiteSpace(v.SubtitleStatusMsg)))
                            break; // 全部终态,正常推送

                        var asrHealth = await asrSubtitleService.CheckHealthAsync(config);
                        if (asrHealth.Success)
                        {
                            consecutiveAsrFail = 0;
                        }
                        else
                        {
                            consecutiveAsrFail++;
                            if (consecutiveAsrFail >= 2 && !asrAlarmSent)
                            {
                                asrAlarmSent = true; // 一晚只告警一次
                                Log.Warning("[feishu] ASR不可用,推送等待中: {Msg}", asrHealth.Message);
                                await notifyService.SendAsync(config,
                                    $"【抖小云】ASR 服务不可用({asrHealth.Message}),今日飞书推送暂停等待中,请检查 ASR 服务");
                            }
                        }
                        Log.Information("[feishu] 今日仍有字幕未就绪,10分钟后重查(截止 {Deadline:HH:mm})", deadline);
                        await Task.Delay(TimeSpan.FromMinutes(10));
                    }
                }
```

插入点说明：等待块必须在 `config` 取得之后（方法开头已有 `var config = commonService.GetConfig();` 及校验），在数据读取前。等待结束后走原有 `GetAllAsync` 流程（此时再读一次库，拿到的就是全就绪快照——等待块里的 pendingCheck 只用于判定，不复用）。

- [ ] **Step 3: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 4: Commit**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): 定时推送等待字幕就绪——10分钟重查/5h保险丝/ASR连续2次失败群告警"
```

---

### Task 2: Job 传参与部署冒烟

**Files:**
- Modify: `dysync.net/job/FeishuDailyPushJob.cs:19`

**Interfaces:**
- Consumes: Task 1 的 `RunDailyPushAsync(bool waitForSubtitles = false)`

- [ ] **Step 1: Job 调用传 waitForSubtitles: true**

```csharp
        public async Task Execute(IJobExecutionContext context)
        {
            await pushService.RunDailyPushAsync(waitForSubtitles: true);
        }
```

- [ ] **Step 2: 编译验证**

```bash
D:/dotnet-sdk/dotnet.exe build D:/dysync/dysync.net/dy.net.csproj -c Release 2>&1 | grep -c " error "
```
Expected: `0`

- [ ] **Step 3: 部署（Global Constraints 速查）+ 手动推送回归**

部署后验证手动路径不受影响（今日 0 条数据时应立即返回，不等待）：

```bash
TOKEN=$(curl -s -X POST http://localhost:10101/api/Auth/Login -H "Content-Type: application/json" -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
time curl -s -m 60 -X POST http://localhost:10101/api/feishu/push/today -H "Authorization: Bearer $TOKEN" | python -c "import sys,json;d=json.load(sys.stdin)['data'];print('success:',d['success'],'| count:',d['count'])"
```
Expected: 秒级返回（手动不等待），success: true

- [ ] **Step 4: 等待逻辑冒烟（造未就绪数据 + 临时缩短验证）**

不引入测试钩子的前提下，用日志验证等待块真的进入：造一条「两字段全空」的今日记录，然后**临时把容器里 cron 调到近未来触发定时推送**（改库 FeishuPushCron 为当前时间+3 分钟，等触发，观察容器日志出现「字幕未就绪,10分钟后重查」），验证后**删掉该记录、把 cron 恢复为空（默认 23:50）**：

```bash
# ① 造未就绪数据(SubtitleSavePath/StatusMsg 都置空)
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
row = con.execute(\"SELECT Id FROM dy_collect_video ORDER BY SyncTime DESC LIMIT 1\").fetchone()
con.execute('UPDATE dy_collect_video SET SyncTime=?, SubtitleSavePath=NULL, SubtitleStatusMsg=NULL WHERE Id=?', ('2026-08-23 12:00:00', row[0]))
con.commit(); print('未就绪记录:', row[0]); con.close()"

# ② cron 调到 +3 分钟(例:现在 13:47 → cron '0 50 13 * * ?')
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
con.execute(\"UPDATE dy_app_config SET FeishuPushCron='0 50 13 * * ?' WHERE 1=1\"); con.commit(); con.close()"

# ③ 触发调度重载(cron 改库不会自动重载 Quartz,用设置页保存或 ExecuteJobNow 等价调 UpdateConfig)
TOKEN=$(curl -s -X POST http://localhost:10101/api/Auth/Login -H "Content-Type: application/json" -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin)['token'])")
curl -s -X POST http://localhost:10101/api/config/UpdateConfig -H "Authorization: Bearer $TOKEN" -H "Content-Type: application/json" -d '{}'

# ④ 到点后看日志
docker logs dysync2026 --since 10m 2>&1 | grep -a "字幕未就绪\|feishu"

# ⑤ 验证完还原:删未就绪数据影响(恢复两字段) + cron 清空
PYTHONIOENCODING=utf-8 python -c "
import sqlite3
con = sqlite3.connect(r'D:/dysync/data/db/dy.sqlite')
con.execute(\"UPDATE dy_collect_video SET SyncTime='2026-08-20 13:00:24.8377043' WHERE SyncTime='2026-08-23 12:00:00'\")
con.execute(\"UPDATE dy_app_config SET FeishuPushCron='' WHERE 1=1\"); con.commit(); con.close()
print('已还原')"
```

Expected: 日志出现 `[feishu] 今日仍有字幕未就绪,10分钟后重查`；表格**未**被写入（推迟生效）。观察一轮即可还原（不必等全流程 10 分钟——出现该日志行即证明进入等待分支）。

- [ ] **Step 5: Commit + 更新记忆**

```bash
cd D:/dysync/dysync.net && git add -A && git commit -m "feat(feishu): 定时Job启用字幕等待(waitForSubtitles:true)"
```

把验证结果写入 memory `feishu-bitable-push.md`（等待逻辑上线 + 冒烟证据 + 今晚 23:50 首次真实运行）。

---

## Self-Review 结论

- **Spec 覆盖**：就绪判定/等待循环/10min 重查/5h 保险丝/ASR 连续 2 次告警一晚一次/手动不受影响（Task 1）✅；Job 传参（Task 2）✅；告警文案、webhook 空跳过（走 SendAsync 现有语义）✅；每轮重读库（pendingCheck 每轮 GetAllAsync）✅。spec 测试计划中「ASR 停机告警实测」依赖 webhook 配置，归入 Task 2 Step 4 可选项（未配 webhook 则验证日志行即可），不单列任务
- **占位符**：无；代码块完整
- **类型一致性**：`RunDailyPushAsync(bool waitForSubtitles = false)` 签名与 Task 2 调用 `waitForSubtitles: true` 一致；`CheckHealthAsync(config)` 返回元组 `.Success/.Message` 与 LocalAsrSubtitleService.cs:94 一致
