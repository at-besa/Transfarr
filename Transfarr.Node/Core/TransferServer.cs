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
    
    public int ListenPort { get; private set; }
    public ConcurrentDictionary<string, UploadItem> ActiveUploads { get; } = new();
    public event Action<IEnumerable<UploadItem>>? OnUploadsChanged;
    public event Action<long>? OnUploadComplete;

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
        
        _ = AcceptClientsAsync();
    }

    private async Task AcceptClientsAsync()
    {
        while (listener != null)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync();
                _ = HandleClientAsync(client);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransferServer] Error accepting client: {ex.Message}");
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        using (client)
        using (var stream = client.GetStream())
        using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
        {
            try
            {
                var requestLine = await reader.ReadLineAsync();
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
                            var uploadId = Guid.NewGuid().ToString("N");
                            var upload = new UploadItem 
                            { 
                                Id = uploadId, 
                                FileName = Path.GetFileName(filePath), 
                                Tth = tth, 
                                TotalSize = size, 
                                RemoteIp = client.Client.RemoteEndPoint?.ToString() ?? "Unknown" 
                            };
                            
                            ActiveUploads[uploadId] = upload;
                            OnUploadsChanged?.Invoke(ActiveUploads.Values.ToList());

                            try
                            {
                                using var fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
                                fileStream.Seek(offset, SeekOrigin.Begin);
                                byte[] buffer = new byte[65536]; // Larger buffer for speed
                                long remaining = size;
                                DateTime lastUpdate = DateTime.Now;
                                long bytesSinceUpdate = 0;

                                while (remaining > 0)
                                {
                                    int read = await fileStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
                                    if (read == 0) break;
                                    await stream.WriteAsync(buffer.AsMemory(0, read));
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
                    }
                }
                else if (requestLine.StartsWith("REQ_DIR|")) // Added REQ_DIR handler
                {
                    var path = requestLine.Substring("REQ_DIR|".Length);
                    var item = shareManager.GetFileListItemByPath(path);
                    if (item != null)
                    {
                        var json = JsonSerializer.Serialize(item);
                        byte[] data = System.Text.Encoding.UTF8.GetBytes(json);
                        byte[] len = BitConverter.GetBytes(data.Length);
                        await stream.WriteAsync(len);
                        await stream.WriteAsync(data);
                    }
                }
                else if (requestLine == "REQ_LIST|")
                {
                    string json = shareManager.GetLocalFileListJson();
                    byte[] data = Encoding.UTF8.GetBytes(json);
                    await stream.WriteAsync(BitConverter.GetBytes(data.Length).AsMemory());
                    await stream.WriteAsync(data.AsMemory());
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[TransferServer] Client handle error: {ex.Message}");
            }
        }
    }
}
