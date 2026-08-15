# ASR 按需启停集成设计（云 API 模式）

日期：2026-08-15
分支：`asr-windows-test`
范围：ASR 服务增强（app.py）、宿主 watcher（新建）、dysync 后端/前端/部署配置

## 背景与目标

dysync（抖小云）与本地 Paraformer ASR 是两个独立进程，靠 HTTP 契约相连（`GET /api/health`、`POST /api/transcribe`）。现状问题：ASR 忘启动 → 字幕任务全失败（报 `ASR request failed with 500`/连接拒绝），用户不知道要先开 `start_web.bat`。

目标：**dysync 用到字幕时 ASR 自动就位，闲置自动退出释放显存**；同时启动/调用前能检查 GPU 与模型是否可用。

## 架构原则：云 API 模式

- ASR = 本地私有云服务：无状态纯转写，输入音频文件、输出文本+时间戳（响应体即回流，不落盘不回调）
- dysync = 唯一调用方与数据所有者：字幕生成/存储（.srt/.txt）/状态/展示全部由 dysync 管
- 部署拓扑透明：同机（host.docker.internal）或局域网分离（http://<ASR机IP>:8000）只差 `AsrServiceUrl` 配置，dysync 代码不变

## 三组件设计

### ① ASR 侧：`D:\AI_Model\ASR\webapp\app.py` 增强

**能力上报**——`/api/health` 响应扩展字段：
```json
{
  "available": true,
  "device": "cuda",            // cuda | cpu（启动时 torch.cuda.is_available() 判定，已有逻辑）
  "gpu_name": "RTX 2060 SUPER",
  "vram_total_mb": 8192,
  "vram_used_mb": 3569,
  "model_loaded": true,        // 模型是否已加载完成
  "model_dir_exists": true     // Paraformer_model 目录与关键文件(model.pt/tokens.json)是否存在
}
```
- `device/gpu_name/vram_*` 复用现有 pynvml 监控代码
- `model_dir_exists` 启动时检查 `MODEL_DIR` 下 `model.pt`、`tokens.json` 存在性；缺失时启动日志明确报错、health 如实上报、transcribe 返回 `模型文件缺失` 明确错误（而非隐晦 500）

**空闲自停**：
- 记录 `LAST_REQUEST_TS`（每次请求中间件更新）
- 后台定时器每分钟检查：`now - LAST_REQUEST_TS > 30min` → 写日志 → `os._exit(0)` 退出释放显存
- 启动参数 `--idle-exit-minutes` 可调（默认 30，0=禁用）
- 退出后由 watcher 按需再拉起，dysync 调用侧有等待重试兜底

**显存上限修正**：`GPU_MEM_LIMIT_GB` 3.5 → 6.5。原 3.5 是给已停跑的 LLM(Qwen,8001) 预留的；现状 8GB 卡空闲 4.6GB，3.5 上限导致长视频转写被误杀（2026-08-15 实锤根因）。留 ~1.5GB 给系统图形进程。

### ② 宿主侧：`D:\dysync\asr-bridge\watcher.ps1`（新建）+ 计划任务

常驻极小进程（登录自启、隐藏窗口、崩溃由计划任务重启）：

```
循环（1s）：
  1. 检测 D:\dysync\asr-bridge\start.flag 存在？
     ├─ 检查 conda python 存在（C:\Users\admin\miniconda3\envs\asr\python.exe）
     ├─ 检查模型目录存在（D:\AI_Model\ASR\Paraformer_model\model.pt）
     ├─ 检查 :8000 未被占用
     ├─ 全过 → Start-Process 后台拉起 uvicorn（stdout/stderr 重定向到 D:\dysync\asr_service*.log）
     └─ 任一失败 → 写 watcher 日志（明确原因），不拉起
     最后删 start.flag
  2. 检测 ASR 进程健康？(仅当拉起后 3 分钟内轮询 health，超时记日志)
```
- watcher 不主动重启自杀的 ASR（按需语义：等下一个 flag）
- 日志：`D:\dysync\asr-bridge\watcher.log`（追加、按大小轮转 >1MB 清空）
- 计划任务注册：`schtasks /Create /TN DysyncAsrWatcher /TR "powershell -WindowStyle Hidden -ExecutionPolicy Bypass -File D:\dysync\asr-bridge\watcher.ps1" /SC ONLOGON /F`（部署脚本里给全，含自启说明）

**同机 vs 分离部署**：本设计（信号文件）适用同机/共享盘场景。局域网分离部署（抖小云在 NAS、ASR 在服务器）时，把触发方式换成本机 HTTP 小服务（`:8002/start`），watcher 主体逻辑不变——作为演进方向记录，不在本期实现。数据回流方向（dysync→ASR 请求、响应体返回文本）两种拓扑相同，无需改动。

### ③ dysync 侧：后端 + 配置 + 前端

**后端 `LocalAsrSubtitleService.cs`**：
- 转写入口（`GenerateSubtitleAsync`）在 health 检查失败后新增「拉起等待」逻辑：
  1. `CheckHealthAsync` 失败 → 写 `/app/asr-bridge/start.flag`（挂载卷，落到宿主 `D:\dysync\asr-bridge\`）
  2. 轮询 health（间隔 5s，最长 **3 分钟**——模型加载 1-2 分钟）
  3. 就绪 → 继续原转写流程；超时 → 返回明确错误「ASR 服务启动超时，请检查宿主机 asr-bridge 日志」
- 批量转写（`GenerateSubtitlesForVideosAsync`）在循环前做一次拉起等待，避免每条都触发

**部署 `docker-compose.yml`**：
```yaml
volumes:
  - D:/dysync/asr-bridge:/app/asr-bridge   # 信号文件桥
```

**前端设置页 `AppSet.vue`**：
- 「Check ASR」结果展示扩展：Online/Offline 之外，显示 `device`（GPU 型号 or CPU 模式）、`vram_used_mb/total_mb`、`model_loaded`、`model_dir_exists`（缺失标红）
- 数据来源：`GET /api/config/asr/health` 透传 ASR 新 health 字段

## GPU/模型检查矩阵（原始需求落点）

| 检查项 | 责任方 | 时机 | 失败表现 |
|--------|--------|------|----------|
| NVIDIA GPU 存在/CUDA 可用 | ASR 启动（torch.cuda） | 启动 | 自动降 CPU，health `device:cpu`，设置页可见 |
| 显存上限 | ASR（软上限 6.5GB） | 转写中 | 明确报错（已有逻辑，仅调常量） |
| 模型文件存在 | watcher + ASR 启动 | 拉起前/启动时 | watcher 日志 + health `model_dir_exists:false` + transcribe 明确报错 |
| conda env 存在 | watcher | 拉起前 | watcher 日志，不拉起，dysync 超时报错可查 |

## 数据流（一次字幕生成，完整）

```
用户点「生成字幕」
→ dysync: CheckHealthAsync 探 :8000 （短超时）
   ├─ 通 → 直接 POST /api/transcribe
   └─ 不通 → 写 /app/asr-bridge/start.flag
       → 宿主 watcher(1s 轮询)见 flag
         → 检查 conda/GPU/模型 → Start-Process uvicorn → 删 flag
       → dysync 轮询 health（≤3min，模型加载）
       → 就绪 → POST /api/transcribe
→ 响应体返回 {text, segments[{text,start,end,start_ms,end_ms}]}
→ dysync 写 .srt + .txt、更新 SubtitleSavePath/StatusMsg
→（30 分钟无请求）ASR os._exit(0) → 显存释放
→ 下次字幕任务 → 回到第一步
```

## 验证标准

1. 杀掉 ASR 进程 → dysync 点「生成字幕」→ 3 分钟内自动就绪并出字幕
2. ASR 空闲 30 分钟后进程自动消失（显存释放）
3. 设置页 Check ASR 显示 GPU 型号/显存/模型状态
4. 临时改名模型目录 → watcher 拒绝拉起 + 日志明确原因；恢复后正常
5. 之前失败的「浙江人做生意」长视频（显存超限那条）能转写成功（6.5GB 上限）
6. 无 GPU 场景（模拟：CUDA 不可见）→ ASR 以 CPU 模式启动，health 如实上报

## 不做的事（YAGNI）
- 不做 ASR→dysync 反向推送/回调（数据回流就是 HTTP 响应体，已满足）
- 不做多任务队列/并发转写（现有 `_INFER_LOCK` 串行已够单机用）
- 不做 HTTP 触发 watcher（本期同机文件桥够用，分离部署时再演进）
- 不动 dysync 数据库结构
- 不做容器内 ASR（Windows Docker 无 GPU 直通，慢 50 倍，否决）
