# -*- coding: utf-8 -*-
"""
Paraformer ASR Web 服务 (FastAPI)
- POST /api/transcribe  : 上传音频/视频,返回识别文字 + 每字时间戳 + 耗时
- WS    /ws/gpu         : 每秒推送 GPU 利用率/显存/温度/功耗
- GET   /api/gpu        : 一次性 GPU 状态
- 静态页面挂载在根路径 /

启动: uvicorn app:app --host 0.0.0.0 --port 8010
"""
import os
# 减少显存碎片(PyTorch 官方推荐),降低长音频分段推理时的碎片化 OOM
os.environ.setdefault("PYTORCH_CUDA_ALLOC_CONF", "expandable_segments:True")
import time
import tempfile
import subprocess
import asyncio
import threading
import shutil
import urllib.request
from pathlib import Path
from urllib.parse import urlparse

import torch
import pynvml
import sys

# Windows 控制台默认 GBK 编码,打印 emoji/特殊字符会触发 UnicodeEncodeError 崩溃,统一改 UTF-8
try:
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    sys.stderr.reconfigure(encoding="utf-8", errors="replace")
except Exception:
    pass
from fastapi import FastAPI, UploadFile, File, WebSocket, WebSocketDisconnect, HTTPException
from fastapi.staticfiles import StaticFiles
from fastapi.responses import JSONResponse
from pydantic import BaseModel

import asr_jobs   # 异步任务兼容层(对齐腾讯云 ASR 提交/轮询契约)

# ==================== 配置 ====================
MODEL_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "Paraformer_model")
VIDEO_EXTS = {".mp4", ".mov", ".avi", ".mkv", ".flv", ".wmv", ".webm", ".ts", ".m4v"}
STATIC_DIR = os.path.join(os.path.dirname(os.path.abspath(__file__)), "static")
# 显存保护:PyTorch 单进程允许使用的最大显存(GB)。设为 None 表示不限制。
# 调低到 3.5:ASR 实际只用 ~2-2.5GB,剩余显存让给并行的 LLM 进程 (8001, Qwen2.5-7B)。
GPU_MEM_LIMIT_GB = 3.5
# 模型信息(前端展示用)
MODEL_INFO = {
    "name": "Paraformer-large",
    "full_id": "speech_paraformer-large-vad-punc_asr_nat-zh-cn-16k-common-vocab8404-pytorch",
    "components": ["Paraformer ASR", "fsmn-vad 分段", "ct-punc 标点"],
    "vocab": 8404,
    "language": "中文 (zh-cn)",
    "sample_rate": 16000,
}

app = FastAPI(title="Paraformer ASR Web")

# 全局推理锁:保护 MODEL.generate 单实例不并发。/api/transcribe 与异步任务 worker 共用。
_INFER_LOCK = threading.Lock()

# ==================== 设备 & 显存保护 ====================
print("=" * 50)
DEVICE = "cuda" if torch.cuda.is_available() else "cpu"
GPU_MEM_TOTAL_BYTES = 0      # 物理总显存
GPU_MEM_LIMIT_BYTES = 0      # 设定的软上限(同下,0 表示无限制)
if DEVICE == "cuda":
    GPU_MEM_TOTAL_BYTES = torch.cuda.get_device_properties(0).total_memory
    if GPU_MEM_LIMIT_GB:
        GPU_MEM_LIMIT_BYTES = int(GPU_MEM_LIMIT_GB * 1024 ** 3)
        frac = min(GPU_MEM_LIMIT_BYTES / GPU_MEM_TOTAL_BYTES, 1.0)
        # 必须在模型加载(首次显存分配)之前设置,才生效
        torch.cuda.set_per_process_memory_fraction(frac, device=0)
        print(f"[OK] 显存保护已启用:上限 {GPU_MEM_LIMIT_GB}GB / "
              f"{GPU_MEM_TOTAL_BYTES/1024**3:.1f}GB (fraction={frac:.3f})")
    else:
        GPU_MEM_LIMIT_BYTES = GPU_MEM_TOTAL_BYTES
        print(f"显存保护未启用,可用 {GPU_MEM_TOTAL_BYTES/1024**3:.1f}GB")

# ==================== 模型加载(启动时一次性) ====================
print("加载 Paraformer 模型 ...")
MODEL = None
if os.path.isdir(MODEL_DIR):
    from funasr import AutoModel as _AM
    # 加入 VAD(语音分段)+ PUNC(标点恢复):长音频被切成短段分别识别,
    # 避免一次性把整段音频喂进模型导致显存爆炸(OOM)。首次启动会从 ModelScope 下载这两个模型。
    try:
        MODEL = _AM(
            model=MODEL_DIR,
            vad_model="fsmn-vad", vad_model_revision="v2.0.4",
            punc_model="ct-punc-c", punc_model_revision="v2.0.4",
            disable_update=True,
        )
        print("模型加载完成(ASR + VAD + PUNC),设备:", DEVICE)
    except Exception as e:
        print(f"[警告] 加载 VAD/PUNC 失败,退回纯 ASR 模式(短音频仍可用): {e}")
        MODEL = _AM(model=MODEL_DIR, disable_update=True)
        print("模型加载完成(纯 ASR),设备:", DEVICE)
else:
    print(f"[警告] 模型目录不存在: {MODEL_DIR}")
print("=" * 50)

# ==================== GPU 监控(pynvml) ====================
GPU_HANDLE = None
GPU_NAME = ""
HAS_NVML = False
try:
    pynvml.nvmlInit()
    GPU_HANDLE = pynvml.nvmlDeviceGetHandleByIndex(0)
    _name = pynvml.nvmlDeviceGetName(GPU_HANDLE)
    GPU_NAME = _name.decode() if isinstance(_name, bytes) else str(_name)
    HAS_NVML = True
    print(f"GPU 监控就绪: {GPU_NAME}")
except Exception as e:
    print(f"[警告] NVML 初始化失败,GPU 监控不可用: {e}")


def gpu_stats() -> dict:
    """读取一次 GPU 状态。"""
    if not HAS_NVML:
        return {"available": False}
    util = pynvml.nvmlDeviceGetUtilizationRates(GPU_HANDLE)
    mem = pynvml.nvmlDeviceGetMemoryInfo(GPU_HANDLE)
    temp = pynvml.nvmlDeviceGetTemperature(GPU_HANDLE, pynvml.NVML_TEMPERATURE_GPU)
    try:
        power = pynvml.nvmlDeviceGetPowerUsage(GPU_HANDLE) / 1000.0       # mW -> W
        power_limit = pynvml.nvmlDeviceGetPowerManagementLimit(GPU_HANDLE) / 1000.0
    except Exception:
        power = power_limit = 0.0
    return {
        "available": True,
        "name": GPU_NAME,
        "gpu_util": int(util.gpu),          # GPU 计算利用率 %
        "mem_util": int(util.memory),       # 显存控制器利用率 %
        "mem_used": int(mem.used),          # 已用显存 bytes
        "mem_total": int(mem.total),        # 物理总显存 bytes
        "mem_limit": int(GPU_MEM_LIMIT_BYTES),  # 显存软上限 bytes(0=不限)
        "temp": int(temp),                  # 温度 ℃
        "power": round(power, 1),           # 功耗 W
        "power_limit": round(power_limit, 1),
    }


# ==================== 音频/视频处理 ====================
def extract_audio(video_path: str) -> str:
    """用 ffmpeg 从视频提取 16kHz 单声道 WAV。"""
    tmp_wav = tempfile.NamedTemporaryFile(suffix=".wav", delete=False).name
    cmd = ["ffmpeg", "-y", "-i", video_path, "-vn", "-acodec", "pcm_s16le",
           "-ar", "16000", "-ac", "1", tmp_wav]
    r = subprocess.run(cmd, capture_output=True)
    if r.returncode != 0:
        raise RuntimeError(r.stderr.decode("utf-8", errors="ignore")[-1500:])
    return tmp_wav


# 标点字符(不消费时间戳,用于时间轴对齐)
_PUNCT = set("，。？！,.;:、！？…；;:""''\"' \t")
_SENT_END = set("。！？.!?")


def _build_timeline(res):
    """把文本与逐字时间戳对齐成 timeline(含标点),供逐字上屏 / 时间轴 / SRT 使用。
    标点字符继承上一个字的结束时间,保证 timeline 与文本逐字对齐。"""
    timeline = []
    for item in (res or []):
        text = item.get("text", "").replace(" ", "")
        ts = item.get("timestamp", []) or []
        ts_idx, last_e = 0, (ts[0][0] if ts else 0)
        for ch in text:
            if ch in _PUNCT:
                timeline.append({"c": ch, "s": last_e, "e": last_e})
            else:
                if ts_idx < len(ts):
                    s, e = ts[ts_idx]; ts_idx += 1; last_e = e
                else:
                    s = e = last_e
                timeline.append({"c": ch, "s": s, "e": e})
    return timeline


def _build_segments(timeline, max_chars=20, max_ms=7000):
    """按标点 / 长度 / 时长把 timeline 切成字幕段。"""
    segs, cur, cur_s, cur_e = [], [], None, 0
    for t in timeline:
        if cur_s is None:
            cur_s = t["s"]
        cur_e = t["e"]
        cur.append(t["c"])
        ends = t["c"] in _SENT_END
        comma = t["c"] in "，,"
        over = len(cur) >= max_chars or (cur_e - cur_s) >= max_ms
        if ends or over or (comma and len(cur) >= 8):
            txt = "".join(cur).strip()
            if txt:
                segs.append({"text": txt, "start": cur_s, "end": cur_e})
            cur, cur_s = [], None
    if cur:
        txt = "".join(cur).strip()
        if txt:
            segs.append({"text": txt, "start": cur_s or 0, "end": cur_e})
    return segs


def _run_asr(audio_path: str):
    """同步执行 ASR(在线程池中调用,避免阻塞事件循环)。"""
    t0 = time.time()
    try:
        with _INFER_LOCK:
            res = MODEL.generate(input=audio_path, batch_size_s=300)
    except RuntimeError as e:
        if "out of memory" in str(e).lower():
            raise RuntimeError(
                f"显存超限(上限 {GPU_MEM_LIMIT_GB or '∞'}GB)。"
                "可能原因:音频过长且 VAD 未生效。请缩短音频后重试。")
        raise
    t1 = time.time()
    timeline = _build_timeline(res)
    segments = _build_segments(timeline)
    text = "".join(t["c"] for t in timeline)
    token_count = sum(1 for t in timeline if t["c"] not in _PUNCT)  # 实际输出 token 数(不含标点)
    last_e = timeline[-1]["e"] if timeline else 0
    return {
        "text": text,
        "segments": segments,        # 分句段落,用于时间轴 / SRT
        "token_count": token_count,  # 输出 Token 数(消耗)
        "duration_ms": last_e,
        "elapsed": round(t1 - t0, 3),
        "device": DEVICE,
    }


def _process_asr_job(url: str):
    """异步任务处理器(由 asr_jobs worker 调用):下载 URL → 推理 → 转腾讯云格式。

    Returns:
        (text, detail_list):detail_list 元素 {"FinalSentence", "StartMs", "EndMs"}
    Raises:
        任何异常由 asr_jobs 捕获并把任务标记为 status=3(失败)。
    """
    # 从 URL 推断后缀(COS 预签名 URL 带 query,path 部分才是对象路径)
    suffix = Path(urlparse(url).path).suffix.lower() or ".wav"
    tmp_in = tempfile.NamedTemporaryFile(suffix=suffix, delete=False).name
    try:
        req = urllib.request.Request(url, headers={"User-Agent": "ParaformerASR/1.0"})
        with urllib.request.urlopen(req, timeout=120) as resp, open(tmp_in, "wb") as f:
            shutil.copyfileobj(resp, f)

        # 视频先抽音频(复用 extract_audio);音频直接喂模型
        audio_path = tmp_in
        extracted = False
        if suffix in VIDEO_EXTS:
            audio_path = extract_audio(tmp_in)
            extracted = True
        try:
            # _run_asr 内部用 _INFER_LOCK,保证与 /api/transcribe 串行推理
            res = _run_asr(audio_path)
        finally:
            if extracted:
                try:
                    os.remove(audio_path)
                except OSError:
                    pass

        # Paraformer segments 的 start/end 已是毫秒(_build_segments 用 max_ms 比较,
        # _run_asr 的 duration_ms 也是同一来源),直接用作腾讯 SentenceDetail 的 StartMs/EndMs。
        detail = [
            {
                "FinalSentence": s["text"],
                "StartMs": int(s["start"]),
                "EndMs": int(s["end"]),
            }
            for s in res["segments"]
            if s.get("text", "").strip()
        ]
        return res["text"], detail
    finally:
        try:
            os.remove(tmp_in)
        except OSError:
            pass


# 启动 ASR 异步任务 worker(模型已加载,注入处理器后即可服务)
try:
    asr_jobs.set_processor(_process_asr_job)
    asr_jobs.start_worker()
    print("[OK] ASR 异步任务 worker 已启动 (POST /api/asr/submit, GET /api/asr/status)")
except Exception as e:
    print(f"[警告] ASR worker 启动失败: {e}")


# ==================== 路由 ====================
@app.get("/api/health")
async def health():
    return {"status": "ok", "model_loaded": MODEL is not None, "device": DEVICE,
            "gpu_name": GPU_NAME, "gpu_monitor": HAS_NVML,
            "mem_limit_gb": GPU_MEM_LIMIT_GB, "model": MODEL_INFO}


@app.get("/api/gpu")
async def get_gpu():
    return gpu_stats()


@app.post("/api/transcribe")
async def transcribe(file: UploadFile = File(...)):
    if MODEL is None:
        raise HTTPException(status_code=500, detail="模型未加载")
    suffix = Path(file.filename or "").suffix.lower()
    if suffix in VIDEO_EXTS:
        # 视频文件
        tmp_in = tempfile.NamedTemporaryFile(suffix=suffix, delete=False).name
    else:
        tmp_in = tempfile.NamedTemporaryFile(suffix=suffix or ".wav", delete=False).name
    content = await file.read()
    with open(tmp_in, "wb") as f:
        f.write(content)

    extracted = False
    audio_path = tmp_in
    try:
        if suffix in VIDEO_EXTS:
            audio_path = extract_audio(tmp_in)
            extracted = True
        # 放到线程池跑,保证 WebSocket GPU 监控在推理期间持续刷新
        result = await asyncio.to_thread(_run_asr, audio_path)
        result["filename"] = file.filename
        result["size"] = len(content)
        return JSONResponse(result)
    except Exception as e:
        raise HTTPException(status_code=500, detail=str(e))
    finally:
        for p in ([audio_path, tmp_in] if extracted else [tmp_in]):
            try:
                os.remove(p)
            except OSError:
                pass


class AsrSubmitIn(BaseModel):
    url: str
    channel_num: int = 1   # 对齐腾讯 CreateRecTask;Paraformer 恒按单声道处理


@app.post("/api/asr/submit")
async def asr_submit(body: AsrSubmitIn):
    """提交异步转写任务(对齐腾讯 CreateRecTask)。立即返回 task_id。"""
    task_id = asr_jobs.submit(body.url)
    return {"task_id": task_id}


@app.get("/api/asr/status")
async def asr_status(task_id: int):
    """查询异步转写任务状态(对齐腾讯 DescribeTaskStatus)。

    返回 {status(0/1/2/3), result(全文), result_detail(逐句), error_msg}。
    """
    st = asr_jobs.get_status(task_id)
    if st is None:
        raise HTTPException(status_code=404, detail="task not found")
    return st


@app.websocket("/ws/gpu")
async def ws_gpu(ws: WebSocket):
    await ws.accept()
    try:
        while True:
            await ws.send_json(gpu_stats())
            await asyncio.sleep(1.0)
    except WebSocketDisconnect:
        pass
    except Exception:
        pass


# 静态前端(放最后,作为兜底)
if os.path.isdir(STATIC_DIR):
    app.mount("/", StaticFiles(directory=STATIC_DIR, html=True), name="static")


if __name__ == "__main__":
    import uvicorn
    uvicorn.run("app:app", host="0.0.0.0", port=8010, reload=False)

