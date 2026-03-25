using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Text.Json;
using System.Collections.Concurrent;
using Transfarr.Shared.Models;
using System.Collections.Generic;
using System.Linq;

namespace Transfarr.Node.Core;

public class TransferServer(ShareManager shareManager, ShareDatabase db)
{
    private TcpListener? listener;
    private readonly CancellationTokenSource cts = new();
    
    public int ListenPort { get; private set; }
    public ConcurrentDictionary<string, UploadItem> ActiveUploads { get; } = new();
    public event Action<IEnumerable<UploadItem>>? OnUploadsChanged;
    public event Action<long>? OnUploadComplete;
    public event Action<TcpClient, string>? OnReverseConnectionReceived;

    public void Start()
    {
        var portStr = db.GetSetting("P2PPort");
        int targetPort = 5151; // Changed default from 0 to 5151
        if (int.TryParse(portStr, out var p)) targetPort = p;

        try 
        {
            // Bind to specified port or 0 (any)
            listener = new TcpListener(IPAddress.Any, targetPort);
            listener.Start();
            ListenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Console.WriteLine($"[TransferServer] Listening for direct P2P connections on port {ListenPort}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransferServer] Failed to bind to target port {targetPort}: {ex.Message}. Falling back to random port.");
            listener = new TcpListener(IPAddress.Any, 0);
            listener.Start();
            ListenPort = ((IPEndPoint)listener.LocalEndpoint).Port;
            Console.WriteLine($"[TransferServer] Listening on fallback port {ListenPort}");
        }
        
        _ = AcceptClientsAsync(cts.Token);
    }
    
    public void Stop()
    {
        cts.Cancel();
        listener?.Stop();
        listener = null;
    }

    private async Task AcceptClientsAsync(CancellationToken token)
    {
        while (listener != null && !token.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(token);
                _ = HandleClientAsync(client, token);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransferServer] Error accepting client: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            try
            {
                var requestLine = await reader.ReadLineAsync(token);
                if (string.IsNullOrEmpty(requestLine)) return;

                if (requestLine.StartsWith("REQ_FILE|"))
                {
                    var parts = requestLine.Split('|');
                    if (parts.Length == 4)
                    {
                        string tth = parts[1];
                        long offset = long.Parse(parts[2]);
                        long size = long.Parse(parts[3]);

                        string? filePath = shareManager.GetLocalPathsByTth(tth).FirstOrDefault();
                        if (filePath != null && File.Exists(filePath))
                        {
                            await SendFileContentAsync(stream, filePath, offset, size, tth, client.Client.RemoteEndPoint?.ToString() ?? "Unknown", token);
                        }
                    }
                }
                else if (requestLine.StartsWith("REQ_DIR|"))
                {
                    var path = requestLine.Substring("REQ_DIR|".Length);
                    await SendDirectoryListAsync(stream, path);
                }
                else if (requestLine == "REQ_LIST|")
                {
                    await SendFullFileListAsync(stream);
                }
                else if (requestLine.StartsWith("CB_READY|"))
                {
                    var hash = requestLine.Split('|')[1];
                    OnReverseConnectionReceived?.Invoke(client, hash);
                    return; // Handed off to DownloadManager, do not dispose here
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransferServer] Client handle error: {ex.Message}");
            }
        }
    }

    private async Task SendFullFileListAsync(Stream stream)
    {
        string json = shareManager.GetLocalFileListJson();
        byte[] data = Encoding.UTF8.GetBytes(json);
        await stream.WriteAsync(BitConverter.GetBytes(data.Length).AsMemory());
        await stream.WriteAsync(data.AsMemory());
    }

    private async Task SendDirectoryListAsync(Stream stream, string path)
    {
        var item = shareManager.GetFileListItemByPath(path);
        if (item != null)
        {
            var json = JsonSerializer.Serialize(item);
            byte[] data = Encoding.UTF8.GetBytes(json);
            byte[] len = BitConverter.GetBytes(data.Length);
            await stream.WriteAsync(len);
            await stream.WriteAsync(data);
        }
    }

    private async Task SendFileContentAsync(Stream stream, string filePath, long offset, long size, string tth, string remoteIp, CancellationToken token)
    {
        var uploadId = Guid.NewGuid().ToString("N");
        var upload = new UploadItem
        {
            Id = uploadId,
            FileName = Path.GetFileName(filePath),
            Tth = tth,
            TotalSize = size,
            RemoteIp = remoteIp
        };

        ActiveUploads[uploadId] = upload;
        OnUploadsChanged?.Invoke(ActiveUploads.Values.ToList());

        try
        {
            using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fileStream.Seek(offset, SeekOrigin.Begin);
            byte[] buffer = new byte[65536];
            long remaining = size;
            DateTime lastUpdate = DateTime.Now;
            long bytesSinceUpdate = 0;

            while (remaining > 0 && !token.IsCancellationRequested)
            {
                int read = await fileStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token);
                if (read == 0) break;
                await stream.WriteAsync(buffer.AsMemory(0, read), token);
                remaining -= read;

                upload.BytesTransferred += read;
                bytesSinceUpdate += read;

                if ((DateTime.Now - lastUpdate).TotalMilliseconds > 500)
                {
                    var elapsed = (DateTime.Now - lastUpdate).TotalSeconds;
                    upload.SpeedMBps = (bytesSinceUpdate / 1024.0 / 1024.0) / elapsed;
                    bytesSinceUpdate = 0;
                    lastUpdate = DateTime.Now;
                    OnUploadsChanged?.Invoke(ActiveUploads.Values.ToList());
                }
            }

            if (remaining == 0)
            {
                OnUploadComplete?.Invoke(size);
            }
        }
        finally
        {
            ActiveUploads.TryRemove(uploadId, out _);
            OnUploadsChanged?.Invoke(ActiveUploads.Values.ToList());
        }
    }

    public async Task ConnectToPassiveDownloader(string ip, int port, string fileHash, CancellationToken token = default)
    {
        try
        {
            Console.WriteLine($"[TransferServer] Initiating ConnectBack to {ip}:{port} for {fileHash}...");
            using var client = new TcpClient();
            await client.ConnectAsync(ip, port, token);
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);

            // Notify the downloader that we are responding to their ConnectBack request
            await writer.WriteLineAsync($"CB_READY|{fileHash}");
            await writer.FlushAsync();

            // Wait for the downloader to send the actually desired request
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            var reqLine = await reader.ReadLineAsync(token);
            if (reqLine == null) return;

            if (reqLine.StartsWith("REQ_FILE|"))
            {
                var parts = reqLine.Split('|');
                if (parts.Length == 4)
                {
                    long offset = long.Parse(parts[2]);
                    long size = long.Parse(parts[3]);

                    string? filePath = shareManager.GetLocalPathsByTth(fileHash).FirstOrDefault();
                    if (filePath != null && File.Exists(filePath))
                    {
                        await SendFileContentAsync(stream, filePath, offset, size, fileHash, ip, token);
                    }
                }
            }
            else if (reqLine == "REQ_LIST|")
            {
                await SendFullFileListAsync(stream);
            }
            else if (reqLine.StartsWith("REQ_DIR|"))
            {
                var path = reqLine.Substring("REQ_DIR|".Length);
                await SendDirectoryListAsync(stream, path);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[TransferServer] ConnectBack failed: {ex.Message}");
        }
    }
}
