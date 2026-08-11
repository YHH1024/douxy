"use strict";

// ============ DOM ============
const $ = (id) => document.getElementById(id);
const dropzone = $("dropzone"), fileInput = $("fileInput"), btnRun = $("btnRun");
const fileinfo = $("fileinfo"), fiName = $("fiName"), fiSize = $("fiSize");
const textOut = $("textOut"), timelineOut = $("timelineOut");
const btnTxt = $("btnTxt"), btnSrt = $("btnSrt");

let selectedFile = null;
let busy = false;
let lastText = "", lastSegments = [], lastBaseName = "识别结果";

// ============ 工具函数 ============
function fmtBytes(b) {
  if (b < 1024) return b + " B";
  if (b < 1048576) return (b / 1024).toFixed(1) + " KB";
  return (b / 1048576).toFixed(2) + " MB";
}
function setOut(el, text, placeholder) {
  el.classList.toggle("placeholder", !!placeholder);
  el.textContent = text;
}
function escapeHtml(t) {
  return String(t).replace(/[&<>]/g, (c) => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;" }[c]));
}
// ms -> mm:ss(时间轴列表用)
function fmtClock(ms) {
  const m = Math.floor(ms / 60000), s = Math.floor(ms % 60000 / 1000);
  return String(m).padStart(2, "0") + ":" + String(s).padStart(2, "0");
}
// ms -> HH:MM:SS,mmm(SRT 时间戳)
function fmtSrtTime(ms) {
  const p = (x, l = 2) => String(x).padStart(l, "0");
  return `${p(Math.floor(ms / 3600000))}:${p(Math.floor(ms % 3600000 / 60000))}:` +
         `${p(Math.floor(ms % 60000 / 1000))},${p(Math.floor(ms % 1000), 3)}`;
}
function download(name, content, mime = "text/plain") {
  const blob = new Blob(["﻿" + content], { type: mime + ";charset=utf-8" }); // BOM 防乱码
  const url = URL.createObjectURL(blob);
  const a = document.createElement("a");
  a.href = url; a.download = name;
  document.body.appendChild(a); a.click(); a.remove();
  setTimeout(() => URL.revokeObjectURL(url), 1000);
}
function buildSrt(segs) {
  return segs.map((s, i) =>
    `${i + 1}\n${fmtSrtTime(s.start)} --> ${fmtSrtTime(s.end)}\n${s.text}`).join("\n\n") + "\n";
}

// ============ 标签页 ============
document.querySelectorAll(".tab").forEach((tab) => {
  tab.addEventListener("click", () => {
    document.querySelectorAll(".tab").forEach((t) => t.classList.remove("active"));
    document.querySelectorAll(".tab-pane").forEach((p) => p.classList.remove("active"));
    tab.classList.add("active");
    $("pane-" + tab.dataset.tab).classList.add("active");
  });
});

// ============ 下载按钮 ============
btnTxt.addEventListener("click", () => { if (lastText) download(lastBaseName + ".txt", lastText); });
btnSrt.addEventListener("click", () => {
  if (lastSegments.length) download(lastBaseName + ".srt", buildSrt(lastSegments));
});

// ============ 上传交互 ============
function pickFile(file) {
  if (!file) return;
  selectedFile = file;
  fileinfo.style.display = "block";
  fiName.textContent = "📄 " + file.name;
  fiSize.textContent = fmtBytes(file.size) + " · " + (file.type || "未知类型");
  btnRun.disabled = false;
}
dropzone.addEventListener("click", () => fileInput.click());
fileInput.addEventListener("change", (e) => pickFile(e.target.files[0]));
["dragenter", "dragover"].forEach((ev) =>
  dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.add("drag"); }));
["dragleave", "drop"].forEach((ev) =>
  dropzone.addEventListener(ev, (e) => { e.preventDefault(); dropzone.classList.remove("drag"); }));
dropzone.addEventListener("drop", (e) => { if (e.dataTransfer.files[0]) pickFile(e.dataTransfer.files[0]); });

// ============ 识别 ============
btnRun.addEventListener("click", async () => {
  if (!selectedFile || busy) return;
  busy = true;
  btnRun.disabled = true;
  btnRun.textContent = "识别中…";
  btnTxt.disabled = btnSrt.disabled = true;
  setOut(textOut, "识别中…", true);
  setTokenLoading(true);
  setOut(timelineOut, "", true);
  resetStats();

  const fd = new FormData();
  fd.append("file", selectedFile);
  const t0 = performance.now();
  try {
    const r = await fetch("/api/transcribe", { method: "POST", body: fd });
    if (!r.ok) {
      const err = await r.json().catch(() => ({ detail: r.statusText }));
      throw new Error(err.detail || "请求失败");
    }
    const data = await r.json();
    renderResult(data, (performance.now() - t0) / 1000);
  } catch (e) {
    setOut(textOut, "❌ 错误:" + e.message, true);
    setTokenLoading(false);
    setOut(timelineOut, "", true);
  } finally {
    busy = false;
    btnRun.disabled = false;
    btnRun.textContent = "开始识别";
  }
});

function resetStats() {
  $("stElapsed").innerHTML = "—<small> s</small>";
  $("stDuration").innerHTML = "—<small> s</small>";
  $("stRtf").textContent = "—";
  $("stChars").textContent = "—";
}

function renderResult(data, wallSec) {
  lastText = data.text || "";
  lastSegments = data.segments || [];
  lastBaseName = (data.filename || "识别结果").replace(/\.[^.]+$/, "");

  // 文字
  setOut(textOut, lastText || "(无识别结果)", !lastText);
  // 时间轴
  renderTimeline(lastSegments);
  // Token 吞吐仪表盘
  renderTokenMetrics(data);

  // 统计
  const dur = (data.duration_ms || 0) / 1000;
  $("stElapsed").innerHTML = (data.elapsed ?? wallSec.toFixed(2)) + "<small> s</small>";
  $("stDuration").innerHTML = (dur ? dur.toFixed(1) : "—") + "<small> s</small>";
  $("stRtf").innerHTML = dur ? (data.elapsed / dur).toFixed(3) : "—";
  $("stChars").textContent = data.token_count || 0;

  // 启用下载
  btnTxt.disabled = !lastText;
  btnSrt.disabled = lastSegments.length === 0;
  btnDlSrt.disabled = lastSegments.length === 0;
}

// ============ 时间轴渲染 ============
function renderTimeline(segs) {
  timelineOut.classList.remove("placeholder");
  timelineOut.innerHTML = "";
  if (!segs.length) { setOut(timelineOut, "(无分段)", true); return; }
  segs.forEach((s) => {
    const row = document.createElement("div");
    row.className = "tl-row";
    const dur = ((s.end - s.start) / 1000).toFixed(1) + "s";
    row.innerHTML =
      `<span class="tl-time">${fmtClock(s.start)}</span>` +
      `<span class="tl-dur">${dur}</span>` +
      `<span class="tl-text">${escapeHtml(s.text)}</span>`;
    timelineOut.appendChild(row);
  });
}

// ============ Token 吞吐指标(输出速度 / 总消耗 / 推理时间 / 音频时长) ============
let tpsRAF = 0;

function setTokenLoading(loading) {
  const t = loading ? "…" : "0";
  $("mTps").textContent = t;
  $("mTokens").textContent = t;
  $("mElapsed").textContent = t;
  $("mDur").textContent = t;
}

function renderTokenMetrics(data) {
  const tokens = data.token_count || 0;
  const elapsed = data.elapsed || 0;
  const tps = elapsed > 0 ? tokens / elapsed : 0;
  $("mTokens").textContent = tokens;
  $("mElapsed").textContent = elapsed.toFixed(2);
  $("mDur").textContent = ((data.duration_ms || 0) / 1000).toFixed(1);
  // Token/s 数字滚动动画
  cancelAnimationFrame(tpsRAF);
  const start = performance.now(), durMs = 700;
  const tick = () => {
    const p = Math.min((performance.now() - start) / durMs, 1);
    const eased = 1 - Math.pow(1 - p, 3);
    $("mTps").textContent = (tps * eased).toFixed(0);
    if (p < 1) tpsRAF = requestAnimationFrame(tick);
  };
  tick();
}

// ============ GPU 监控:WebSocket + Canvas 折线图 ============
const canvas = $("gpuChart");
const ctx = canvas.getContext("2d");
const HIST = 60;
const utilHist = [], memHist = [];
let dpr = window.devicePixelRatio || 1;

function resizeCanvas() {
  dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth, h = canvas.clientHeight;
  canvas.width = Math.max(1, Math.floor(w * dpr));
  canvas.height = Math.max(1, Math.floor(h * dpr));
  drawChart();
}
new ResizeObserver(resizeCanvas).observe(canvas.parentElement);

function drawChart() {
  const w = canvas.width, h = canvas.height;
  ctx.clearRect(0, 0, w, h);
  ctx.strokeStyle = "rgba(255,255,255,0.05)";
  ctx.lineWidth = 1 * dpr;
  for (let i = 1; i < 4; i++) {
    const y = (h / 4) * i;
    ctx.beginPath(); ctx.moveTo(0, y); ctx.lineTo(w, y); ctx.stroke();
  }
  drawLine(utilHist, "#4f9cff", w, h);
  drawLine(memHist, "#34d399", w, h);
  ctx.font = `${11 * dpr}px sans-serif`;
  ctx.fillStyle = "#4f9cff"; ctx.fillRect(10 * dpr, 8 * dpr, 10 * dpr, 10 * dpr);
  ctx.fillStyle = "#8b93a7"; ctx.fillText("GPU 利用率", 26 * dpr, 17 * dpr);
  ctx.fillStyle = "#34d399"; ctx.fillRect(120 * dpr, 8 * dpr, 10 * dpr, 10 * dpr);
  ctx.fillStyle = "#8b93a7"; ctx.fillText("显存控制器", 136 * dpr, 17 * dpr);
}
function drawLine(hist, color, w, h) {
  if (hist.length < 2) return;
  ctx.strokeStyle = color;
  ctx.lineWidth = 2 * dpr;
  ctx.beginPath();
  hist.forEach((v, i) => {
    const x = (i / (HIST - 1)) * w;
    const y = h - (v / 100) * h;
    i === 0 ? ctx.moveTo(x, y) : ctx.lineTo(x, y);
  });
  ctx.stroke();
  const grad = ctx.createLinearGradient(0, 0, 0, h);
  grad.addColorStop(0, color + "33");
  grad.addColorStop(1, color + "00");
  ctx.lineTo(w, h); ctx.lineTo(0, h); ctx.closePath();
  ctx.fillStyle = grad; ctx.fill();
}

function updateGPU(s) {
  if (!s || !s.available) {
    $("hStatus").textContent = "GPU 监控不可用";
    $("hStatus").style.background = "rgba(239,68,68,.12)";
    $("hStatus").style.color = "var(--danger)";
    return;
  }
  $("hGpu").textContent = s.name;
  $("gUtil").textContent = s.gpu_util;
  $("gUtilBar").style.width = s.gpu_util + "%";
  const usedMB = s.mem_used / 1048576;
  const capMB = ((s.mem_limit && s.mem_limit > 0) ? s.mem_limit : s.mem_total) / 1048576;
  const memRatio = capMB ? usedMB / capMB : 0;
  $("gMemUsed").textContent = usedMB.toFixed(0) + " MB";
  $("gMemTotal").textContent = (capMB / 1024).toFixed(1) + " GB";
  $("gMemBar").style.width = Math.min(memRatio * 100, 100) + "%";
  document.querySelector(".gcard.mem").classList.toggle("alert", memRatio > 0.9);
  $("gTemp").textContent = s.temp;
  $("gTempBar").style.width = Math.min(s.temp / 90 * 100, 100) + "%";
  $("gPow").textContent = s.power;
  $("gPowLim").textContent = s.power_limit;
  $("gPowBar").style.width = (s.power_limit ? s.power / s.power_limit * 100 : 0) + "%";
  utilHist.push(s.gpu_util); if (utilHist.length > HIST) utilHist.shift();
  memHist.push(s.mem_util); if (memHist.length > HIST) memHist.shift();
  drawChart();
  $("hStatus").textContent = "实时监控中";
}

function connectGPU() {
  const proto = location.protocol === "https:" ? "wss" : "ws";
  const ws = new WebSocket(`${proto}://${location.host}/ws/gpu`);
  ws.onmessage = (e) => updateGPU(JSON.parse(e.data));
  ws.onopen = () => { $("hStatus").textContent = "实时监控中"; };
  ws.onclose = () => { $("hStatus").textContent = "已断开,3s 后重连"; setTimeout(connectGPU, 3000); };
  ws.onerror = () => ws.close();
}

// ============ LLM 对话 / 翻译 (调用 8001 服务) ============
const LLM = location.port === "8010" ? "http://127.0.0.1:8001" : "";
let llmOnline = false;

const chatOut = $("chatOut"), chatInput = $("chatInput");
const btnChatSend = $("btnChatSend"), btnChatClear = $("btnChatClear");
const translateSrc = $("translateSrc"), translateOut = $("translateOut");
const translateLang = $("translateLang"), btnTranslate = $("btnTranslate"), btnFillSrc = $("btnFillSrc"), btnCpOut = $("btnCpOut"), btnDlTxt = $("btnDlTxt"), btnDlSrt = $("btnDlSrt");
let chatHistory = [], chatBusy = false;

// 轮询 LLM 健康状态,更新右上角 pill
async function checkLlm() {
  const pill = $("hLlm");
  try {
    const h = await fetch(LLM + "/api/llm/health", { cache: "no-store" }).then((r) => r.json());
    llmOnline = !!h.model_loaded;
    const dev = h.device === "cuda" ? "GPU" : "CPU";
    pill.innerHTML = `<span class="dot" style="background:${llmOnline ? "var(--accent-2)" : "var(--warn)"}"></span>LLM ${llmOnline ? "在线·" + dev : "离线"}`;
    pill.classList.toggle("off", !llmOnline);
  } catch {
    llmOnline = false;
    pill.innerHTML = `<span class="dot" style="background:var(--text-dim)"></span>LLM 离线`;
    pill.classList.add("off");
  }
}

// SSE 流式读取:逐 token 回调
async function streamChat(messages, { onToken, onDone, onError, temperature = 0.7, top_p = 0.8, max_tokens, signal } = {}) {
  try {
    const r = await fetch(LLM + "/api/llm/chat", {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify({ messages, temperature, top_p, max_tokens }),
      signal,
    });
    if (!r.ok) {
      const err = await r.json().catch(() => ({ detail: r.statusText }));
      throw new Error(err.detail || "LLM 请求失败");
    }
    const reader = r.body.getReader();
    const dec = new TextDecoder();
    let buf = "";
    while (true) {
      const { value, done } = await reader.read();
      if (done) break;
      buf += dec.decode(value, { stream: true });
      let idx;
      while ((idx = buf.indexOf("\n\n")) >= 0) {
        const block = buf.slice(0, idx);
        buf = buf.slice(idx + 2);
        const line = block.split("\n").find((l) => l.startsWith("data:"));
        if (!line) continue;
        const payload = line.slice(5).trim();
        if (payload === "[DONE]") { onDone && onDone(); return; }
        let obj; try { obj = JSON.parse(payload); } catch { continue; }
        if (obj.error) { onError && onError(obj.error); return; }
        if (obj.text) onToken && onToken(obj.text);
      }
    }
    onDone && onDone();
  } catch (e) {
    if (e && e.name === "AbortError") { onDone && onDone(); return; }
    onError && onError(e.message);
  }
}

function chatBubble(role, text) {
  const empty = chatOut.querySelector(".chat-empty");
  if (empty) empty.remove();
  const div = document.createElement("div");
  div.className = "chat-msg " + (role === "user" ? "user" : "assistant");
  div.innerHTML = escapeHtml(text || "");
  chatOut.appendChild(div);
  chatOut.scrollTop = chatOut.scrollHeight;
  return div;
}

async function sendChat() {
  const text = chatInput.value.trim();
  if (!text || chatBusy) return;
  if (!llmOnline) { alert("LLM 服务(8001)未启动,请稍候或检查启动日志。"); return; }
  chatBusy = true; btnChatSend.disabled = true; chatInput.value = "";
  chatBubble("user", text);
  chatHistory.push({ role: "user", content: text });
  const bubble = chatBubble("assistant", "");
  let acc = "";
  const messages = [
    { role: "system", content: "你是 Qwen2.5,一个有帮助的中文 AI 助手。回答简洁、准确、自然。" },
    ...chatHistory,
  ];
  await streamChat(messages, {
    onToken: (t) => { acc += t; bubble.innerHTML = escapeHtml(acc); chatOut.scrollTop = chatOut.scrollHeight; },
    onDone: () => { chatHistory.push({ role: "assistant", content: acc }); },
    onError: (e) => { bubble.innerHTML = '<span class="chat-err">⚠️ ' + escapeHtml(e) + "</span>"; },
  });
  chatBusy = false; btnChatSend.disabled = false; chatInput.focus();
}

btnChatSend.addEventListener("click", sendChat);
chatInput.addEventListener("keydown", (e) => {
  if (e.key === "Enter" && !e.shiftKey) { e.preventDefault(); sendChat(); }
});
btnChatClear.addEventListener("click", () => {
  chatHistory = [];
  chatOut.innerHTML = '<div class="chat-empty">和 Qwen2.5-7B 多轮对话,回答流式输出</div>';
});

let translating = false, translateAbort = null;

// 长文本切块:优先在换行/句号处断,每块 <= maxLen 字符。
// 分块翻译可避免单次长生成时模型退化(译完后继续把中文当输入"中文翻中文")。
function chunkText(text, maxLen = 800) {
  const sentences = text.split(/(?<=[\n。.!?！？])/).filter((s) => s.length);
  const out = [];
  let buf = "";
  for (let s of sentences) {
    while (s.length > maxLen) { out.push(s.slice(0, maxLen)); s = s.slice(maxLen); }
    if (buf && buf.length + s.length > maxLen) { out.push(buf); buf = s; }
    else { buf += s; }
  }
  if (buf) out.push(buf);
  return out.length ? out : [text];
}

async function runTranslate() {
  const src = translateSrc.value.trim();
  if (!src) { alert("请输入要翻译的文字"); return; }
  if (!llmOnline) { alert("LLM 服务(8001)未启动,请稍候或检查启动日志。"); return; }
  const lang = translateLang.value;
  translating = true;
  translateAbort = new AbortController();
  btnTranslate.textContent = "⏹ 停止";
  btnTranslate.classList.add("stop");
  btnDlSrt.disabled = true;
  translateOut.classList.remove("placeholder"); translateOut.textContent = "";
  const sys = `你是专业翻译引擎。把用户给出的文本翻译成${lang}。规则:只输出译文,不要任何解释、不要加引号、不要保留原文;若文本已经是${lang},则原样输出。`;
  const chunks = chunkText(src);
  let acc = "";
  for (let i = 0; i < chunks.length; i++) {
    if (!translating) break;
    await streamChat(
      [{ role: "system", content: sys }, { role: "user", content: chunks[i] }],
      {
        temperature: 0.3,
        max_tokens: 4096,
        signal: translateAbort.signal,
        onToken: (t) => { acc += t; translateOut.textContent = acc; },
        onError: (e) => { translateOut.innerHTML = '<span class="chat-err">⚠️ ' + escapeHtml(e) + "</span>"; },
      }
    );
    acc += "\n";
  }
  translating = false; translateAbort = null;
  btnTranslate.textContent = "开始翻译";
  btnTranslate.classList.remove("stop");
  translateOut.textContent = acc.trim();
  const has = !!acc.trim();
  btnCpOut.disabled = !has;
  btnDlTxt.disabled = !has;
  btnDlSrt.disabled = lastSegments.length === 0;
}
btnTranslate.addEventListener("click", () => {
  if (translating) {
    if (translateAbort) translateAbort.abort();
    translating = false; translateAbort = null;
    btnTranslate.textContent = "开始翻译";
    btnTranslate.classList.remove("stop");
    return;
  }
  runTranslate();
});
btnFillSrc.addEventListener("click", () => {
  if (lastText) translateSrc.value = lastText;
  else alert("还没有识别结果,先上传音频识别一次。");
});
btnCpOut.addEventListener("click", () => {
  const t = translateOut.textContent.trim();
  if (!t) return;
  navigator.clipboard.writeText(t).then(() => {
    btnCpOut.textContent = "已复制";
    setTimeout(() => (btnCpOut.textContent = "复制"), 1200);
  }).catch(() => alert("复制失败,请手动选中复制"));
});
btnDlTxt.addEventListener("click", () => {
  const t = translateOut.textContent.trim();
  if (t) download("译文.txt", t);
});

// 单段翻译(供双语 SRT 用)
async function translateSegment(text, lang, signal) {
  let acc = "";
  const sys = `你是专业翻译引擎。把用户给出的文本翻译成${lang}。只输出译文,不要解释、不要引号。若已是${lang}则原样输出。`;
  await streamChat([{ role: "system", content: sys }, { role: "user", content: text }], {
    temperature: 0.3, max_tokens: 2048, signal,
    onToken: (t) => { acc += t; },
  });
  return acc.trim();
}

// 双语 SRT:按 ASR 句段逐段翻译,时间轴借用原文段
let srtBusy = false, srtAbort = null;
async function buildBilingualSrt() {
  if (!lastSegments.length) { alert("先上传音频识别后再生成双语字幕"); return; }
  if (!llmOnline) { alert("LLM 服务(8001)未启动,请稍候或检查启动日志。"); return; }
  if (translating) { alert("请先完成或停止当前翻译"); return; }
  const lang = translateLang.value;
  srtBusy = true; srtAbort = new AbortController();
  btnTranslate.disabled = true; btnCpOut.disabled = true; btnDlTxt.disabled = true;
  btnDlSrt.classList.add("stop");
  let srt = "", interrupted = false;
  for (let i = 0; i < lastSegments.length; i++) {
    if (!srtBusy) { interrupted = true; break; }
    btnDlSrt.textContent = `⏹ ${i + 1}/${lastSegments.length}`;
    const seg = lastSegments[i];
    let trans = "";
    try { trans = await translateSegment(seg.text, lang, srtAbort.signal); } catch (e) {}
    if (!trans) trans = seg.text;
    srt += `${i + 1}\n${fmtSrtTime(seg.start)} --> ${fmtSrtTime(seg.end)}\n${seg.text}\n${trans}\n\n`;
  }
  srtBusy = false; srtAbort = null;
  btnDlSrt.textContent = "⬇ 双语SRT"; btnDlSrt.classList.remove("stop");
  btnTranslate.disabled = false;
  const hasOut = !!translateOut.textContent.trim();
  btnCpOut.disabled = !hasOut; btnDlTxt.disabled = !hasOut;
  btnDlSrt.disabled = lastSegments.length === 0;
  if (!interrupted && srt.trim()) download("双语字幕.srt", srt);
}
btnDlSrt.addEventListener("click", () => {
  if (srtBusy) { if (srtAbort) srtAbort.abort(); srtBusy = false; return; }
  buildBilingualSrt();
});

// ============ 初始化 ============
(async function init() {
  resizeCanvas();
  connectGPU();
  checkLlm();
  setInterval(checkLlm, 5000);
  try {
    const h = await fetch("/api/health").then((r) => r.json());
    $("hDevice").textContent = (h.device || "?") + (h.model_loaded ? "" : " (模型未加载)");
    if (h.model) {
      $("hModel").textContent = h.model.name;
      $("miName").textContent = h.model.name;
      $("miComp").textContent = (h.model.components || []).join(" · ");
      $("miVocab").textContent = h.model.vocab ? (h.model.vocab + " 字") : "—";
      $("miLang").textContent = h.model.language || "—";
    }
    if (!h.model_loaded) {
      btnRun.disabled = true;
      setOut(textOut, "⚠️ 模型未加载,请检查后端日志", true);
    }
  } catch (e) {
    $("hStatus").textContent = "后端未连接";
  }
})();

