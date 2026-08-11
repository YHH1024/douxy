# -*- coding: utf-8 -*-
"""
ASR 异步任务兼容层(模仿腾讯云 CreateRecTask / DescribeTaskStatus 契约)

为本机 Paraformer ASR 提供异步任务接口,供 live-platform backend 以「提交 → 轮询」
的方式调用(对齐腾讯云录音文件识别的调用模型):
  submit(url) -> task_id      对应腾讯 CreateRecTask(提交后立即返回)
  get_status(task_id)         对应腾讯 DescribeTaskStatus(轮询查状态/结果)

设计要点:
- 任务存内存 dict,单 worker 线程串行消费队列。串行天然解决 funasr
  AutoModel.generate 单实例并发不安全的问题(同一时刻只有一个推理在跑)。
- 实际的「下载音频 → 视频抽音频 → 推理 → 时间戳格式转换」由 app.py 注入的
  _processor 回调完成;本模块只负责调度,不 import app.py,避免循环依赖。
- 任务完成后保留 2 小时供 backend 轮询,超时 GC 清理,防止内存无限增长。

status 数值对齐腾讯云 DescribeTaskStatus:
  0 = waiting(等待) / 1 = doing(执行中) / 2 = success(成功) / 3 = failed(失败)
"""
import queue
import threading
import time
from typing import Callable, Optional

# ---------------- 内部状态 ----------------
_jobs: "dict[int, dict]" = {}        # task_id -> 任务字典
_jobs_lock = threading.Lock()        # 保护 _jobs 的增删改查
_task_queue: "queue.Queue[int]" = queue.Queue()   # 待处理 task_id 队列
_processor: Optional[Callable[[str], "tuple[str, list[dict]]"]] = None  # 由 app.py 注入

_seq = 0
_seq_lock = threading.Lock()

# 任务保留时长(秒):完成后供 backend 轮询查询,超时清理
_TTL_SECONDS = 2 * 60 * 60


def _next_id() -> int:
    """自增 task_id(简单递增,对齐腾讯 TaskId 的 int 形态)。"""
    global _seq
    with _seq_lock:
        _seq += 1
        return _seq


def set_processor(fn: Callable[[str], "tuple[str, list[dict]]"]) -> None:
    """app.py 启动时注入处理器:fn(url) -> (text, detail_list)。

    detail_list 元素形如 {"FinalSentence": str, "StartMs": int, "EndMs": int}。
    fn 抛任何异常都会被 worker 捕获并把任务标记为失败(status=3)。
    """
    global _processor
    _processor = fn


def submit(url: str) -> int:
    """提交转写任务,立即返回 task_id(不阻塞,不下载)。"""
    task_id = _next_id()
    with _jobs_lock:
        _jobs[task_id] = {
            "status": 0,            # waiting
            "result": "",           # 全文(status=2 时有值)
            "result_detail": [],    # 逐句 [{FinalSentence, StartMs, EndMs}]
            "error_msg": "",        # 失败原因(status=3 时有值)
            "created_at": time.time(),
            "url": url,
        }
    _task_queue.put(task_id)
    return task_id


def get_status(task_id: int) -> Optional[dict]:
    """查询任务状态。任务不存在(或已被 GC 清理)返回 None。"""
    with _jobs_lock:
        j = _jobs.get(int(task_id))
        if not j:
            return None
        return {
            "status": j["status"],
            "result": j["result"],
            "result_detail": j["result_detail"],
            "error_msg": j["error_msg"],
        }


def start_worker() -> None:
    """启动后台 worker 线程(daemon,随主进程退出)。幂等:多次调用只起一个。"""
    t = threading.Thread(target=_worker_loop, name="asr-jobs-worker", daemon=True)
    t.start()


def _worker_loop() -> None:
    """串行消费队列:取 task_id → 调 processor → 写结果。任何异常标失败。"""
    while True:
        task_id = _task_queue.get()
        try:
            _process(task_id)
        except Exception as e:  # 兜底(_process 内部已处理,此处再保险)
            _mark_failed(task_id, f"worker error: {e}")
        finally:
            _task_queue.task_done()
            _gc()


def _process(task_id: int) -> None:
    with _jobs_lock:
        j = _jobs.get(task_id)
        if not j:
            return
        j["status"] = 1  # doing
    if _processor is None:
        _mark_failed(task_id, "processor not registered")
        return
    try:
        # 下载 → 推理 → 格式转换,由 app.py 注入的 processor 完成。
        # processor 内部用 _INFER_LOCK 保证与 /api/transcribe 串行推理。
        text, detail = _processor(j["url"])
        with _jobs_lock:
            j = _jobs.get(task_id)
            if j:
                j["status"] = 2
                j["result"] = text or ""
                j["result_detail"] = detail or []
    except Exception as e:
        _mark_failed(task_id, str(e))


def _mark_failed(task_id: int, err: str) -> None:
    with _jobs_lock:
        j = _jobs.get(task_id)
        if j:
            j["status"] = 3
            j["error_msg"] = (err or "")[:1000]


def _gc() -> None:
    """清理创建超过 _TTL_SECONDS 的任务(无论状态)。worker 每轮顺带调用。"""
    cutoff = time.time() - _TTL_SECONDS
    with _jobs_lock:
        stale = [tid for tid, j in _jobs.items() if j.get("created_at", 0) < cutoff]
        for tid in stale:
            _jobs.pop(tid, None)
