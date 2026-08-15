# ASR 按需启停集成 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** dysync 用字幕时 ASR 自动就位（宿主 watcher 按需拉起）、闲置 30 分钟自退释放显存，health 上报 GPU/模型详情，顺手把显存上限 3.5→6.5 修掉长视频误杀。

**Architecture:** 云 API 模式三组件：① `app.py` 增强（health 扩展 + 空闲自杀 + 上限常量）② 宿主 `watcher.ps1` + 计划任务（信号文件桥，挂载卷互通）③ dysync 后端拉起等待（`LocalAsrSubtitleService` 写 flag + 轮询）+ compose 挂载 + 设置页详情。

**Tech Stack:** Python/FastAPI/uvicorn（conda env asr）、PowerShell 5.1 + schtasks、.NET 6 (dy.net.csproj, `D:\dotnet-sdk\dotnet.exe`)、Vue3+antd（AppSet.vue）。

## Global Constraints

- ASR 服务地址契约不变：`GET /api/health`、`POST /api/transcribe`（dysync 只调这两个）
- 显存软上限改为 `GPU_MEM_LIMIT_GB = 6.5`（8GB 卡，留 ~1.5GB 给系统图形进程）
- 空闲退出默认 30 分钟（`IDLE_EXIT_MINUTES` 环境变量可调，0=禁用）
- watcher 轮询间隔 1s；health 就绪等待最长 180s（模型加载 1-2 分钟）
- 信号文件路径固定：容器 `/app/asr-bridge/start.flag` ↔ 宿主 `D:\dysync\asr-bridge\start.flag`
- watcher 不主动重启自杀的 ASR（按需语义，等下一个 flag）
- 部署链（后端改动）：`dotnet publish` → `docker cp dy.net.dll` → `docker commit dysync:asr-local` → `docker compose up -d --force-recreate`（见记忆 dysync-deployment；docker cp 后必须验证无 dist/dist 嵌套——本期只 cp dll 无此问题）
- 编码：app.py/watcher.ps1 存 UTF-8；PS1 首行不加 BOM 依赖，用 `chcp` 无关方案（PS5.1 读 UTF-8 需 BOM，watcher.ps1 保存为 **UTF-8 with BOM**）

---

### Task 1: app.py 增强——health 扩展 + 空闲自停 + 显存上限

**Files:**
- Modify: `D:\AI_Model\ASR\webapp\app.py`（:47 常量、:301-305 health、文件尾启动块、新增中间件与空闲线程）

**Interfaces:**
- Produces（Task 3/4 依赖的 health 响应字段，全部小写）：
```json
{"status":"ok","model_loaded":true,"device":"cuda","gpu_name":"RTX 2060 SUPER",
 "gpu_monitor":true,"mem_limit_gb":6.5,
 "vram_total_mb":8192,"vram_used_mb":3569,
 "model_dir_exists":true,"idle_exit_minutes":30}
```

- [ ] **Step 1: 修改显存上限常量（:47）**

把：
```python
GPU_MEM_LIMIT_GB = 3.5
```
改为：
```python
GPU_MEM_LIMIT_GB = 6.5
```
（原 3.5 是给已停跑的 LLM 预留；8GB 卡实测空闲 4.6GB，3.5 误杀长视频）

- [ ] **Step 2: 新增模型目录检查与空闲配置（常量区，:60 前后 `_INFER_LOCK` 附近）**

```python
# 模型目录关键文件检查(health 上报用)
MODEL_FILES_REQUIRED = ["model.pt", "tokens.json"]
MODEL_DIR_EXISTS = os.path.isdir(MODEL_DIR) and all(
    os.path.isfile(os.path.join(MODEL_DIR, f)) for f in MODEL_FILES_REQUIRED
)

# 空闲自动退出(分钟);0=禁用。dysync 按需拉起,闲置释放显存给其他 GPU 进程。
IDLE_EXIT_MINUTES = int(os.environ.get("ASR_IDLE_EXIT_MINUTES", "30"))
LAST_REQUEST_TS = time.time()   # 每次请求更新
```

- [ ] **Step 3: 请求中间件——打时间戳（FastAPI app 定义之后，health 路由之前）**

```python
@app.middleware("http")
async def _touch_last_request(request, call_next):
    global LAST_REQUEST_TS
    LAST_REQUEST_TS = time.time()
    return await call_next(request)
```

- [ ] **Step 4: 空闲看门狗线程（中间件之后）**

```python
def _idle_watchdog():
    """每 60s 检查一次;超过 IDLE_EXIT_MINUTES 无请求则退出进程释放显存。"""
    if IDLE_EXIT_MINUTES <= 0:
        return
    while True:
        time.sleep(60)
        idle = time.time() - LAST_REQUEST_TS
        if idle > IDLE_EXIT_MINUTES * 60:
            print(f"[IDLE] {idle/60:.0f} 分钟无请求,自动退出释放显存", flush=True)
            os._exit(0)

threading.Thread(target=_idle_watchdog, daemon=True).start()
```

- [ ] **Step 5: 扩展 /api/health（:301-305）**

把：
```python
@app.get("/api/health")
async def health():
    return {"status": "ok", "model_loaded": MODEL is not None, "device": DEVICE,
            "gpu_name": GPU_NAME, "gpu_monitor": HAS_NVML,
            "mem_limit_gb": GPU_MEM_LIMIT_GB, "model": MODEL_INFO}
```
改为：
```python
@app.get("/api/health")
async def health():
    stats = gpu_stats() if HAS_NVML else {}
    return {"status": "ok", "model_loaded": MODEL is not None, "device": DEVICE,
            "gpu_name": GPU_NAME, "gpu_monitor": HAS_NVML,
            "mem_limit_gb": GPU_MEM_LIMIT_GB, "model": MODEL_INFO,
            "vram_total_mb": stats.get("total_mb", 0),
            "vram_used_mb": stats.get("used_mb", 0),
            "model_dir_exists": MODEL_DIR_EXISTS,
            "idle_exit_minutes": IDLE_EXIT_MINUTES}
```

- [ ] **Step 6: 语法自检**

Run: `/c/Users/admin/miniconda3/envs/asr/python.exe -m py_compile /d/AI_Model/ASR/webapp/app.py && echo COMPILE_OK`
Expected: `COMPILE_OK`
（注：`gpu_stats()` 现有返回键需核对，若实际是别的键名如 `mem_total`，以现有实现为准调整 Step 5 的取键）

- [ ] **Step 7: 重启 ASR 并验证 health 新字段**

Run:
```bash
# 停旧进程(568)拉新的
powershell.exe -NoProfile -Command "Stop-Process -Id 568 -Force -ErrorAction SilentlyContinue"
powershell.exe -NoProfile -Command "
Start-Process 'C:\Users\admin\miniconda3\envs\asr\python.exe' -ArgumentList '-m','uvicorn','app:app','--host','0.0.0.0','--port','8000' -WorkingDirectory 'D:\AI_Model\ASR\webapp' -RedirectStandardOutput 'D:\dysync\asr_service.log' -RedirectStandardError 'D:\dysync\asr_service.err.log' -WindowStyle Minimized"
sleep 90   # 模型加载
curl -s http://127.0.0.1:8000/api/health | python -m json.tool
```
Expected: 返回含 `vram_total_mb: 8192`、`model_dir_exists: true`、`idle_exit_minutes: 30`、`mem_limit_gb: 6.5`

- [ ] **Step 8: 实测长视频转写（之前显存超限那条）**

Run:
```bash
# 从 dysync 触发那条失败视频(Id=2088271938844844032)的字幕重新生成
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
curl -s -m 300 -X GET "http://localhost:10101/api/video/asr/2088271938844844032?overwrite=true" -H "Authorization: Bearer $TOKEN"
```
Expected: `code:0`，不再报 `显存超限(上限 3.5GB)`；宿主生成同名 `.srt`/`.txt`

- [ ] **Step 9: 提交（app.py 不在 git 仓，备份留档）**

app.py 位于 `D:\AI_Model\ASR`（非 dysync 仓）。做带日期备份：
```bash
cp /d/AI_Model/ASR/webapp/app.py /d/AI_Model/ASR/webapp/app.py.bak-20260815
```
（权威副本 `dysync.net/tools/asr-webapp/app.py` 的同步单独记入 Task 6 记忆步骤，不在本期强做）

---

### Task 2: 宿主 watcher——watcher.ps1 + 计划任务

**Files:**
- Create: `D:\dysync\asr-bridge\watcher.ps1`（UTF-8 with BOM）
- Create: `D:\dysync\asr-bridge\watcher.log`（运行时生成）

**Interfaces:**
- Consumes: `start.flag`（同目录，dysync 容器经挂载卷写入）
- Produces: 拉起 uvicorn 进程（stdout→`D:\dysync\asr_service.log`，stderr→`D:\dysync\asr_service.err.log`，与 Task 1 Step 7 路径一致）

- [ ] **Step 1: 写 watcher.ps1**

```powershell
# DysyncAsrWatcher - 按 start.flag 拉起本地 ASR 服务
# 依赖: conda env asr 的 python、模型目录、8000 端口空闲
$ErrorActionPreference = "Continue"
$BridgeDir   = "D:\dysync\asr-bridge"
$Flag        = Join-Path $BridgeDir "start.flag"
$LogFile     = Join-Path $BridgeDir "watcher.log"
$Py          = "C:\Users\admin\miniconda3\envs\asr\python.exe"
$WorkDir     = "D:\AI_Model\ASR\webapp"
$ModelPt     = "D:\AI_Model\ASR\Paraformer_model\model.pt"
$OutLog      = "D:\dysync\asr_service.log"
$ErrLog      = "D:\dysync\asr_service.err.log"
$HealthUrl   = "http://127.0.0.1:8000/api/health"

function Write-Log($msg) {
    $line = "{0} {1}" -f (Get-Date -Format "yyyy-MM-dd HH:mm:ss"), $msg
    Add-Content -Path $LogFile -Value $line -Encoding UTF8
    if ((Get-Item $LogFile -ErrorAction SilentlyContinue).Length -gt 1MB) {
        Set-Content -Path $LogFile -Value "" -Encoding UTF8   # 超1MB清空
    }
}

New-Item -ItemType Directory -Force -Path $BridgeDir | Out-Null
Write-Log "watcher 启动,轮询 $Flag"

while ($true) {
    try {
        if (Test-Path $Flag) {
            # 1) 前置检查
            if (-not (Test-Path $Py))     { Write-Log "[SKIP] conda python 不存在: $Py" }
            elseif (-not (Test-Path $ModelPt)) { Write-Log "[SKIP] 模型文件不存在: $ModelPt" }
            else {
                # 2) ASR 已在跑?(health 通即视为在跑)
                $alive = $false
                try { Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 3 | Out-Null; $alive = $true } catch {}
                if ($alive) {
                    Write-Log "[OK] ASR 已在运行,忽略本次 flag"
                } else {
                    Write-Log "[START] 拉起 ASR ..."
                    Start-Process -FilePath $Py `
                        -ArgumentList '-m','uvicorn','app:app','--host','0.0.0.0','--port','8000' `
                        -WorkingDirectory $WorkDir `
                        -RedirectStandardOutput $OutLog `
                        -RedirectStandardError $ErrLog `
                        -WindowStyle Minimized
                    # 3) 等就绪(最长180s,模型加载慢)
                    $ok = $false
                    foreach ($i in 1..36) {
                        Start-Sleep -Seconds 5
                        try { Invoke-RestMethod -Uri $HealthUrl -TimeoutSec 3 | Out-Null; $ok = $true; break } catch {}
                    }
                    if ($ok) { Write-Log "[READY] ASR 就绪(${i}x5s)" }
                    else     { Write-Log "[FAIL] ASR 180s 未就绪,详见 $ErrLog" }
                }
            }
            Remove-Item $Flag -Force -ErrorAction SilentlyContinue
        }
    } catch {
        Write-Log "[ERR] $($_.Exception.Message)"
    }
    Start-Sleep -Seconds 1
}
```

- [ ] **Step 2: 写入文件（UTF-8 with BOM）**

用 Write 工具写 `D:\dysync\asr-bridge\watcher.ps1` 后，转 BOM：
```bash
powershell.exe -NoProfile -Command "
\$c = Get-Content -Raw -Encoding UTF8 'D:\dysync\asr-bridge\watcher.ps1'
[System.IO.File]::WriteAllText('D:\dysync\asr-bridge\watcher.ps1', \$c, (New-Object System.Text.UTF8Encoding \$true))
Write-Output 'BOM_OK'"
```
Expected: `BOM_OK`

- [ ] **Step 3: 注册计划任务（登录自启）**

```bash
powershell.exe -NoProfile -Command "schtasks /Create /TN DysyncAsrWatcher /TR \"powershell.exe -WindowStyle Hidden -ExecutionPolicy Bypass -File D:\dysync\asr-bridge\watcher.ps1\" /SC ONLOGON /F; schtasks /Query /TN DysyncAsrWatcher"
```
Expected: `成功: 创建计划任务 "DysyncAsrWatcher"`，Query 列出该任务

- [ ] **Step 4: 立即启动 watcher（不等重新登录）**

```bash
powershell.exe -NoProfile -Command "Start-ScheduledTask -TaskName DysyncAsrWatcher; Start-Sleep 3; (Get-ScheduledTask -TaskName DysyncAsrWatcher).State"
```
Expected: `Running`；`watcher.log` 出现 `watcher 启动` 行

- [ ] **Step 5: 端到端测试 watcher（手动 flag 触发）**

```bash
# 先杀掉现跑的 ASR，模拟"没启动"
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object {(Get-Process -Id \$_.Id).Path -like '*envs\asr*'} | Stop-Process -Force"
sleep 2
echo "want asr" > /d/dysync/asr-bridge/start.flag
echo "flag 已写，等待 watcher 拉起(最长3分钟)..."
for i in $(seq 1 36); do
  sleep 5
  CODE=$(curl -s -m 3 -o /dev/null -w "%{http_code}" http://127.0.0.1:8000/api/health 2>/dev/null)
  if [ "$CODE" = "200" ]; then echo "✅ 第${i}次探测(${i}x5s) ASR 已就绪"; break; fi
done
tail -5 /d/dysync/asr-bridge/watcher.log
```
Expected: watcher.log 出现 `[START]` 与 `[READY]`；health 返回 200；flag 文件被删除

- [ ] **Step 6: 负向测试（模型缺失不拉起）**

```bash
mv /d/AI_Model/ASR/Paraformer_model/model.pt /d/AI_Model/ASR/Paraformer_model/model.pt.hidden
echo "want asr" > /d/dysync/asr-bridge/start.flag
sleep 5
tail -3 /d/dysync/asr-bridge/watcher.log
mv /d/AI_Model/ASR/Paraformer_model/model.pt.hidden /d/AI_Model/ASR/Paraformer_model/model.pt
```
Expected: log 出现 `[SKIP] 模型文件不存在`；ASR 未被拉起；flag 被删；模型恢复后再次 flag 可正常拉起

---

### Task 3: dysync 后端——拉起等待逻辑

**Files:**
- Modify: `D:\dysync\dysync.net\service\LocalAsrSubtitleService.cs`（`GenerateSubtitleAsync` 的 health 检查处，约 :158；新增私有方法）

**Interfaces:**
- Consumes: `CheckHealthAsync(config, ct)` 已有；flag 容器路径 `/app/asr-bridge/start.flag`
- Produces: `EnsureAsrRunningAsync(AppConfig config, CancellationToken ct)` → `Task<(bool Success, string Message)>`，`GenerateSubtitleAsync` 与 `GenerateSubtitlesForVideosAsync` 在转写前调用

- [ ] **Step 1: 新增 EnsureAsrRunningAsync（CheckHealthAsync 方法之后）**

```csharp
        /// <summary>
        /// 确保 ASR 服务在线:不在线则写信号文件触发宿主 watcher 拉起,并轮询等待就绪(最长180s)。
        /// </summary>
        private const string AsrBridgeFlagPath = "/app/asr-bridge/start.flag";

        private async Task<(bool Success, string Message)> EnsureAsrRunningAsync(
            AppConfig config,
            CancellationToken cancellationToken = default)
        {
            var health = await CheckHealthAsync(config, cancellationToken);
            if (health.Success)
            {
                return (true, health.Message);
            }

            // 写信号文件通知宿主 watcher 拉起(宿主侧目录经 compose 挂载对应)
            try
            {
                var flagDir = Path.GetDirectoryName(AsrBridgeFlagPath);
                if (!string.IsNullOrEmpty(flagDir))
                {
                    Directory.CreateDirectory(flagDir);
                }
                await File.WriteAllTextAsync(AsrBridgeFlagPath, DateTime.Now.ToString("O"), cancellationToken);
            }
            catch (Exception ex)
            {
                return (false, $"ASR offline and bridge flag write failed: {ex.Message}");
            }

            // 轮询等待就绪:5s 间隔,最长 180s(模型加载 1-2 分钟)
            for (var attempt = 0; attempt < 36; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                health = await CheckHealthAsync(config, cancellationToken);
                if (health.Success)
                {
                    return (true, "ASR service started on demand.");
                }
            }

            return (false, "ASR service did not become ready in 180s. Check D:\\dysync\\asr-bridge\\watcher.log on the host.");
        }
```

- [ ] **Step 2: GenerateSubtitleAsync 接入（原 health 检查处，约 :158-163）**

把：
```csharp
            var healthResult = await CheckHealthAsync(config, cancellationToken);
            if (!healthResult.Success)
            {
                await UpdateVideoSubtitleStateAsync(video, string.Empty, healthResult.Message);
                return (false, healthResult.Message, string.Empty);
            }
```
改为：
```csharp
            var healthResult = await EnsureAsrRunningAsync(config, cancellationToken);
            if (!healthResult.Success)
            {
                await UpdateVideoSubtitleStateAsync(video, string.Empty, healthResult.Message);
                return (false, healthResult.Message, string.Empty);
            }
```

- [ ] **Step 3: 批量转写循环前同样接入（GenerateSubtitlesForVideosAsync，循环开始前）**

在该方法 `foreach`/循环体开始前插入（如变量名不同以实际为准）：
```csharp
            var ensureResult = await EnsureAsrRunningAsync(config, cancellationToken);
            if (!ensureResult.Success)
            {
                return (0, videos.Count);
            }
```

- [ ] **Step 4: 编译验证**

Run: `cd /d/dysync/dysync.net && /d/dotnet-sdk/dotnet.exe build dy.net.csproj -c Release 2>&1 | tail -5`
Expected: `0 个错误` / `Build succeeded`（warning CS1591 可忽略）

- [ ] **Step 5: 提交**

```bash
cd /d/dysync/dysync.net
git add service/LocalAsrSubtitleService.cs
git commit -m "feat: ASR 按需拉起等待(写 start.flag+轮询 health 最长180s)"
```
（带 Co-Authored-By 尾注）

---

### Task 4: compose 挂载 + 设置页 health 详情 + 部署

**Files:**
- Modify: `D:\dysync\docker-compose.yml`（volumes 增一行）
- Modify: `D:\dysync\dysync.net\Controllers\ConfigController.cs`（asr/health 透传新字段——若已透传整个 payload 则零改动，先查）
- Modify: `D:\dysync\dysync.net\app\src\pages\set\AppSet.vue`（ASR Status 区块）
- Modify: 部署产物（dll + 前端 dist + 镜像）

**Interfaces:**
- Consumes: Task 1 的 health 新字段；Task 3 的 `EnsureAsrRunningAsync`
- Produces: 设置页可显示 设备/GPU/显存/模型状态

- [ ] **Step 1: compose 加挂载**

`docker-compose.yml` 的 `volumes:` 列表末尾加：
```yaml
      # ASR 按需拉起信号桥(容器写 flag → 宿主 watcher 见 flag 拉起 ASR)
      - D:/dysync/asr-bridge:/app/asr-bridge
```

- [ ] **Step 2: 查 ConfigController 是否透传 health payload**

Run: `cd /d/dysync/dysync.net && grep -n -A 20 'asr/health' Controllers/ConfigController.cs | head -40`
- 若已把 ASR 响应整个转发（含新字段自动带出）→ 跳过 Step 3
- 若只挑字段组装 → 按 Step 3 补透传 `device/gpu_name/vram_total_mb/vram_used_mb/model_dir_exists`

- [ ] **Step 3:（仅当需要）ConfigController 补透传字段**

在 asr/health 组装匿名对象处，把 ASR 原始响应 JSON 用 `JsonDocument` 解析后附加：
```csharp
device = root.TryGetProperty("device", out var d) ? d.GetString() : null,
gpuName = root.TryGetProperty("gpu_name", out var g) ? g.GetString() : null,
vramTotalMb = root.TryGetProperty("vram_total_mb", out var t) ? t.GetInt32() : 0,
vramUsedMb = root.TryGetProperty("vram_used_mb", out var u) ? u.GetInt32() : 0,
modelDirExists = root.TryGetProperty("model_dir_exists", out var m) ? m.GetBoolean() : false,
```
（以现有代码风格为准，保持匿名对象属性命名 camelCase 输出）

- [ ] **Step 4: AppSet.vue ASR Status 区块增强**

现区块（:189-194 附近）扩展为（在 tag 行后追加详情行）：
```html
        <a-form-item label="ASR Status">
          <a-tag :color="asrHealthAvailable ? 'green' : 'red'">{{ asrHealthAvailable ? 'Online' : 'Offline' : '' }}</a-tag>
          <span>{{ asrHealthMessage }}</span>
          <a-button size="small" @click="checkAsrHealth" :loading="asrHealthLoading">Check ASR</a-button>
          <div v-if="asrHealthDetail" style="margin-top:4px;color:#888;font-size:12px;">
            设备: {{ asrHealthDetail.device || '-' }} ·
            GPU: {{ asrHealthDetail.gpu_name || '-' }} ·
            显存: {{ asrHealthDetail.vram_used_mb || 0 }}/{{ asrHealthDetail.vram_total_mb || 0 }} MB ·
            模型: <a-tag :color="asrHealthDetail.model_dir_exists ? 'green' : 'red')" size="small">{{ asrHealthDetail.model_dir_exists ? 'OK' : '缺失' }}</a-tag>
          </div>
        </a-form-item>
```
script 加：
```ts
const asrHealthDetail = ref<Record<string, any> | null>(null);
```
`checkAsrHealth` 成功分支加：
```ts
        asrHealthDetail.value = res.data || null;
```
（注意修正上面模板里 `'red')` 的笔误为 `'red'`；以现有 AppSet.vue 实际结构为准合入）

- [ ] **Step 5: 构建前端**

Run: `cd /d/dysync/dysync.net/app && npm run build 2>&1 | tail -3`
Expected: `✓ built in ...s`，无 vue-tsc 错误

- [ ] **Step 6: 部署后端+前端（既有链）**

```bash
cd /d/dysync/dysync.net
/d/dotnet-sdk/dotnet.exe publish dy.net.csproj -c Release -r linux-x64 --self-contained false -o /d/dysync/build-context/pub-asrondemand
docker cp /d/dysync/build-context/pub-asrondemand/dy.net.dll dysync2026:/app/dy.net.dll
# 前端
docker exec dysync2026 sh -c 'rm -rf /app/app/dist/assets /app/app/dist/index.html /app/app/dist/logo.png /app/app/dist/dist'
docker cp /d/dysync/dysync.net/app/dist/. dysync2026:/app/app/dist
docker exec dysync2026 sh -c 'if [ -d /app/app/dist/dist ]; then cd /app/app/dist && rm -rf assets index.html logo.png && cp -r dist/* ./ && rm -rf dist && echo 已展平; else echo 无嵌套; fi'
docker commit dysync2026 dysync:asr-local
cd /d/dysync && docker compose up -d --force-recreate
```
Expected: compose recreate 成功；`docker inspect` 容器 Image == 镜像 Id

- [ ] **Step 7: 全链路验收**

```bash
# 1) 杀 ASR 模拟冷启动
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object {(Get-Process -Id \$_.Id).Path -like '*envs\asr*'} | Stop-Process -Force"
# 2) dysync 触发字幕(douyin 登录→asr 接口)
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
curl -s -m 300 -X GET "http://localhost:10101/api/video/asr/2087553737416663040?overwrite=true" -H "Authorization: Bearer $TOKEN"
```
Expected: `code:0`；期间 watcher.log 出现 `[START]`/`[READY]`；整个链路 ≤3 分钟

- [ ] **Step 8: 提交 + 记忆更新**

```bash
cd /d/dysync/dysync.net
git add Controllers/ConfigController.cs app/src/pages/set/AppSet.vue
git commit -m "feat: 设置页 ASR health 详情(设备/GPU/显存/模型状态)"
```
更新记忆 `asr-service-location.md`：ASR 已按需启停（watcher+flag 契约、idle 30min、GPU_MEM_LIMIT 6.5、`ASR_IDLE_EXIT_MINUTES` env）；`docker-daemon-network-blocked.md` 无需动。

---

## Self-Review

1. **Spec 覆盖**：health 扩展✅(T1S5) 空闲自停✅(T1S3-4) 上限修正✅(T1S1,S8) watcher✅(T2) 拉起等待✅(T3) compose 挂载✅(T4S1) 设置页详情✅(T4S4) 长视频验证✅(T1S8) 负向测试✅(T2S6) 全链路验收✅(T4S7) conda 检查✅(watcher) GPU/CPU 降级✅(app.py 已有，health 报 device)
2. **占位符扫描**：Task 4 Step 3 标注「以现有代码风格为准」——这是条件步骤（可能零改动），已给完整代码模板，非占位。Task 3 Step 3 同理标注循环变量名以实际为准，代码完整。
3. **类型一致性**：`EnsureAsrRunningAsync` 签名 T3 定义=T3S2/S3 使用一致；health 字段名 T1 产出=T4S3/S4 消费一致（全小写 snake_case）；flag 路径 T2 宿主 `D:\dysync\asr-bridge\start.flag` ↔ T3 容器 `/app/asr-bridge/start.flag`，由 T4S1 挂载对齐。
