using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

namespace DynamicIsland.AirDrop
{
    public class AirDropReceiverServer
    {
        public static AirDropReceiverServer Instance { get; } = new AirDropReceiverServer();

        public const int LocalSendPort = 53317;
        public const string DeviceAlias = "infinity";

        private TcpListener? tcpListener;
        private CancellationTokenSource? cts;

        private TaskCompletionSource<bool>? currentDecisionTcs;
        private AirDropIncomingRequest? currentActiveRequest;
        private readonly List<string> receivedFilePaths = new();
        private long totalSessionBytes = 0;
        private long totalTransferredBytes = 0;

        public event Action<AirDropIncomingRequest>? OnIncomingTransferRequested;
        public event Action<double, long, long>? OnProgressUpdated;
        public event Action<string, List<string>>? OnTransferCompleted;

        public bool IsRunning { get; private set; }

        public string DestinationFolder
        {
            get
            {
                string downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "AirDrop");
                if (!Directory.Exists(downloads))
                {
                    try { Directory.CreateDirectory(downloads); } catch { }
                }
                return downloads;
            }
        }

        public void Start()
        {
            if (IsRunning) return;
            try
            {
                tcpListener = new TcpListener(IPAddress.Any, LocalSendPort);
                tcpListener.Start();
                IsRunning = true;
                cts = new CancellationTokenSource();

                Task.Run(() => AcceptClientsLoop(cts.Token));
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "airdrop_server.log"), $"[AirDropReceiverServer] Running on 0.0.0.0:{LocalSendPort} as '{DeviceAlias}' at {DateTime.Now}\n");
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "airdrop_server.log"), $"[AirDropReceiverServer] Start failed: {ex}\n");
            }
        }

        public void Stop()
        {
            IsRunning = false;
            cts?.Cancel();
            try { tcpListener?.Stop(); } catch { }
            tcpListener = null;
        }

        public void AcceptCurrentTransfer()
        {
            currentDecisionTcs?.TrySetResult(true);
        }

        public void DeclineCurrentTransfer()
        {
            currentDecisionTcs?.TrySetResult(false);
        }

        private async Task AcceptClientsLoop(CancellationToken token)
        {
            while (!token.IsCancellationRequested && tcpListener != null)
            {
                try
                {
                    var client = await tcpListener.AcceptTcpClientAsync(token);
                    _ = Task.Run(() => HandleClientAsync(client, token));
                }
                catch (OperationCanceledException) { break; }
                catch { }
            }
        }

        private async Task HandleClientAsync(TcpClient client, CancellationToken token)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                try
                {
                    byte[] initialBuffer = new byte[8192];
                    int bytesRead = await stream.ReadAsync(initialBuffer, 0, initialBuffer.Length, token);
                    if (bytesRead <= 0) return;

                    int headerEnd = FindHeaderEnd(initialBuffer, bytesRead);
                    if (headerEnd == -1) return;

                    string headerStr = Encoding.UTF8.GetString(initialBuffer, 0, headerEnd);
                    var lines = headerStr.Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length == 0) return;

                    var firstLine = lines[0].Split(' ');
                    if (firstLine.Length < 2) return;

                    string method = firstLine[0].ToUpperInvariant();
                    string rawUrl = firstLine[1];
                    string path = rawUrl.Split('?')[0].TrimEnd('/');

                    long contentLength = 0;
                    foreach (var l in lines)
                    {
                        if (l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                        {
                            long.TryParse(l.Substring(15).Trim(), out contentLength);
                        }
                    }

                    var queryParams = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    int qIdx = rawUrl.IndexOf('?');
                    if (qIdx >= 0 && qIdx < rawUrl.Length - 1)
                    {
                        string qStr = rawUrl.Substring(qIdx + 1);
                        foreach (var kv in qStr.Split('&'))
                        {
                            var parts = kv.Split('=');
                            if (parts.Length == 2)
                            {
                                queryParams[Uri.UnescapeDataString(parts[0])] = Uri.UnescapeDataString(parts[1]);
                            }
                        }
                    }

                    if (method == "OPTIONS")
                    {
                        await SendHttpResponseAsync(stream, 200, "OK", "text/plain", "", token);
                        return;
                    }

                    if (path.EndsWith("/info", StringComparison.OrdinalIgnoreCase) ||
                        path.EndsWith("/register", StringComparison.OrdinalIgnoreCase))
                    {
                        await HandleInfoRequestAsync(stream, token);
                    }
                    else if (path.EndsWith("/prepare-upload", StringComparison.OrdinalIgnoreCase) && method == "POST")
                    {
                        int bodyBytesInInitial = bytesRead - headerEnd;
                        byte[] initialBodyPart = new byte[bodyBytesInInitial];
                        Array.Copy(initialBuffer, headerEnd, initialBodyPart, 0, bodyBytesInInitial);
                        await HandlePrepareUploadAsync(stream, initialBodyPart, contentLength, token);
                    }
                    else if (path.EndsWith("/upload", StringComparison.OrdinalIgnoreCase) && method == "POST")
                    {
                        int bodyBytesInInitial = bytesRead - headerEnd;
                        byte[] initialBodyPart = new byte[bodyBytesInInitial];
                        Array.Copy(initialBuffer, headerEnd, initialBodyPart, 0, bodyBytesInInitial);
                        await HandleUploadFileAsync(stream, initialBodyPart, queryParams, contentLength, token);
                    }
                    else if (path.EndsWith("/cancel", StringComparison.OrdinalIgnoreCase) && method == "POST")
                    {
                        await SendHttpResponseAsync(stream, 200, "OK", "application/json", "{}", token);
                    }
                    else
                    {
                        await SendHttpResponseAsync(stream, 404, "Not Found", "text/plain", "Not Found", token);
                    }
                }
                catch { }
            }
        }

        private static int FindHeaderEnd(byte[] buffer, int length)
        {
            for (int i = 0; i <= length - 4; i++)
            {
                if (buffer[i] == '\r' && buffer[i + 1] == '\n' && buffer[i + 2] == '\r' && buffer[i + 3] == '\n')
                {
                    return i + 4;
                }
            }
            return -1;
        }

        private async Task HandleInfoRequestAsync(NetworkStream stream, CancellationToken token)
        {
            var info = new
            {
                alias = DeviceAlias,
                version = "2.0",
                deviceModel = "Windows",
                deviceType = "desktop",
                fingerprint = "dynamic-island-pc",
                port = LocalSendPort,
                protocol = "http",
                download = true,
                announcement = true,
                announce = true
            };

            string json = JsonSerializer.Serialize(info);
            await SendHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, token);
        }

        private async Task HandlePrepareUploadAsync(NetworkStream stream, byte[] initialBodyPart, long contentLength, CancellationToken token)
        {
            byte[] fullBody = new byte[contentLength];
            Array.Copy(initialBodyPart, fullBody, Math.Min(initialBodyPart.Length, (int)contentLength));
            int totalRead = initialBodyPart.Length;

            while (totalRead < contentLength)
            {
                int read = await stream.ReadAsync(fullBody, totalRead, (int)(contentLength - totalRead), token);
                if (read <= 0) break;
                totalRead += read;
            }

            string body = Encoding.UTF8.GetString(fullBody);
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string senderAlias = "Nearby Phone";
            string deviceModel = "Mobile";
            string deviceType = "mobile";

            if (root.TryGetProperty("info", out var infoProp))
            {
                if (infoProp.TryGetProperty("alias", out var a)) senderAlias = a.GetString() ?? senderAlias;
                if (infoProp.TryGetProperty("deviceModel", out var dm)) deviceModel = dm.GetString() ?? deviceModel;
                if (infoProp.TryGetProperty("deviceType", out var dt)) deviceType = dt.GetString() ?? deviceType;
            }

            var incomingFiles = new List<AirDropIncomingFile>();
            if (root.TryGetProperty("files", out var filesProp))
            {
                foreach (var prop in filesProp.EnumerateObject())
                {
                    var fObj = prop.Value;
                    string fId = fObj.TryGetProperty("id", out var idProp) ? idProp.GetString() ?? prop.Name : prop.Name;
                    string fName = fObj.TryGetProperty("fileName", out var nameProp) ? nameProp.GetString() ?? "file" : "file";
                    long fSize = fObj.TryGetProperty("size", out var sizeProp) ? sizeProp.GetInt64() : 0;
                    string fType = fObj.TryGetProperty("fileType", out var typeProp) ? typeProp.GetString() ?? "" : "";
                    string? fPreview = fObj.TryGetProperty("preview", out var prevProp) ? prevProp.GetString() : null;

                    incomingFiles.Add(new AirDropIncomingFile
                    {
                        Id = fId,
                        FileName = fName,
                        Size = fSize,
                        FileType = fType,
                        Preview = fPreview
                    });
                }
            }

            if (incomingFiles.Count == 0)
            {
                await SendHttpResponseAsync(stream, 400, "Bad Request", "application/json", "{\"error\":\"No files\"}", token);
                return;
            }

            var incomingRequest = new AirDropIncomingRequest
            {
                SessionId = Guid.NewGuid().ToString("N"),
                SenderAlias = senderAlias,
                DeviceModel = deviceModel,
                DeviceType = deviceType,
                Files = incomingFiles
            };

            var firstPreview = incomingFiles.FirstOrDefault(f => !string.IsNullOrEmpty(f.Preview))?.Preview;
            if (!string.IsNullOrEmpty(firstPreview))
            {
                try
                {
                    byte[] prevBytes = Convert.FromBase64String(firstPreview);
                    using var ms = new MemoryStream(prevBytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    incomingRequest.Thumbnail = bmp;
                }
                catch { }
            }

            currentActiveRequest = incomingRequest;
            receivedFilePaths.Clear();
            totalSessionBytes = incomingFiles.Sum(f => f.Size);
            totalTransferredBytes = 0;

            currentDecisionTcs = new TaskCompletionSource<bool>();

            Application.Current?.Dispatcher.Invoke(() =>
            {
                OnIncomingTransferRequested?.Invoke(incomingRequest);
            });

            var completedTask = await Task.WhenAny(currentDecisionTcs.Task, Task.Delay(45000));
            bool isAccepted = completedTask == currentDecisionTcs.Task && currentDecisionTcs.Task.Result;

            if (isAccepted)
            {
                var filesMap = new Dictionary<string, string>();
                foreach (var f in incomingFiles)
                {
                    filesMap[f.Id] = Guid.NewGuid().ToString("N");
                }

                var responsePayload = new
                {
                    sessionId = incomingRequest.SessionId,
                    files = filesMap
                };

                string json = JsonSerializer.Serialize(responsePayload);
                await SendHttpResponseAsync(stream, 200, "OK", "application/json; charset=utf-8", json, token);
            }
            else
            {
                await SendHttpResponseAsync(stream, 403, "Forbidden", "application/json", "{\"error\":\"Declined\"}", token);
            }
        }

        private async Task HandleUploadFileAsync(NetworkStream stream, byte[] initialBodyPart, Dictionary<string, string> queryParams, long contentLength, CancellationToken token)
        {
            string? sessionId = queryParams.GetValueOrDefault("sessionId");
            string? fileId = queryParams.GetValueOrDefault("fileId");

            if (currentActiveRequest == null || (sessionId != null && currentActiveRequest.SessionId != sessionId))
            {
                await SendHttpResponseAsync(stream, 400, "Bad Request", "application/json", "{\"error\":\"Invalid session\"}", token);
                return;
            }

            var fileMeta = currentActiveRequest.Files.FirstOrDefault(f => f.Id == fileId) ?? currentActiveRequest.Files.FirstOrDefault();
            string fileName = fileMeta?.FileName ?? $"received_file_{Guid.NewGuid().ToString("N").Substring(0, 6)}";

            string destFolder = DestinationFolder;
            string destPath = GetUniqueFilePath(destFolder, fileName);

            byte[] buffer = new byte[64 * 1024];
            long bytesRemaining = contentLength;

            using (var fs = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (initialBodyPart.Length > 0)
                {
                    int toWrite = (int)Math.Min(initialBodyPart.Length, bytesRemaining);
                    await fs.WriteAsync(initialBodyPart, 0, toWrite, token);
                    bytesRemaining -= toWrite;
                    totalTransferredBytes += toWrite;
                }

                while (bytesRemaining > 0)
                {
                    int toRead = (int)Math.Min(buffer.Length, bytesRemaining);
                    int read = await stream.ReadAsync(buffer, 0, toRead, token);
                    if (read <= 0) break;

                    await fs.WriteAsync(buffer, 0, read, token);
                    bytesRemaining -= read;
                    totalTransferredBytes += read;

                    double progress = totalSessionBytes > 0
                        ? Math.Clamp((double)totalTransferredBytes / totalSessionBytes, 0.0, 1.0)
                        : 1.0;

                    Application.Current?.Dispatcher.Invoke(() =>
                    {
                        OnProgressUpdated?.Invoke(progress, totalTransferredBytes, totalSessionBytes);
                    });
                }
            }

            receivedFilePaths.Add(destPath);
            await SendHttpResponseAsync(stream, 200, "OK", "application/json", "{}", token);

            if (receivedFilePaths.Count >= currentActiveRequest.Files.Count)
            {
                var savedList = new List<string>(receivedFilePaths);
                string sender = currentActiveRequest.SenderAlias;

                Application.Current?.Dispatcher.Invoke(() =>
                {
                    OnTransferCompleted?.Invoke(sender, savedList);
                });

                currentActiveRequest = null;
            }
        }

        private static async Task SendHttpResponseAsync(NetworkStream stream, int statusCode, string statusText, string contentType, string body, CancellationToken token)
        {
            byte[] bodyBytes = Encoding.UTF8.GetBytes(body);
            string headers = $"HTTP/1.1 {statusCode} {statusText}\r\n" +
                             $"Content-Type: {contentType}\r\n" +
                             $"Content-Length: {bodyBytes.Length}\r\n" +
                             "Access-Control-Allow-Origin: *\r\n" +
                             "Access-Control-Allow-Methods: GET, POST, OPTIONS\r\n" +
                             "Access-Control-Allow-Headers: Content-Type, Authorization\r\n" +
                             "Connection: close\r\n\r\n";

            byte[] headerBytes = Encoding.UTF8.GetBytes(headers);
            await stream.WriteAsync(headerBytes, 0, headerBytes.Length, token);
            if (bodyBytes.Length > 0)
            {
                await stream.WriteAsync(bodyBytes, 0, bodyBytes.Length, token);
            }
            await stream.FlushAsync(token);
        }

        private static string GetUniqueFilePath(string folder, string fileName)
        {
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            string ext = Path.GetExtension(fileName);
            string fullPath = Path.Combine(folder, fileName);

            int count = 1;
            while (File.Exists(fullPath))
            {
                fullPath = Path.Combine(folder, $"{nameWithoutExt} ({count}){ext}");
                count++;
            }
            return fullPath;
        }
    }
}
