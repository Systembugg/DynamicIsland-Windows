using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DynamicIsland.AirDrop
{
    public class ProgressStreamContent : HttpContent
    {
        private readonly Stream fileStream;
        private readonly int bufferSize;
        private readonly Action<long, long> onProgress;
        private readonly long totalBytes;

        public ProgressStreamContent(Stream stream, int bufferSize, Action<long, long> onProgress, string contentType = "application/octet-stream")
        {
            this.fileStream = stream;
            this.bufferSize = bufferSize;
            this.onProgress = onProgress;
            this.totalBytes = stream.Length;
            Headers.ContentType = new MediaTypeHeaderValue(contentType);
            Headers.ContentLength = totalBytes;
        }

        protected override async Task SerializeToStreamAsync(Stream stream, System.Net.TransportContext? context)
        {
            byte[] buffer = new byte[bufferSize];
            long totalSent = 0;
            fileStream.Position = 0;
            int bytesRead;
            while ((bytesRead = await fileStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                await stream.WriteAsync(buffer, 0, bytesRead);
                totalSent += bytesRead;
                onProgress?.Invoke(totalSent, totalBytes);
            }
        }

        protected override bool TryComputeLength(out long length) { length = totalBytes; return true; }

        protected override void Dispose(bool disposing) { if (disposing) fileStream.Dispose(); base.Dispose(disposing); }
    }

    public class AirDropClient
    {
        public static AirDropClient Instance { get; } = new AirDropClient();

        private readonly HttpClient secureClient;
        private readonly HttpClient plainClient;
        private CancellationTokenSource? currentTransferCts;
        private string? activeSessionId;
        private string? activeTargetIp;
        private string activeProtocol = "https";
        private int activePort;
        private string lastError = "";

        public string LastError => lastError;

        public AirDropClient()
        {
            var secureHandler = new SocketsHttpHandler
            {
                SslOptions = new SslClientAuthenticationOptions
                {
                    RemoteCertificateValidationCallback = (sender, cert, chain, errors) => true
                },
                ConnectTimeout = TimeSpan.FromSeconds(8)
            };
            secureClient = new HttpClient(secureHandler) { Timeout = TimeSpan.FromMinutes(10) };
            plainClient = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(8) }) { Timeout = TimeSpan.FromMinutes(10) };
        }

        private HttpClient GetClient(string proto) => proto == "https" ? secureClient : plainClient;

        public async Task<bool> SendFileAsync(
            AirDropDevice device, string filePath, Action<string> onStatus, Action<double> onProgress, CancellationToken token = default)
        {
            if (!File.Exists(filePath)) { lastError = "File not found"; return false; }

            currentTransferCts = CancellationTokenSource.CreateLinkedTokenSource(token);
            var ct = currentTransferCts.Token;
            activeTargetIp = device.IpAddress;
            activePort = device.Port;
            activeProtocol = string.IsNullOrEmpty(device.Protocol) ? "https" : device.Protocol;

            try
            {
                var fileInfo = new FileInfo(filePath);
                string fileId = "file-" + Guid.NewGuid().ToString("N").Substring(0, 8);
                string fileName = fileInfo.Name;
                long fileSize = fileInfo.Length;
                string mime = GetMimeType(fileName);
                string? preview = null;

                if (mime == "text" && fileSize < 65536)
                {
                    try
                    {
                        preview = File.ReadAllText(filePath);
                        if (fileName.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                        {
                            fileName = preview.Length > 40 ? preview.Substring(0, 40).Replace("\r", " ").Replace("\n", " ") : preview;
                        }
                        if (preview.Length > 500) preview = preview.Substring(0, 500);
                    }
                    catch { }
                }

                var preparePayload = new
                {
                    info = new
                    {
                        alias = "Dynamic Island PC",
                        version = "2.0",
                        deviceModel = "Windows",
                        deviceType = "desktop",
                        fingerprint = "dynamic-island-" + Guid.NewGuid().ToString("N").Substring(0, 8),
                        port = 53317,
                        protocol = "https",
                        download = true
                    },
                    files = new Dictionary<string, object>
                    {
                        { fileId, new { id = fileId, fileName = fileName, size = fileSize, fileType = mime, sha256 = (string?)null, preview = preview } }
                    }
                };

                string jsonReq = JsonSerializer.Serialize(preparePayload);
                onStatus?.Invoke("Connecting to " + device.Alias + "...");

                string[][] attempts = new[]
                {
                    new[] { device.Protocol, "/api/localsend/v2/prepare-upload" },
                    new[] { "https", "/api/localsend/v2/prepare-upload" },
                    new[] { "http", "/api/localsend/v2/prepare-upload" },
                    new[] { "https", "/api/localsend/v1/prepare-upload" },
                    new[] { "http", "/api/localsend/v1/prepare-upload" },
                };

                HttpResponseMessage? prepareRes = null;
                string usedProtocol = activeProtocol;
                string usedApiBase = "/api/localsend/v2";

                foreach (var attempt in attempts)
                {
                    string proto = attempt[0];
                    string endpoint = attempt[1];
                    string apiBase = endpoint.Contains("/v1/") ? "/api/localsend/v1" : "/api/localsend/v2";
                    try
                    {
                        string prepareUrl = proto + "://" + device.IpAddress + ":" + device.Port + endpoint;
                        var content = new StringContent(jsonReq, Encoding.UTF8, "application/json");
                        onStatus?.Invoke("Trying " + proto.ToUpper() + "... Tap Accept on phone");
                        var client = GetClient(proto);
                        prepareRes = await client.PostAsync(prepareUrl, content, ct);
                        if (prepareRes.IsSuccessStatusCode)
                        {
                            usedProtocol = proto;
                            usedApiBase = apiBase;
                            activeProtocol = proto;
                            onStatus?.Invoke("Phone accepted! Sending...");
                            break;
                        }
                        else
                        {
                            int code = (int)prepareRes.StatusCode;
                            string respBody = await prepareRes.Content.ReadAsStringAsync(ct);
                            lastError = proto + " " + endpoint + ": HTTP " + code + " - " + respBody;
                            prepareRes = null;
                        }
                    }
                    catch (Exception ex)
                    {
                        lastError = proto + " " + endpoint + ": " + ex.GetType().Name + " - " + ex.Message;
                    }
                }

                if (prepareRes == null || !prepareRes.IsSuccessStatusCode)
                {
                    onStatus?.Invoke("Connection failed");
                    return false;
                }

                string prepareJson = await prepareRes.Content.ReadAsStringAsync(ct);
                using var doc = JsonDocument.Parse(prepareJson);
                var root = doc.RootElement;

                string sessionId = root.GetProperty("sessionId").GetString() ?? "";
                activeSessionId = sessionId;

                string fileToken = "";
                if (root.TryGetProperty("files", out var filesProp) && filesProp.TryGetProperty(fileId, out var tokenProp))
                {
                    fileToken = tokenProp.GetString() ?? "";
                }

                if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(fileToken))
                {
                    lastError = "Missing sessionId or fileToken in response";
                    return false;
                }

                onStatus?.Invoke("Sending to " + device.Alias + "...");

                string uploadUrl = usedProtocol + "://" + device.IpAddress + ":" + device.Port + usedApiBase + "/upload?sessionId=" + sessionId + "&fileId=" + fileId + "&token=" + fileToken;

                string uploadContentType = (mime == "text") ? "text/plain" : "application/octet-stream";
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
                using var progressContent = new ProgressStreamContent(fs, 65536, (sent, total) =>
                {
                    double p = total > 0 ? (double)sent / total : 0.0;
                    onProgress?.Invoke(Math.Clamp(p, 0.0, 1.0));
                }, uploadContentType);

                var uploadClient = GetClient(usedProtocol);
                var uploadRes = await uploadClient.PostAsync(uploadUrl, progressContent, ct);
                if (!uploadRes.IsSuccessStatusCode) lastError = "Upload HTTP " + (int)uploadRes.StatusCode;
                return uploadRes.IsSuccessStatusCode;
            }
            catch (OperationCanceledException) { lastError = "Cancelled"; return false; }
            catch (Exception ex) { lastError = ex.GetType().Name + ": " + ex.Message; return false; }
            finally { activeSessionId = null; }
        }

        public async Task CancelActiveTransferAsync()
        {
            try
            {
                currentTransferCts?.Cancel();
                if (!string.IsNullOrEmpty(activeSessionId) && !string.IsNullOrEmpty(activeTargetIp))
                {
                    var client = GetClient(activeProtocol);
                    await client.PostAsync(activeProtocol + "://" + activeTargetIp + ":" + activePort + "/api/localsend/v2/cancel?sessionId=" + activeSessionId, new StringContent("{}", Encoding.UTF8, "application/json"));
                }
            }
            catch { }
        }

        private static string GetMimeType(string fileName)
        {
            string ext = Path.GetExtension(fileName).ToLowerInvariant();
            return ext switch
            {
                ".txt" or ".url" => "text",
                ".jpg" or ".jpeg" or ".png" or ".webp" or ".gif" => "image",
                ".mp4" or ".mkv" or ".mov" or ".avi" => "video",
                ".mp3" or ".wav" or ".m4a" or ".flac" => "audio",
                ".pdf" => "pdf",
                ".apk" => "apk",
                _ => "file"
            };
        }
    }
}
