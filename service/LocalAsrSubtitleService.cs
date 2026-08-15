using dy.net.model.entity;
using dy.net.repository;
using Newtonsoft.Json.Linq;
using Serilog;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;

namespace dy.net.service
{
    /// <summary>
    /// Subtitle generator that calls a local FastAPI ASR service.
    /// </summary>
    public class LocalAsrSubtitleService
    {
        public const string ASR_HTTP_CLIENT = "local-asr-client";

        private readonly DouyinVideoRepository _videoRepository;
        private readonly IHttpClientFactory _httpClientFactory;

        public LocalAsrSubtitleService(DouyinVideoRepository videoRepository, IHttpClientFactory httpClientFactory)
        {
            _videoRepository = videoRepository;
            _httpClientFactory = httpClientFactory;
        }

        public async Task<(bool Success, string Message, string SubtitlePath)> GenerateSubtitleByVideoIdAsync(
            string videoId,
            AppConfig config,
            bool? overwriteExisting = null,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(videoId))
            {
                return (false, "Video id is required.", string.Empty);
            }

            var video = await _videoRepository.GetByIdAsync(videoId);
            if (video == null)
            {
                return (false, "Video not found.", string.Empty);
            }

            return await GenerateSubtitleAsync(video, config, overwriteExisting, cancellationToken);
        }

        public async Task<(int SuccessCount, int FailedCount)> GenerateSubtitlesByIdsAsync(
            IEnumerable<string> videoIds,
            AppConfig config,
            bool? overwriteExisting = null,
            CancellationToken cancellationToken = default)
        {
            var ids = videoIds?.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList() ?? new List<string>();
            if (!ids.Any())
            {
                return (0, 0);
            }

            var videos = await _videoRepository.Query(x => ids.Contains(x.Id)).ToListAsync();
            return await GenerateSubtitlesForVideosAsync(videos, config, overwriteExisting, cancellationToken);
        }

        public async Task<(int SuccessCount, int FailedCount)> GenerateSubtitlesForVideosAsync(
            IEnumerable<DouyinVideo> videos,
            AppConfig config,
            bool? overwriteExisting = null,
            CancellationToken cancellationToken = default)
        {
            if (videos == null)
            {
                return (0, 0);
            }

            int successCount = 0;
            int failedCount = 0;

            foreach (var video in videos)
            {
                var result = await GenerateSubtitleAsync(video, config, overwriteExisting, cancellationToken);
                if (result.Success)
                {
                    successCount++;
                }
                else
                {
                    failedCount++;
                }
            }

            return (successCount, failedCount);
        }

        public async Task<(bool Success, string Message, string ServiceUrl)> CheckHealthAsync(
            AppConfig config,
            CancellationToken cancellationToken = default)
        {
            var validation = ValidateConfig(config);
            if (!validation.Success)
            {
                return (false, validation.Message, validation.ServiceUrl);
            }

            try
            {
                var client = _httpClientFactory.CreateClient(ASR_HTTP_CLIENT);
                using var request = new HttpRequestMessage(HttpMethod.Get, BuildEndpoint(validation.ServiceUrl, "/api/health"));
                using var response = await client.SendAsync(request, cancellationToken);
                var content = await response.Content.ReadAsStringAsync();

                if (!response.IsSuccessStatusCode)
                {
                    return (false, $"ASR service returned {(int)response.StatusCode}: {TrimMessage(content)}", validation.ServiceUrl);
                }

                var healthMessage = ExtractHealthMessage(content);
                var detail = BuildHealthDetail(content);
                return (true, string.IsNullOrWhiteSpace(healthMessage) ? detail : $"{healthMessage} · {detail}", validation.ServiceUrl);
            }
            catch (Exception ex)
            {
                return (false, $"ASR service is unreachable: {ex.Message}", validation.ServiceUrl);
            }
        }

        /// <summary>把 ASR /api/health 原始响应拼成人类可读详情(设置页展示用)。</summary>
        private static string BuildHealthDetail(string responseText)
        {
            try
            {
                var root = JObject.Parse(responseText);
                var device = ReadString(root, "device");
                var gpuName = ReadString(root, "gpu_name");
                var modelLoaded = root["model_loaded"]?.ToString();
                var modelDir = root["model_dir_exists"]?.ToString();
                var vramUsed = root["vram_used_mb"]?.ToString();
                var vramTotal = root["vram_total_mb"]?.ToString();
                var idleMin = root["idle_exit_minutes"]?.ToString();

                var parts = new List<string>();
                if (!string.IsNullOrEmpty(device)) parts.Add($"device:{device}");
                if (!string.IsNullOrEmpty(gpuName)) parts.Add(gpuName);
                if (!string.IsNullOrEmpty(vramUsed) && vramUsed != "0") parts.Add($"VRAM {vramUsed}/{vramTotal}MB");
                parts.Add(modelLoaded == "True" ? "model:loaded" : "model:not-loaded");
                parts.Add(modelDir == "True" ? "model-dir:OK" : "model-dir:MISSING");
                if (!string.IsNullOrEmpty(idleMin) && idleMin != "0") parts.Add($"idle-exit:{idleMin}min");
                return string.Join(" · ", parts);
            }
            catch
            {
                return "ASR service is online.";
            }
        }

        /// <summary>ASR 按需拉起的信号文件路径(容器内,经 compose 挂载对应宿主 D:\dysync\asr-bridge)。</summary>
        private const string AsrBridgeFlagPath = "/app/asr-bridge/start.flag";

        /// <summary>
        /// 确保 ASR 服务在线:不在线则写信号文件触发宿主 watcher 拉起,并轮询等待就绪(最长180s)。
        /// </summary>
        private async Task<(bool Success, string Message, string ServiceUrl)> EnsureAsrRunningAsync(
            AppConfig config,
            CancellationToken cancellationToken = default)
        {
            var health = await CheckHealthAsync(config, cancellationToken);
            if (health.Success)
            {
                return (true, health.Message, health.ServiceUrl);
            }

            // 写信号文件通知宿主 watcher 拉起 ASR
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
                return (false, $"ASR offline and bridge flag write failed: {ex.Message}", health.ServiceUrl);
            }

            // 轮询等待就绪:5s 间隔,最长 180s(模型加载 1-2 分钟)
            for (var attempt = 0; attempt < 36; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                health = await CheckHealthAsync(config, cancellationToken);
                if (health.Success)
                {
                    return (true, "ASR service started on demand.", health.ServiceUrl);
                }
            }

            return (false, "ASR service did not become ready in 180s. Check D:\\dysync\\asr-bridge\\watcher.log on the host.", health.ServiceUrl);
        }

        public async Task<(bool Success, string Message, string SubtitlePath)> GenerateSubtitleAsync(
            DouyinVideo video,
            AppConfig config,
            bool? overwriteExisting = null,
            CancellationToken cancellationToken = default)
        {
            if (video == null)
            {
                return (false, "Video is required.", string.Empty);
            }

            if (config == null)
            {
                await UpdateVideoSubtitleStateAsync(video, string.Empty, "Missing ASR config.");
                return (false, "Missing ASR config.", string.Empty);
            }

            if (string.IsNullOrWhiteSpace(video.VideoSavePath) || !File.Exists(video.VideoSavePath))
            {
                await UpdateVideoSubtitleStateAsync(video, string.Empty, "Video file not found.");
                return (false, "Video file not found.", string.Empty);
            }

            string subtitlePath = Path.ChangeExtension(video.VideoSavePath, ".srt");
            string textPath = Path.ChangeExtension(video.VideoSavePath, ".txt");
            bool overwrite = overwriteExisting ?? config.AsrOverwriteExisting;

            if (!overwrite && File.Exists(subtitlePath))
            {
                await UpdateVideoSubtitleStateAsync(video, subtitlePath, "Subtitle already exists.");
                return (true, "Subtitle already exists.", subtitlePath);
            }

            var healthResult = await EnsureAsrRunningAsync(config, cancellationToken);
            if (!healthResult.Success)
            {
                await UpdateVideoSubtitleStateAsync(video, string.Empty, healthResult.Message);
                return (false, healthResult.Message, string.Empty);
            }

            try
            {
                var transcribeResult = await TranscribeFileAsync(healthResult.ServiceUrl, video.VideoSavePath, config, cancellationToken, video.VideoTitle);
                if (!transcribeResult.Success)
                {
                    await UpdateVideoSubtitleStateAsync(video, string.Empty, transcribeResult.Message);
                    return (false, transcribeResult.Message, string.Empty);
                }

                var subtitleContent = BuildSrtContent(transcribeResult.Payload);
                if (string.IsNullOrWhiteSpace(subtitleContent))
                {
                    const string emptySubtitleMessage = "ASR finished but no subtitle content was returned.";
                    await UpdateVideoSubtitleStateAsync(video, string.Empty, emptySubtitleMessage);
                    return (false, emptySubtitleMessage, string.Empty);
                }

                await File.WriteAllTextAsync(subtitlePath, subtitleContent, Encoding.UTF8, cancellationToken);
                if (!string.IsNullOrWhiteSpace(transcribeResult.Payload.Text))
                {
                    await File.WriteAllTextAsync(textPath, transcribeResult.Payload.Text, Encoding.UTF8, cancellationToken);
                }

                await UpdateVideoSubtitleStateAsync(video, subtitlePath, "Subtitle generated via local ASR service.");
                return (true, "Subtitle generated via local ASR service.", subtitlePath);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[ASR] Subtitle generation failed, VideoId={VideoId}, Path={VideoPath}", video.Id, video.VideoSavePath);
                var message = $"Subtitle generation failed: {ex.Message}";
                await UpdateVideoSubtitleStateAsync(video, string.Empty, message);
                return (false, message, string.Empty);
            }
        }

        private static (bool Success, string Message, string ServiceUrl) ValidateConfig(AppConfig config)
        {
            if (config == null)
            {
                return (false, "Missing ASR config.", string.Empty);
            }

            if (string.IsNullOrWhiteSpace(config.AsrServiceUrl))
            {
                return (false, "Missing ASR service URL.", string.Empty);
            }

            var serviceUrl = config.AsrServiceUrl.Trim().TrimEnd('/');
            if (!Uri.TryCreate(serviceUrl, UriKind.Absolute, out _))
            {
                return (false, $"Invalid ASR service URL: {config.AsrServiceUrl}", serviceUrl);
            }

            return (true, string.Empty, serviceUrl);
        }

        private async Task<(bool Success, string Message, AsrTranscribePayload Payload)> TranscribeFileAsync(
            string serviceUrl,
            string videoPath,
            AppConfig config,
            CancellationToken cancellationToken,
            string titleForRecord = null)
        {
            var client = _httpClientFactory.CreateClient(ASR_HTTP_CLIENT);

            await using var fileStream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
            using var fileContent = new StreamContent(fileStream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");

            using var formContent = new MultipartFormDataContent();
            formContent.Add(fileContent, "file", Path.GetFileName(videoPath));

            // 带 display=title(视频标题)供 ASR 任务记录展示,替代磁盘文件名(可能乱码)
            var endpoint = BuildEndpoint(serviceUrl, "/api/transcribe");
            if (!string.IsNullOrWhiteSpace(titleForRecord))
            {
                endpoint += (endpoint.Contains('?') ? "&" : "?") + "title=" + Uri.EscapeDataString(titleForRecord);
            }

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = formContent
            };

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            var responseText = await response.Content.ReadAsStringAsync();

            if (!response.IsSuccessStatusCode)
            {
                return (false, $"ASR request failed with {(int)response.StatusCode}: {ExtractErrorMessage(responseText)}", null);
            }

            return ParseTranscribeResponse(responseText);
        }

        private static (bool Success, string Message, AsrTranscribePayload Payload) ParseTranscribeResponse(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return (false, "ASR service returned an empty response.", null);
            }

            try
            {
                var root = JObject.Parse(responseText);
                var payloadToken = root["data"] ?? root;
                var payload = new AsrTranscribePayload
                {
                    Text = ReadString(payloadToken, "text", "Text", "full_text", "fullText"),
                    DurationMs = ReadNullableLong(payloadToken, "duration_ms", "durationMs", "DurationMs")
                };

                var segmentsToken = payloadToken["segments"] ?? payloadToken["Segments"] ?? payloadToken["sentences"] ?? payloadToken["Sentences"];
                payload.Segments = ParseSegments(segmentsToken);

                if (!payload.Segments.Any() && string.IsNullOrWhiteSpace(payload.Text))
                {
                    return (false, "ASR service returned no text content.", null);
                }

                return (true, "ASR completed.", payload);
            }
            catch (Exception ex)
            {
                return (false, $"Failed to parse ASR response: {ex.Message}. Response: {TrimMessage(responseText)}", null);
            }
        }

        private static List<AsrSegment> ParseSegments(JToken segmentsToken)
        {
            var segments = new List<AsrSegment>();
            if (segmentsToken is not JArray segmentArray)
            {
                return segments;
            }

            foreach (var token in segmentArray)
            {
                var text = ReadString(token, "text", "Text", "sentence", "Sentence", "FinalSentence");
                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                var startMs = ReadTimeMs(token, new[] { "start_ms", "StartMs", "startMs" }, new[] { "start", "Start" });
                var endMs = ReadTimeMs(token, new[] { "end_ms", "EndMs", "endMs" }, new[] { "end", "End" });

                if (endMs <= startMs)
                {
                    endMs = startMs + 2000;
                }

                segments.Add(new AsrSegment
                {
                    StartMs = startMs,
                    EndMs = endMs,
                    Text = text.Trim()
                });
            }

            return segments;
        }

        private static long ReadTimeMs(JToken token, IEnumerable<string> millisecondFields, IEnumerable<string> secondFields)
        {
            foreach (var field in millisecondFields)
            {
                var value = ReadNumber(token?[field], true);
                if (value.HasValue)
                {
                    return Math.Max(0, value.Value);
                }
            }

            foreach (var field in secondFields)
            {
                var value = ReadNumber(token?[field], false);
                if (value.HasValue)
                {
                    return Math.Max(0, value.Value);
                }
            }

            return 0;
        }

        private static long? ReadNullableLong(JToken token, params string[] names)
        {
            foreach (var name in names)
            {
                var value = ReadNumber(token?[name], true);
                if (value.HasValue)
                {
                    return value.Value;
                }
            }

            return null;
        }

        private static long? ReadNumber(JToken token, bool isMilliseconds)
        {
            if (token == null || token.Type == JTokenType.Null)
            {
                return null;
            }

            if (double.TryParse(token.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var numericValue))
            {
                return isMilliseconds ? (long)Math.Round(numericValue) : (long)Math.Round(numericValue * 1000);
            }

            return null;
        }

        private static string ReadString(JToken token, params string[] names)
        {
            foreach (var name in names)
            {
                var value = token?[name]?.ToString();
                if (!string.IsNullOrWhiteSpace(value))
                {
                    return value;
                }
            }

            return string.Empty;
        }

        private static string BuildSrtContent(AsrTranscribePayload payload)
        {
            var segments = payload.Segments?.Where(segment => !string.IsNullOrWhiteSpace(segment.Text)).ToList() ?? new List<AsrSegment>();
            if (!segments.Any())
            {
                if (string.IsNullOrWhiteSpace(payload.Text))
                {
                    return string.Empty;
                }

                segments.Add(new AsrSegment
                {
                    StartMs = 0,
                    EndMs = Math.Max(payload.DurationMs ?? 5000, 5000),
                    Text = payload.Text.Trim()
                });
            }

            var builder = new StringBuilder();
            int index = 1;
            foreach (var segment in segments)
            {
                var safeEndMs = segment.EndMs > segment.StartMs ? segment.EndMs : segment.StartMs + 2000;
                builder.AppendLine(index.ToString(CultureInfo.InvariantCulture));
                builder.AppendLine($"{FormatSrtTime(segment.StartMs)} --> {FormatSrtTime(safeEndMs)}");
                builder.AppendLine(segment.Text.Trim());
                builder.AppendLine();
                index++;
            }

            return builder.ToString().Trim();
        }

        private static string FormatSrtTime(long milliseconds)
        {
            var safeMilliseconds = Math.Max(0, milliseconds);
            var timeSpan = TimeSpan.FromMilliseconds(safeMilliseconds);
            return $"{(int)timeSpan.TotalHours:00}:{timeSpan.Minutes:00}:{timeSpan.Seconds:00},{timeSpan.Milliseconds:000}";
        }

        private static string BuildEndpoint(string serviceUrl, string relativePath)
        {
            return $"{serviceUrl.TrimEnd('/')}{relativePath}";
        }

        private static string ExtractErrorMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return "Empty error response.";
            }

            try
            {
                var root = JObject.Parse(responseText);
                return ReadString(root, "message", "detail", "error", "Message", "Detail", "Error");
            }
            catch
            {
                return TrimMessage(responseText);
            }
        }

        private static string ExtractHealthMessage(string responseText)
        {
            if (string.IsNullOrWhiteSpace(responseText))
            {
                return string.Empty;
            }

            try
            {
                var root = JObject.Parse(responseText);
                return ReadString(root, "message", "status", "detail", "Message", "Status", "Detail");
            }
            catch
            {
                return TrimMessage(responseText);
            }
        }

        private static string TrimMessage(string content)
        {
            if (string.IsNullOrWhiteSpace(content))
            {
                return string.Empty;
            }

            var singleLine = content.Replace("\r", " ").Replace("\n", " ").Trim();
            return singleLine.Length <= 300 ? singleLine : singleLine[..300];
        }

        private async Task UpdateVideoSubtitleStateAsync(DouyinVideo video, string subtitlePath, string statusMessage)
        {
            video.SubtitleSavePath = subtitlePath;
            video.SubtitleStatusMsg = statusMessage;
            video.SubtitleCreateTime = string.IsNullOrWhiteSpace(subtitlePath) ? null : DateTime.Now;
            await _videoRepository.UpdateAsync(video);
        }

        private sealed class AsrTranscribePayload
        {
            public string Text { get; set; }

            public long? DurationMs { get; set; }

            public List<AsrSegment> Segments { get; set; } = new();
        }

        private sealed class AsrSegment
        {
            public long StartMs { get; set; }

            public long EndMs { get; set; }

            public string Text { get; set; }
        }
    }
}
