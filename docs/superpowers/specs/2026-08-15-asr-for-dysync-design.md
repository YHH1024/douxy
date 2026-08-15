# 抖小云专属 ASR 服务设计（独立复制版）

日期：2026-08-15
分支：`asr-windows-test`（文档）；代码主体在 `D:\ASR-For-Dysync\`（新目录，不入 dysync 仓）
前置：复用已上线的按需启停机制（watcher + start.flag，见 docs/superpowers/specs/2026-08-15-asr-on-demand-integration-design.md）

## 背景与目标

用户需要：① 抖小云专属的 ASR 任务面板与转换记录；② ASR 可复制到其他 Windows 机器双击运行。决策：从 `D:\AI_Model\ASR\webapp` **复制独立新项目**（原项目零改动，live-platform 的异步任务接口不受影响）；打包形态用**绿色便携包**（否决 PyInstaller exe：torch+cu121 极易失败、3-4GB 产物、杀软误报）；任务面板做在 **ASR 自带 static 面板**（不动抖小云前端）。

GPU 休眠诉求已被现有闲置退出机制满足（实测：ASR 退出后显存 2016→725MB，P8 最低功耗态；剩余为桌面图形基线，不可释放）——本设计不新增相关工作。

## 新项目结构

```
D:\ASR-For-Dysync\
 ├─ 启动ASR.bat            ← 双击运行（PATH 修正 + 启动 + 浏览器打开面板）
 ├─ start_web.bat          ← 保留原名兼容 watcher
 ├─ app.py                 ← 复制改造：读 config.json、记录任务、暴露 /api/jobs/list
 ├─ asr_jobs.py            ← 复制改造：任务完成/失败时追加写 data/jobs.json
 ├─ static/                ← 复制改造：面板加「任务记录」tab
 ├─ config.json            ← 新增：{ port, model_dir, idle_exit_minutes }
 ├─ data/jobs.json         ← 新增：任务历史（JSON Lines 追加写）
 ├─ Paraformer_model/      ← 复制（868MB）
 └─ deploy\make_portable.bat ← 便携包构建脚本
```

## 改造点

### ① 任务持久化（asr_jobs.py + app.py）

- 内存任务表保留（热数据 + 兼容 live-platform 轮询契约不改）
- 任务终态（success/failed）时追加一行 JSON 到 `data/jobs.json`：
  ```json
  {"task_id":123,"ts":"2026-08-15T15:30:00","source":"dysync","file":"视频名.mp4","status":2,"cost_sec":12.4,"token_count":856,"error":null}
  ```
- 同步转写（`POST /api/transcribe`，抖小云走的路径）也记录：app.py 在转写完成后直接追加同样结构（source 区分 `dysync-sync`/`panel-upload`/`async-job`）
- 新端点 `GET /api/jobs/list?limit=100`：读 jobs.json 尾部 N 条（倒序），带统计（今日成功/失败/平均耗时）
- 文件锁：追加写用单线程队列（复用现有 _jobs_lock 思路），读端点只读文件不抢锁

### ② 面板任务页（static/）

- 现有面板（GPU 监控 + 手动转写）收纳进 tab：「GPU 监控」/「上传转写」/「任务记录」（现有功能全部保留）
- **任务记录 tab 统计卡 8 格分两排**：
  - 实时排（内存任务表，秒级可感）：今日成功 / 今日失败 / 当前任务（🔵转写中+文件名截断hover，空闲显—）/ 排队中（0 灰显）
  - 汇总排（jobs.json，10 秒轮询）：本周转换（周一至今，含失败，卡下小字注明）/ 本月转换（月初至今）/ 平均耗时 / 累计 Token
- **任务表格**：状态（✅绿/❌红，失败行淡红底，错误信息 hover tooltip）、文件名、耗时、**Token 数**（口径同现有面板 token_count：不含标点的输出字数）、时间（今天显时分/隔天显日期）；`/api/jobs/list?limit=100` 倒序，最新在上，10 秒自动刷新
- 响应含 `active`（当前任务文件名或 null）/`queued`（排队数）字段供实时排使用

### ③ 配置外部化（config.json + app.py）

```json
{ "port": 8000, "model_dir": "./Paraformer_model", "idle_exit_minutes": 30 }
```
- `app.py` 启动读此文件（缺失用默认值）；`model_dir` 相对路径基于 app.py 所在目录解析（便携部署关键）
- `启动ASR.bat` / `start_web.bat` 从 config.json 读端口（bat 内用 python 一行解析，避免 bat 解析 JSON 的脏活）
- 按需启停机制原样保留：宿主 watcher 的拉起目标改为新目录 bat（watcher.ps1 改一个 `$WorkDir` 变量）

### ④ 便携包构建（deploy/make_portable.bat）

产出 `D:\ASR-Portable\`（可整目录拷走）：
```
ASR-Portable\
 ├─ 启动ASR.bat
 ├─ app.py / asr_jobs.py / static/ / config.json
 ├─ Paraformer_model\
 ├─ runtime\            ← python-embed 免安装运行时
 └─ data\               ← 空，运行时生成
```
- runtime 来源：**conda-pack 导出**（`pip install conda-pack` 进 asr env → `conda-pack -n asr -o runtime.tar` → 解压），这是 torch+cuda 环境最可靠的迁移方式；首次构建在本机完成
- `启动ASR.bat` 便携版：`set PATH=%~dp0runtime;%~dp0runtime\Scripts;%~dp0runtime\Library\bin;%PATH%` + `runtime\python.exe -m uvicorn app:app ...`
- 构建脚本做体积提示（预计 3-4GB），不改任何源文件

## 与抖小云的衔接（零改动）

- 新服务同端口 8000、同契约（/api/health、/api/transcribe、/api/asr/*），抖小云 `AsrServiceUrl` 不变
- watcher.ps1 只改拉起目标路径（`$WorkDir`、`$Py` 指向新目录）
- 切换时机：新目录验证通过后，停旧服务、改 watcher 路径、写 flag 拉起新服务——抖小云无感知

## 不做（YAGNI）

- 不做用户系统/多租户/远程访问鉴权（本机工具）
- 不做任务重跑/取消（记录只读）
- 不做数据库（JSON Lines 够用，超 10MB 滚动截断保留尾部）
- 不做 exe 单文件（否决理由见背景）
- 不动原项目 `D:\AI_Model\ASR\webapp`（live-platform 继续用）

## 验证标准

1. `D:\ASR-For-Dysync\启动ASR.bat` 双击 → 服务起在 8000 → 面板开 → GPU 监控正常
2. 抖小云点「生成字幕」→ 走新服务出字幕成功 → 面板「任务记录」出现该任务（状态/耗时/字数）
3. 重启 ASR 服务 → 面板历史任务仍在（jobs.json 持久化）
4. 失败任务（构造超长音频）→ 面板红字 + error tooltip
5. watcher 指向新目录后按需启停链路完整（杀进程 → 抖小云触发 → 自动拉起 → 转写成功）
6. `make_portable.bat` 产出便携包 → 复制到 `D:\ASR-Portable-Test\`（模拟新机）→ 双击启动 → 转写一条成功
7. 原项目 live-platform 的异步接口在新服务上仍可用（submit/status 轮询）

## 风险与对策

- conda-pack 体积大/耗时长（一次性 ~3GB 打包）→ 构建脚本给进度提示；失败可重跑
- 便携包在无 NVIDIA 驱动机器跑 → app.py 已有 CPU 降级（torch.cuda 不可用落 CPU），面板显示 device:cpu
- 两套 ASR 并存期端口冲突（旧服务若也在 8000）→ 切换流程明确"先停旧再启新"；便携包默认端口可改 config.json
- jobs.json 并发写 → 单线程追加（转写本就串行：_INFER_LOCK）
