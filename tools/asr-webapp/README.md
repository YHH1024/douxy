# Windows ASR 运行文件说明

这个目录用于随仓库一起分发 Windows 本地 ASR 服务的运行脚本和前端页面。

包含内容：

- `app.py`
- `asr_jobs.py`
- `start_web.bat`
- `static/`

不包含内容：

- `Paraformer_model`
- Python 运行环境
- `ffmpeg`

部署时请额外准备：

1. 模型目录：`D:\ASR\Paraformer_model`
2. 运行目录：`D:\ASR\webapp`
3. 可用的 Python 环境
4. 系统可访问的 `ffmpeg`

推荐做法：

1. 将本目录文件复制到 `D:\ASR\webapp`
2. 在 `D:\ASR\webapp\python.local.txt` 中写入真实 `python.exe` 路径
3. 运行 `start_web.bat`
4. 打开 `http://127.0.0.1:8010/api/health`

更完整的部署步骤请查看：

- `D:\2026年工作\AI相关\Douxiaoyun\dysync.net\docs\windows-asr-deploy.md`
