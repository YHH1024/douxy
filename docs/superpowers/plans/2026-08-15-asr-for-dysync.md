# 抖小云专属 ASR 服务 实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 复制 `D:\AI_Model\ASR\webapp` 到 `D:\ASR-For-Dysync\` 独立项目，加任务持久化+8 格看板+配置外部化，再用 conda-pack 产出可拷走双击运行的绿色便携包。

**Architecture:** 三步交付——①复制项目+config.json 配置化 ②任务记录（jobs.json 追加+`/api/jobs/list` 端点+面板「任务记录」tab）③便携包构建（conda-pack runtime + 启动bat）。原项目零改动；抖小云/watcher 仅改指向路径。

**Tech Stack:** Python 3.11 + FastAPI/uvicorn（现有）、原生 JS+CSS（现有 static）、conda-pack（打包）、Windows bat。

## Global Constraints

- 原项目 `D:\AI_Model\ASR\webapp` **只读**，任何文件不改动
- 新项目目录：`D:\ASR-For-Dysync\`；模型目录复制到 `D:\ASR-For-Dysync\Paraformer_model\`（868MB）
- jobs.json 记录字段（顺序固定）：`task_id,ts,source,file,status,cost_sec,token_count,error`；`source ∈ {dysync-sync, panel-upload, async-job}`
- 契约不变：`/api/health`、`/api/transcribe`、`/api/asr/submit|status` 全部保留（抖小云与 live-platform 零改动）
- 端口默认 8000，来自 `config.json`（`{"port":8000,"model_dir":"./Paraformer_model","idle_exit_minutes":30}`）
- 看板 8 格：今日成功/今日失败/当前任务/排队中（实时）+ 本周转换/本月转换/平均耗时/累计Token（汇总）；Token 口径=不含标点输出字数（同现有 `token_count`）
- 便携包产出 `D:\ASR-Portable\`，runtime 用 conda-pack 导出（本机一次性构建）
- 新项目代码用 git 管理：`D:\ASR-For-Dysync\` 里 `git init`（独立仓，不进 dysync 仓）

---

### Task 1: 复制项目 + config.json 配置化

**Files:**
- Create: `D:\ASR-For-Dysync\`（整个目录：app.py/asr_jobs.py/static/ 复制自原项目）
- Create: `D:\ASR-For-Dysync\config.json`
- Create: `D:\ASR-For-Dysync\启动ASR.bat`、`D:\ASR-For-Dysync\start_web.bat`
- Modify（新副本内）: `app.py` 常量区读 config

**Interfaces:**
- Produces: 新项目可独立启动（端口/模型路径/空闲退出由 config.json 控制）；`CONFIG` 全局 dict 供 Task 2/3 引用（键：`port`、`model_dir`、`idle_exit_minutes`）

- [ ] **Step 1: 复制文件（不含 __pycache__）**

```bash
mkdir -p /d/ASR-For-Dysync
cp /d/AI_Model/ASR/webapp/app.py /d/ASR-For-Dysync/
cp /d/AI_Model/ASR/webapp/asr_jobs.py /d/ASR-For-Dysync/
cp -r /d/AI_Model/ASR/webapp/static /d/ASR-For-Dysync/static
echo "复制完成:"; ls /d/ASR-For-Dysync/
```
Expected: 列出 app.py、asr_jobs.py、static。（模型目录 Task 1 最后一步单独复制，耗时长）

- [ ] **Step 2: 写 config.json**

```json
{
  "port": 8000,
  "model_dir": "./Paraformer_model",
  "idle_exit_minutes": 30
}
```

- [ ] **Step 3: 新副本 app.py 读配置（常量区改造）**

在 `import` 块之后、`MODEL_DIR = ...` 之前插入：
```python
# ==================== 外部配置(config.json) ====================
import json as _json
_BASE_DIR = os.path.dirname(os.path.abspath(__file__))
def _load_config():
    try:
        with open(os.path.join(_BASE_DIR, "config.json"), "r", encoding="utf-8") as f:
            return _json.load(f)
    except Exception:
        return {}
CONFIG = _load_config()
```
然后把原常量改为读 CONFIG（逐处修改）：
```python
MODEL_DIR = os.path.normpath(os.path.join(_BASE_DIR, CONFIG.get("model_dir", "./Paraformer_model")))
```
```python
IDLE_EXIT_MINUTES = int(os.environ.get("ASR_IDLE_EXIT_MINUTES", CONFIG.get("idle_exit_minutes", 30)))
```
（原行是 `IDLE_EXIT_MINUTES = int(os.environ.get("ASR_IDLE_EXIT_MINUTES", "30"))`，default 从 "30" 换成 CONFIG 值）
文件尾部 `uvicorn.run` 的 port 改 `CONFIG.get("port", 8000)`（若启动走 bat 传参则 bat 同步读 config，两处都改保持一致）。

- [ ] **Step 4: 写 启动ASR.bat（新项目版，读 config 端口）**

```bat
@echo off
chcp 65001 >nul
title Dysync ASR (Paraformer)
cd /d "%~dp0"

set "CONDA_ENV=C:\Users\admin\miniconda3\envs\asr"
set "PATH=%CONDA_ENV%\Library\bin;%CONDA_ENV%\Scripts;%CONDA_ENV%;%PATH%"

REM 从 config.json 读端口(用 python 一行解析,失败回退 8000)
set "PORT=8000"
for /f %%i in ('"%CONDA_ENV%\python.exe" -c "import json;print(json.load(open('config.json',encoding='utf-8')).get('port',8000))" 2^>nul') do set "PORT=%%i"

echo ============================================================
echo   Dysync ASR Service  (port %PORT%)
echo   browser: http://127.0.0.1:%PORT%
echo ============================================================

start "" /min cmd /c "timeout /t 8 /nobreak >nul & start http://127.0.0.1:%PORT%"
"%CONDA_ENV%\python.exe" -m uvicorn app:app --host 0.0.0.0 --port %PORT%
pause
```
另存一份内容相同、文件名 `start_web.bat`（watcher 兼容名，无 pause 也行——保持两个都提供）。

- [ ] **Step 5: 复制模型目录（868MB，耗时）**

```bash
cp -r /d/AI_Model/ASR/Paraformer_model /d/ASR-For-Dysync/Paraformer_model
ls /d/ASR-For-Dysync/Paraformer_model/model.pt && echo "模型复制完成"
```
Expected: `模型复制完成`

- [ ] **Step 6: 语法检查 + 独立启动验证（临时用别的端口避开旧服务）**

```bash
/c/Users/admin/miniconda3/envs/asr/python.exe -m py_compile /d/ASR-For-Dysync/app.py /d/ASR-For-Dysync/asr_jobs.py && echo COMPILE_OK
```
把 config.json 的 port 临时改 8001 → 用 Task 2 的方式后台启动 → `curl :8001/api/health` 200 → 改回 8000。
Expected: health 返回 `model_dir_exists:true`（相对路径解析正确）、`mem_limit_gb:6.5`

- [ ] **Step 7: git init + 首次提交**

```bash
cd /d/ASR-For-Dysync && git init 2>/dev/null
cat > .gitignore <<'EOF'
__pycache__/
data/
*.bak*
EOF
git add -A && git commit -m "init: 从 AI_Model/ASR/webapp 复制独立项目 + config.json 配置化"
```
（注：模型目录 868MB 建议排除——`.gitignore` 加 `Paraformer_model/`，commit 只含代码）

---

### Task 2: 任务记录——持久化 + API + 面板 tab

**Files:**
- Create: `D:\ASR-For-Dysync\job_store.py`（单一职责：jobs.json 追加写+查询+统计）
- Modify: `D:\ASR-For-Dysync\app.py`（transcribe/异步任务挂记录钩子；`/api/jobs/list` 端点；health 不变）
- Modify: `D:\ASR-For-Dysync\static\index.html` + `app.js`（顶部 tab + 任务记录页）

**Interfaces:**
- Consumes: Task 1 的 `CONFIG`、`_BASE_DIR`
- Produces: `job_store.record(source,file,status,cost_sec,token_count,error)`、`job_store.list_jobs(limit)->{"jobs":[...],"active":str|None,"queued":int,"stats":{...}}`；`GET /api/jobs/list?limit=100`

- [ ] **Step 1: 写 job_store.py**

```python
# -*- coding: utf-8 -*-
"""任务历史持久化:JSON Lines 追加写 + 尾部查询 + 汇总统计。"""
import json
import os
import threading
import time
from datetime import datetime, timedelta

_DATA_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "data")
_JOBS_FILE = os.path.join(_DATA_DIR, "jobs.json")
_MAX_BYTES = 10 * 1024 * 1024   # 超 10MB 保留尾部
_write_lock = threading.Lock()
_seq = 0


def _ensure_dir():
    os.makedirs(_DATA_DIR, exist_ok=True)


def record(source: str, file: str, status: int, cost_sec: float,
           token_count: int, error: str = None) -> None:
    """任务终态时调用:追加一行 JSON。status: 2=成功 3=失败(对齐 asr_jobs)。"""
    global _seq
    _seq += 1
    line = json.dumps({
        "task_id": int(time.time() * 1000) * 1000 + _seq % 1000,  # 粗略唯一
        "ts": datetime.now().strftime("%Y-%m-%dT%H:%M:%S"),
        "source": source,
        "file": os.path.basename(file or "")[:120],
        "status": status,
        "cost_sec": round(cost_sec or 0, 1),
        "token_count": token_count or 0,
        "error": (error or None) and str(error)[:300],
    }, ensure_ascii=False)
    with _write_lock:
        _ensure_dir()
        with open(_JOBS_FILE, "a", encoding="utf-8") as f:
            f.write(line + "\n")
        _rotate_if_needed()


def _rotate_if_needed():
    try:
        if os.path.getsize(_JOBS_FILE) > _MAX_BYTES:
            with open(_JOBS_FILE, "r", encoding="utf-8") as f:
                lines = f.readlines()
            with open(_JOBS_FILE, "w", encoding="utf-8") as f:
                f.writelines(lines[-5000:])   # 保留尾部 5000 条
    except OSError:
        pass


def _read_all():
    if not os.path.exists(_JOBS_FILE):
        return []
    out = []
    with open(_JOBS_FILE, "r", encoding="utf-8") as f:
        for ln in f:
            ln = ln.strip()
            if not ln:
                continue
            try:
                out.append(json.loads(ln))
            except json.JSONDecodeError:
                continue
    return out


def list_jobs(limit: int = 100, active: str = None, queued: int = 0) -> dict:
    """倒序返回尾部 limit 条 + 实时态 + 汇总统计。"""
    all_jobs = _read_all()
    now = datetime.now()
    today0 = now.replace(hour=0, minute=0, second=0, microsecond=0)
    # 本周一 00:00(周一为一周开始)
    monday0 = today0 - timedelta(days=today0.weekday())
    month0 = today0.replace(day=1)

    def _cnt(d0):
        return sum(1 for j in all_jobs if j.get("ts", "") >= d0.strftime("%Y-%m-%dT%H:%M:%S"))

    ok_jobs = [j for j in all_jobs if j.get("status") == 2]
    today_jobs = [j for j in all_jobs if j.get("ts", "") >= today0.strftime("%Y-%m-%dT%H:%M:%S")]
    today_ok = [j for j in today_jobs if j.get("status") == 2]
    avg = round(sum(j.get("cost_sec", 0) for j in ok_jobs) / len(ok_jobs), 1) if ok_jobs else 0
    stats = {
        "today_ok": len(today_ok),
        "today_fail": len(today_jobs) - len(today_ok),
        "week_cnt": _cnt(monday0),
        "month_cnt": _cnt(month0),
        "avg_cost": avg,
        "total_tokens": sum(j.get("token_count", 0) for j in ok_jobs),
    }
    return {"jobs": all_jobs[::-1][:limit], "active": active, "queued": queued, "stats": stats}
```

- [ ] **Step 2: app.py 挂同步转写记录（transcribe 端点）**

顶部 `import job_store`。`transcribe` 端点改造（包住推理与异常）：
```python
    extracted = False
    audio_path = tmp_in
    _t0 = time.time()
    try:
        if suffix in VIDEO_EXTS:
            audio_path = extract_audio(tmp_in)
            extracted = True
        result = await asyncio.to_thread(_run_asr, audio_path)
        result["filename"] = file.filename
        result["size"] = len(content)
        job_store.record("dysync-sync", file.filename, 2,
                         time.time() - _t0, result.get("token_count", 0))
        return JSONResponse(result)
    except Exception as e:
        job_store.record("dysync-sync", file.filename, 3, time.time() - _t0, 0, str(e))
        raise HTTPException(status_code=500, detail=str(e))
```
（`time` 已 import。`dysync-sync` 来源是抖小云走的同步路径；面板手动上传走同一端点——source 用 `file.filename` 无法区分，统一记 `dysync-sync` 可接受，或加 query 参数 `?source=panel-upload` 由前端传——**采用后者**：`async def transcribe(file: UploadFile = File(...), source: str = "dysync-sync")`，record 用该变量；面板 app.js 上传 URL 加 `?source=panel-upload`）

- [ ] **Step 3: app.py 挂异步任务记录（processor 完成处）**

`_process_asr_job`（app.py 内 processor 函数）返回前与 except 处各加一行（按实际函数结构调整，拿到 url 文件名与耗时）：
```python
        job_store.record("async-job", url.rsplit("/", 1)[-1], 2, cost, len(text or ""))
```
失败分支：
```python
        job_store.record("async-job", url.rsplit("/", 1)[-1], 3, cost, 0, str(e))
```
（若 processor 无法拿到精确 token 数，用 `len(text)` 代替——异步路径抖小云不用，精度可接受）

- [ ] **Step 4: app.py 加 /api/jobs/list 端点（health 端点后）**

```python
@app.get("/api/jobs/list")
async def jobs_list(limit: int = 100):
    """任务记录:尾部 limit 条 + 实时态(active/queued) + 汇总统计。"""
    import asr_jobs as _aj
    active_file = None
    queued = 0
    try:
        with _aj._jobs_lock:
            for j in _aj._jobs.values():
                if j["status"] == 1:
                    active_file = (j.get("url") or "?").rsplit("/", 1)[-1]
                    break
            queued = sum(1 for j in _aj._jobs.values() if j["status"] == 0)
    except Exception:
        pass
    return job_store.list_jobs(limit=limit, active=active_file, queued=queued)
```
（同步转写进行中不算在 active 里——瞬时态，轮询到时通常已结束，可接受）

- [ ] **Step 5: static 面板加「任务记录」tab（index.html）**

在右列 `tab-panel` 的 `.tabs` 里，「🌐 翻译」按钮后加：
```html
          <button class="tab" data-tab="jobs">📋 任务记录</button>
```
`tab-body` 末尾加 pane：
```html
          <div class="tab-pane" id="pane-jobs">
            <div class="tgrid" style="margin-bottom:10px;">
              <div class="tcard"><div class="k">今日成功</div><div class="v" style="color:#4caf50"><span id="jTodayOk">0</span></div></div>
              <div class="tcard"><div class="k">今日失败</div><div class="v" style="color:#f44336"><span id="jTodayFail">0</span></div></div>
              <div class="tcard"><div class="k">当前任务</div><div class="v" style="font-size:14px" id="jActive">—</div></div>
              <div class="tcard"><div class="k">排队中</div><div class="v"><span id="jQueued">0</span></div></div>
              <div class="tcard"><div class="k">本周转换<small>(含失败)</small></div><div class="v"><span id="jWeek">0</span></div></div>
              <div class="tcard"><div class="k">本月转换<small>(含失败)</small></div><div class="v"><span id="jMonth">0</span></div></div>
              <div class="tcard"><div class="k">平均耗时</div><div class="v"><span id="jAvg">0</span><small> s</small></div></div>
              <div class="tcard"><div class="k">累计Token</div><div class="v"><span id="jTokens">0</span></div></div>
            </div>
            <div class="jobs-table" id="jobsTable">
              <div class="jobs-empty">暂无任务记录</div>
            </div>
          </div>
```

- [ ] **Step 6: app.js 加任务页逻辑（文件末尾）**

```javascript
// ==================== 任务记录 tab ====================
let _jobsTimer = null;

async function refreshJobs() {
  try {
    const r = await fetch('/api/jobs/list?limit=100');
    const d = await r.json();
    const s = d.stats || {};
    document.getElementById('jTodayOk').textContent = s.today_ok ?? 0;
    document.getElementById('jTodayFail').textContent = s.today_fail ?? 0;
    document.getElementById('jWeek').textContent = s.week_cnt ?? 0;
    document.getElementById('jMonth').textContent = s.month_cnt ?? 0;
    document.getElementById('jAvg').textContent = s.avg_cost ?? 0;
    document.getElementById('jTokens').textContent = (s.total_tokens ?? 0).toLocaleString();
    const act = document.getElementById('jActive');
    act.textContent = d.active ? (d.active.length > 18 ? d.active.slice(0, 16) + '…' : d.active) : '—';
    act.title = d.active || '';
    document.getElementById('jQueued').textContent = d.queued ?? 0;

    const box = document.getElementById('jobsTable');
    const jobs = d.jobs || [];
    if (!jobs.length) { box.innerHTML = '<div class="jobs-empty">暂无任务记录</div>'; return; }
    let html = '<table class="jt"><tr><th>状态</th><th>文件名</th><th>耗时</th><th>Token</th><th>时间</th></tr>';
    for (const j of jobs) {
      const ok = j.status === 2;
      const t = (j.ts || '').replace(/^\d{4}-/, '').replace('T', ' ').slice(0, 11);
      const err = j.error ? ` title="${String(j.error).replace(/"/g, '&quot;')}"` : '';
      html += `<tr class="${ok ? '' : 'fail'}"><td>${ok ? '✅' : `<span${err} style="cursor:help">❌</span>`}</td>` +
        `<td class="jf" title="${String(j.file || '').replace(/"/g, '&quot;')}">${j.file || ''}</td>` +
        `<td>${j.cost_sec}s</td><td>${(j.token_count || 0).toLocaleString()}</td><td>${t}</td></tr>`;
    }
    box.innerHTML = html + '</table>';
  } catch (e) { /* 静默,下轮重试 */ }
}

// 切到任务 tab 时启动 10s 轮询,离开时停(复用现有 tab 切换事件)
document.querySelectorAll('.tabs .tab').forEach(btn => {
  btn.addEventListener('click', () => {
    if (btn.dataset.tab === 'jobs') {
      refreshJobs();
      if (!_jobsTimer) _jobsTimer = setInterval(refreshJobs, 10000);
    } else if (_jobsTimer) {
      clearInterval(_jobsTimer); _jobsTimer = null;
    }
  });
});
```
另：现有上传函数的请求 URL 从 `/api/transcribe` 改 `/api/transcribe?source=panel-upload`（找到 `fetch('/api/transcribe'` 或 FormData 提交处）。

- [ ] **Step 7: style.css 加任务表样式（文件末尾）**

```css
/* ===== 任务记录 ===== */
.jobs-table { max-height: 320px; overflow-y: auto; }
.jobs-empty { color: #888; text-align: center; padding: 24px 0; }
table.jt { width: 100%; border-collapse: collapse; font-size: 12px; }
table.jt th { text-align: left; color: #9aa; padding: 6px 8px; border-bottom: 1px solid #333; position: sticky; top: 0; background: #161a22; }
table.jt td { padding: 6px 8px; border-bottom: 1px solid #222; color: #cdd; }
table.jt td.jf { max-width: 260px; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
table.jt tr.fail td { color: #f88; background: rgba(244,67,54,.06); }
```
（颜色按现有深色主题微调，变量可用 style.css 里已有的）

- [ ] **Step 8: 端到端验证（临时端口 8001 起，避开旧服务）**

```bash
# 起服务(8001)
powershell.exe -NoProfile -Command "
\$env:PATH = 'C:\Users\admin\miniconda3\envs\asr\Library\bin;C:\Users\admin\miniconda3\envs\asr\Scripts;C:\Users\admin\miniconda3\envs\asr;' + \$env:PATH
Start-Process 'C:\Users\admin\miniconda3\envs\asr\python.exe' -ArgumentList '-m','uvicorn','app:app','--host','0.0.0.0','--port','8001' -WorkingDirectory 'D:\ASR-For-Dysync' -RedirectStandardOutput 'D:\dysync\asr_new.log' -RedirectStandardError 'D:\dysync\asr_new.err.log' -WindowStyle Minimized"
sleep 90
# 1) health
curl -s http://127.0.0.1:8001/api/health | python -m json.tool | grep -E "model_dir|mem_limit"
# 2) 转一条(用已有视频)
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
curl -s -m 300 "http://localhost:10101/api/video/asr/2087553737416663040?overwrite=true" -H "Authorization: Bearer $TOKEN" | head -c 200
# 3) 任务记录出现
curl -s "http://127.0.0.1:8001/api/jobs/list?limit=10" | python -m json.tool | head -30
```
Expected: ①`model_dir_exists:true` ②dysync 转写 code:0（注意：**此步会转写走旧服务 8000**——验证 dysync 集成需 Task 4 切换后；本步改为**直接 curl 上传文件到 8001** 模拟：`curl -s -F "file=@某.mp4" http://127.0.0.1:8001/api/transcribe`）③jobs/list 返回该条记录含 token_count
（更正：②用 `curl -s -F "file=@/d/dysync/data/favorite/Ruka/.../xxx.mp4" "http://127.0.0.1:8001/api/transcribe?source=dysync-sync"` 直传验证记录链路）
- 面板：浏览器开 `http://127.0.0.1:8001/` 点「📋 任务记录」tab 看到 8 格+表格

- [ ] **Step 9: 提交（新项目仓）**

```bash
cd /d/ASR-For-Dysync && git add -A && git commit -m "feat: 任务记录持久化(job_store)+/api/jobs/list+面板任务tab(8格统计)"
```

---

### Task 3: watcher 切换 + 抖小云全链路验证

**Files:**
- Modify: `D:\dysync\asr-bridge\watcher.ps1`（`$WorkDir`/`$Py` 不变，但拉起命令指到新目录——实际只需改 `$WorkDir = "D:\ASR-For-Dysync"` 与 `$OutLog/$ErrLog` 路径、`$ModelPt` 指新模型目录）

- [ ] **Step 1: 改 watcher.ps1 四个变量**

```powershell
$WorkDir     = "D:\ASR-For-Dysync"
$ModelPt     = "D:\ASR-For-Dysync\Paraformer_model\model.pt"
$OutLog      = "D:\dysync\asr_service.log"      # 不变
$ErrLog      = "D:\dysync\asr_service.err.log"  # 不变
```
（`$Py` 仍指向 conda env——便携化在 Task 4，本步先让按需启停用新目录）

- [ ] **Step 2: 重启 watcher**

```bash
powershell.exe -NoProfile -Command "Get-CimInstance Win32_Process -Filter \"Name='powershell.exe'\" | Where-Object { \$_.CommandLine -like '*watcher.ps1*' } | ForEach-Object { Stop-Process -Id \$_.ProcessId -Force }
Start-Sleep 2
Start-Process powershell.exe -ArgumentList '-WindowStyle','Hidden','-ExecutionPolicy','Bypass','-File','D:\dysync\asr-bridge\watcher.ps1' -WindowStyle Hidden"
sleep 3; tail -2 /d/dysync/asr-bridge/watcher.log
```
Expected: log 出现新的「watcher 启动」行

- [ ] **Step 3: 杀旧服务 + 全链路验证**

```bash
# 停旧(8000)与新(8001)
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*envs\asr*' } | Stop-Process -Force"
sleep 2
# 抖小云触发生成字幕(会自动经 flag 拉起新目录服务)
TOKEN=$(curl -s -X POST "http://localhost:10101/api/Auth/Login" -H 'Content-Type: application/json' -d '{"UserName":"douyin","Password":"douyin2026"}' | python -c "import sys,json;print(json.load(sys.stdin).get('token',''))")
T0=$(date +%s)
curl -s -m 300 "http://localhost:10101/api/video/asr/2087553737416663040?overwrite=true" -H "Authorization: Bearer $TOKEN" | head -c 200
echo; echo "耗时 $(( $(date +%s) - T0 ))s"
# 任务面板有记录
curl -s "http://127.0.0.1:8000/api/jobs/list?limit=5" | python -m json.tool | head -20
```
Expected: dysync 转写 code:0；`http://127.0.0.1:8000/` 现在是新服务（面板有「任务记录」tab）；jobs/list 含该任务；总耗时 ≤60s（冷启动+转写）

- [ ] **Step 4: 重启持久化验证**

```bash
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*envs\asr*' } | Stop-Process -Force"
sleep 3; echo "want" > /d/dysync/asr-bridge/start.flag
# 等 watcher 拉起后
for i in $(seq 1 24); do sleep 5; CODE=$(curl -s -m 3 -o /dev/null -w "%{http_code}" http://127.0.0.1:8000/api/health); [ "$CODE" = "200" ] && break; done
curl -s "http://127.0.0.1:8000/api/jobs/list?limit=5" | python -c "import sys,json;d=json.load(sys.stdin);print('重启后任务数:',len(d['jobs']))"
```
Expected: 重启后 jobs 数不变（json 持久化生效）

- [ ] **Step 5: 提交 + 记忆更新**

watcher.ps1 不在 git（asr-bridge 目录非仓），改动直接生效。更新记忆 `asr-service-location.md`：服务已迁至 `D:\ASR-For-Dysync\`（原项目保留给 live-platform）、面板新地址同端口、jobs.json 位置。

---

### Task 4: 绿色便携包构建

**Files:**
- Create: `D:\ASR-For-Dysync\deploy\make_portable.bat`
- Create: `D:\ASR-For-Dysync\deploy\启动ASR-Portable.bat`（便携包专用启动器模板，runtime 相对路径）

**Interfaces:**
- Consumes: Task 1-3 的新项目（代码+config+模型）
- Produces: `D:\ASR-Portable\`（可整目录拷走的完整包）

- [ ] **Step 1: conda-pack 导出 runtime（一次性，~10-20 分钟）**

```bash
/c/Users/admin/miniconda3/envs/asr/python.exe -m pip install conda-pack 2>&1 | tail -1
/c/Users/admin/miniconda3/envs/asr/Scripts/conda-pack.exe -n asr -o /d/dysync/build-context/asr-runtime.tar --n-threads 4 --ignore-editable-packages 2>&1 | tail -3
ls -lh /d/dysync/build-context/asr-runtime.tar
```
Expected: tar 文件 ~2.5-3.5GB（torch+cu121）。失败时看输出——常见是 pip 元数据问题，加 `--ignore-missing-files` 重试。

- [ ] **Step 2: 写 make_portable.bat**

```bat
@echo off
chcp 65001 >nul
setlocal
REM ===== 构建抖小云 ASR 绿色便携包 =====
set "SRC=D:\ASR-For-Dysync"
set "OUT=D:\ASR-Portable"
set "RUNTIME_TAR=D:\dysync\build-context\asr-runtime.tar"

if not exist "%RUNTIME_TAR%" ( echo [ERR] 找不到 runtime 包: %RUNTIME_TAR% & pause & exit /b 1 )

echo [1/4] 清理并建输出目录 %OUT% ...
rmdir /s /q "%OUT%" 2>nul
mkdir "%OUT%" "%OUT%\runtime" "%OUT%\data"

echo [2/4] 复制代码与配置 ...
copy "%SRC%\app.py" "%OUT%\" >nul
copy "%SRC%\asr_jobs.py" "%OUT%\" >nul
copy "%SRC%\job_store.py" "%OUT%\" >nul
copy "%SRC%\config.json" "%OUT%\" >nul
xcopy "%SRC%\static" "%OUT%\static\" /e /i >nul

echo [3/4] 复制模型(868MB) ...
xcopy "%SRC%\Paraformer_model" "%OUT%\Paraformer_model\" /e /i >nul

echo [4/4] 解压 runtime(约 3GB,需几分钟) ...
tar -xf "%RUNTIME_TAR%" -C "%OUT%\runtime"
if errorlevel 1 ( echo [ERR] runtime 解压失败 & pause & exit /b 1 )

copy "%SRC%\deploy\启动ASR-Portable.bat" "%OUT%\启动ASR.bat" >nul

echo ============================================
echo   便携包构建完成: %OUT%
echo   整个目录可拷到任意 Windows 机器双击 启动ASR.bat
echo ============================================
pause
```

- [ ] **Step 3: 写便携版启动器（deploy/启动ASR-Portable.bat）**

```bat
@echo off
chcp 65001 >nul
title Dysync ASR Portable
cd /d "%~dp0"

REM 全部相对路径——整个目录拷哪都能跑
set "RT=%~dp0runtime"
set "PATH=%RT%\Library\bin;%RT%\Scripts;%RT%;%PATH%"

set "PORT=8000"
for /f %%i in ('"%RT%\python.exe" -c "import json;print(json.load(open('config.json',encoding='utf-8')).get('port',8000))" 2^>nul') do set "PORT=%%i"

echo ============================================================
echo   Dysync ASR Portable  (port %PORT%)
echo   本目录: %~dp0
echo   browser: http://127.0.0.1:%PORT%
echo ============================================================

start "" /min cmd /c "timeout /t 8 /nobreak >nul & start http://127.0.0.1:%PORT%"
"%RT%\python.exe" -m uvicorn app:app --host 0.0.0.0 --port %PORT%
pause
```

- [ ] **Step 4: 执行构建**

```bash
powershell.exe -NoProfile -Command "Start-Process cmd -ArgumentList '/c','D:\ASR-For-Dysync\deploy\make_portable.bat' -Wait" 2>&1 | tail -3
du -sh /d/ASR-Portable 2>/dev/null
```
Expected: `D:\ASR-Portable\` 总体积 ~4GB

- [ ] **Step 5: 便携包验证（模拟新机器：先停本机所有 ASR）**

```bash
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*envs\asr*' -or \$_.Path -like '*ASR-Portable*' } | Stop-Process -Force"
# 便携包 config 改 8002 避开
python -c "import json;p=r'D:\ASR-Portable\config.json';c=json.load(open(p));c['port']=8002;json.dump(c,open(p,'w'))"
# 双击等价:后台启动便携包
powershell.exe -NoProfile -Command "
\$rt='D:\ASR-Portable\runtime'
\$env:PATH=\"\$rt\Library\bin;\$rt\Scripts;\$rt;\" + \$env:PATH
Start-Process \"\$rt\python.exe\" -ArgumentList '-m','uvicorn','app:app','--host','0.0.0.0','--port','8002' -WorkingDirectory 'D:\ASR-Portable' -RedirectStandardOutput 'D:\dysync\asr_portable.log' -RedirectStandardError 'D:\dysync\asr_portable.err.log' -WindowStyle Minimized"
# 等就绪并验证
for i in $(seq 1 30); do sleep 5; CODE=$(curl -s -m 3 -o /dev/null -w "%{http_code}" http://127.0.0.1:8002/api/health); [ "$CODE" = "200" ] && { echo "✅ 便携包服务就绪(${i}x5s)"; break; }; done
curl -s http://127.0.0.1:8002/api/health | python -m json.tool | grep -E "device|model_dir"
# 转写一条(直传)
curl -s -m 120 -F "file=@某个测试.mp4" "http://127.0.0.1:8002/api/transcribe?source=panel-upload" | head -c 150
curl -s "http://127.0.0.1:8002/api/jobs/list?limit=3" | python -m json.tool | head -15
```
Expected: 便携包独立就绪（不依赖 conda）、GPU 可用（device:cuda）、转写成功、任务记录正常

- [ ] **Step 6: 清理验证端口 + 提交 + 记忆**

```bash
powershell.exe -NoProfile -Command "Get-Process python -ErrorAction SilentlyContinue | Where-Object { \$_.Path -like '*ASR-Portable*' } | Stop-Process -Force"
python -c "import json;p=r'D:\ASR-Portable\config.json';c=json.load(open(p));c['port']=8000;json.dump(c,open(p,'w'))"
cd /d/ASR-For-Dysync && git add -A && git commit -m "feat: 绿色便携包构建脚本(make_portable+便携启动器)"
```
更新记忆：便携包位置/体积/构建方法/新机器使用方式（拷目录→双击启动ASR.bat→改抖小云 AsrServiceUrl）。

---

## Self-Review

1. **Spec 覆盖**：复制独立✅T1 配置化✅T1S2-3 任务持久化✅T2S1 同步/异步/面板三路记录✅T2S2-3 jobs/list✅T2S4 面板8格+表格✅T2S5-7 watcher切换✅T3S1-2 全链路✅T3S3 重启持久✅T3S4 便携包✅T4S1-5 新机器模拟✅T4S5(8002独立起) 抖小云零改动✅(契约不变,仅watcher路径) 原项目零改动✅(全程只读) GPU休眠✅(无需新工作,既有机制)
2. **占位符**：T2S3 异步记录「按实际函数结构调整」——给了完整代码模板与字段说明，属条件适配非占位；T2S8 的验证方式自纠错已内联（②改为直传验证）
3. **类型一致**：`job_store.record(source,file,status,cost_sec,token_count,error)` T2S1 定义=T2S2/S3 调用一致；`list_jobs(limit,active,queued)` 返回结构 T2S1=T2S4/S6 消费一致（jobs/active/queued/stats 与前端 id 对应：jTodayOk←today_ok 等）；`CONFIG` 键名 T1S3=T1S4/T4S3 一致；watcher 变量名 T3S1 与现有 watcher.ps1 一致（$WorkDir/$ModelPt/$OutLog/$ErrLog）
