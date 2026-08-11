# Windows 本地 ASR 部署说明

## 适用范围

本文档适用于当前这套本地部署方案：

- `dysync.net`
- 本地 ASR 服务：`D:\ASR\webapp`

当前约定的 ASR 服务地址是：

- `http://127.0.0.1:8010`

## 本次改动说明

### `dysync.net` 侧

已经完成以下能力：

- 通过 HTTP 调用本地 ASR 服务
- 增加 ASR 健康检查
- 支持单个视频生成字幕
- 支持批量生成字幕
- 视频列表展示字幕状态
- 支持查看字幕内容预览
- 默认 ASR 服务地址改为 `http://127.0.0.1:8010`

### ASR 服务侧

已经完成以下调整：

- 服务端口从 `8000` 改为 `8010`
- `start_web.bat` 已增强，支持以下启动方式：
  - 使用环境变量 `ASR_PYTHON`
  - 使用 `python.local.txt`
  - 自动回退到系统 `PATH` 里的 `python`
  - 启动前检查关键 Python 包是否存在

## 本次涉及的主要文件

### `dysync.net` 修改文件

- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\Controllers\ConfigController.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\Controllers\VideoController.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\Program.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\extension\ServiceExtension.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\model\entity\AppConfig.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\model\entity\DouyinVideo.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\service\DouyinCommonService.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\service\LocalAsrSubtitleService.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouyinBasicSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouYinCollectSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouYinFavoritSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouyinFollowedSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouyinCollectCustomSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouyinMixSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\job\DouyinSeriesSyncJob.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\utils\FFmpegHelper.cs`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\app\src\store\coreapi.ts`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\app\src\pages\set\AppSet.vue`
- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\app\src\pages\workplace\RecordTable.vue`

### ASR 服务修改文件

- `D:\ASR\webapp\app.py`
- `D:\ASR\webapp\static\app.js`
- `D:\ASR\webapp\start_web.bat`

## ASR 服务部署要求

### 必须存在的目录

- `D:\ASR\webapp`
- `D:\ASR\Paraformer_model`

### 必须具备的外部依赖

- `ffmpeg` 必须能在系统 `PATH` 中找到

### Python 环境必须具备的包

运行 `start_web.bat` 的 Python 环境里，至少需要安装：

- `fastapi`
- `uvicorn`
- `torch`
- `pynvml`
- `funasr`

## ASR 服务启动方式

### 方式一：通过环境变量 `ASR_PYTHON`

先指定正确的 `python.exe` 路径。

示例：

```powershell
$env:ASR_PYTHON = 'D:\your-env\python.exe'
```

然后执行：

```powershell
cd /d D:\ASR\webapp
.\start_web.bat
```

### 方式二：通过 `python.local.txt`

在下面这个位置创建文件：

- `D:\ASR\webapp\python.local.txt`

文件内容只写一行：

```text
D:\your-env\python.exe
```

然后执行：

```powershell
cd /d D:\ASR\webapp
.\start_web.bat
```

## 健康检查

ASR 启动后，打开：

- `http://127.0.0.1:8010/api/health`

正常情况下，应返回类似：

```json
{
  "status": "ok",
  "model_loaded": true,
  "device": "cuda"
}
```

## `dysync.net` 配置方式

打开 `dysync.net` 设置页，确认：

- `ASR Service URL = http://127.0.0.1:8010`

然后点击：

- `Check ASR`

正常结果：

- 状态显示为 `Online`

## 字幕功能联调步骤

1. 启动本地 ASR 服务
2. 确认 `http://127.0.0.1:8010/api/health` 可访问
3. 启动 `dysync.net`
4. 打开设置页，确认 `ASR Service URL`
5. 点击 `Check ASR`
6. 打开视频列表
7. 点击 `生成字幕`
8. 确认字幕状态变成 `Ready`
9. 点击 `View subtitle` 查看字幕内容

## 常见问题排查

### 1. `ERR_CONNECTION_REFUSED`

原因通常是：

- ASR 服务没有启动成功

优先检查：

- `start_web.bat` 控制台输出
- 选定的 Python 环境是否安装了所需包

### 2. `{"detail":"Not Found"}`

原因通常是：

- 当前端口被别的 FastAPI 或 Python 服务占用了

优先检查：

- `8010` 端口上是否真的是当前这套 ASR 服务

### 3. `Check ASR` 失败

优先检查：

- `ASR Service URL` 是否正确
- Windows 防火墙是否拦截
- ASR 启动日志是否报错

### 4. 生成字幕失败

优先检查：

- 视频文件本地是否存在
- ASR 控制台日志是否报错
- `ffmpeg` 是否可用
- GPU 是否可用
- 模型是否加载成功

## 当前已知限制

当前交付版本还不是“独立打包版 ASR 可执行程序”。

现在的运行方式仍然是：

- `dysync.net` 调用本地 Python 版 ASR 服务

后续如果要做：

- 独立 `exe`
- 自动检测环境后自动下载依赖
- 安装包

这些属于下一阶段任务，不在当前交付范围内。
