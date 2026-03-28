using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Transfarr.Shared.Models;

using Microsoft.Extensions.Options;
using Transfarr.Node.Options;

namespace Transfarr.Node.Core;

public class DownloadManager(ShareDatabase db, SystemLogger logger, IOptions<NodeOptions> options)
{
    private readonly NodeOptions options = options.Value;
    private readonly ConcurrentDictionary<string, DownloadItem> items = new();
    private readonly ConcurrentDictionary<string, DownloadItem> activeDownloads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TcpClient>> pendingReverseConnections = new();
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly System.Diagnostics.Stopwatch progressStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private long lastProgressTicks = 0;
    
    public Func<string, string, Task>? RequestConnectBackAction { get; set; }
    public Func<string, string, Task<IEnumerable<string>>>? RequestNegotiationAction { get; set; }
    public event Action? OnQueueChanged;

    public void Shutdown()
    {
        shutdownCts.Cancel();
    }

    public List<DownloadItem> AllItems => items.Values.ToList();

    public string DownloadsFolder { get; private set; } = "";
    public bool IsConfigured { get; private set; }

    public void Initialize()
    {
        var savedPath = db.GetSetting("DownloadsFolder");
        if (string.IsNullOrEmpty(savedPath))
        {
            savedPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, options.Storage.DefaultDownloadsFolder);
        }

        DownloadsFolder = savedPath;
        IsConfigured = true;
        Directory.CreateDirectory(DownloadsFolder);

        // Load Persistent Queue
        var savedItems = db.GetDownloadQueue();
        foreach (var item in savedItems)
        {
            // Reset status if it was "Downloading" to "Queued" or "Error (Interrupted)"
            if (item.Status == "Downloading") item.Status = "Queued";
            items.TryAdd(item.Id, item);
        }
    }

    public void SetDownloadsFolder(string path)
    {
        DownloadsFolder = path;
        IsConfigured = true;
        db.SaveSetting("DownloadsFolder", path);
        Directory.CreateDirectory(DownloadsFolder);
        logger.LogInfo($"Download directory set to: {path}");
    }

    public void AddToQueue(PeerInfo targetPeer, string fileName, long fileSize, string tth, string relativePath = "")
    {
        if (!IsConfigured) return;

        var item = new DownloadItem
        {
            TargetPeerId = targetPeer.PeerId,
            TargetPeer = targetPeer,
            FileName = fileName,
            FileSize = fileSize,
            Tth = tth,
            RelativePath = relativePath
        };
        items.TryAdd(item.Id, item);
        db.UpsertDownloadItem(item); // Persist
        OnQueueChanged?.Invoke();
        _ = ProcessQueueAsync();
    }

    public void RemoveFromQueue(string itemId)
    {
        if (items.TryRemove(itemId, out var item))
        {
            db.RemoveDownloadItem(itemId); // Persist
            if (activeDownloads.TryGetValue(itemId, out _))
            {
                activeDownloads.TryRemove(itemId, out _);
            }
            OnQueueChanged?.Invoke();
            _ = ProcessQueueAsync();
        }
    }

    public void AddFolderToQueue(PeerInfo targetPeer, FileListItem folder, string baseRelativePath = "")
    {
        if (!IsConfigured) return;

        string currentPath = Path.Combine(baseRelativePath, folder.Name);
        
        if (folder.Children != null)
        {
            foreach (var child in folder.Children)
            {
                if (child.IsDirectory)
                {
                    AddFolderToQueue(targetPeer, child, currentPath);
                }
                else
                {
                    AddToQueue(targetPeer, child.Name, child.Size, child.Tth, currentPath);
                }
            }
        }
    }

    public async Task AddFolderByPathToQueue(PeerInfo targetPeer, string virtualPath)
    {
        if (!IsConfigured) return;

        try
        {
            TcpClient client;
            Stream stream;
            
            if (targetPeer.IsPassive)
            {
                logger.LogInfo($"[Download] Peer {targetPeer.Name} is passive. Requesting ConnectBack for directory {virtualPath}...");
                var tcs = new TaskCompletionSource<TcpClient>();
                var reqId = "ADL_DIR_" + virtualPath.GetHashCode();
                HandleReverseConnectionRequest(reqId, tcs);
                
                if (RequestConnectBackAction != null)
                {
                    await RequestConnectBackAction(targetPeer.PeerId, reqId);
                }
                
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000, shutdownCts.Token));
                if (completedTask != tcs.Task) return;
                
                client = await tcs.Task;
                stream = client.GetStream();
            }
            else
            {
                // Stage 3: Negotiation
                logger.LogInfo($"[Download] Negotiating connection for directory {virtualPath} with {targetPeer.Name}...");
                IEnumerable<string> candidates = new List<string> { targetPeer.DirectIp };
                if (RequestNegotiationAction != null)
                {
                    candidates = await RequestNegotiationAction(targetPeer.PeerId, "DIR_LIST_" + virtualPath.GetHashCode());
                }

                TcpClient? finalClient = null;
                Stream? finalStream = null;
                bool connected = false;
                foreach (var ip in candidates.Distinct())
                {
                    if (string.IsNullOrEmpty(ip)) continue;
                    TcpClient candidateClient = new TcpClient();
                    try
                    {
                        logger.LogInfo($"[Download] Trying directory candidate: {ip}:{targetPeer.TransferPort}...");
                        var connectTask = candidateClient.ConnectAsync(ip, targetPeer.TransferPort, shutdownCts.Token).AsTask();
                        if (await Task.WhenAny(connectTask, Task.Delay(3000, shutdownCts.Token)) == connectTask)
                        {
                            await connectTask;
                            connected = true;
                            finalClient = candidateClient;
                            finalStream = candidateClient.GetStream();
                            logger.LogInfo($"[Download] Connected to {targetPeer.Name} for directory listing via {ip}");
                            break;
                        }
                        else
                        {
                            logger.LogWarning($"[Download] Directory connection timeout for {ip}");
                            candidateClient.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Download] Directory connection failed for {ip}: {ex.Message}");
                        candidateClient.Dispose();
                    }
                }

                if (!connected || finalClient == null || finalStream == null) 
                { 
                    throw new Exception($"Failed to connect to {targetPeer.Name} after trying all candidates ({string.Join(", ", candidates)})."); 
                }
                client = finalClient;
                stream = finalStream;
            }

            using (client)
            using (stream)
            {
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                
                await writer.WriteLineAsync($"REQ_DIR|{virtualPath}");
                await writer.FlushAsync();
                
                byte[] lenBuffer = new byte[4];
                int br = 0;
                while(br < 4) {
                    int r = await stream.ReadAsync(lenBuffer.AsMemory(br, 4 - br));
                    if (r == 0) return;
                    br += r;
                }
                int len = BitConverter.ToInt32(lenBuffer, 0);
                
                byte[] data = new byte[len];
                int totalRead = 0;
                while (totalRead < len)
                {
                    int r = await stream.ReadAsync(data.AsMemory(totalRead, len - totalRead));
                    if (r == 0) break;
                    totalRead += r;
                }
                
                var json = Encoding.UTF8.GetString(data);
                var folder = System.Text.Json.JsonSerializer.Deserialize<FileListItem>(json);
                if (folder != null)
                {
                    AddFolderToQueue(targetPeer, folder, "");
                }
            }
        }
        catch (Exception ex)
        {
            logger.LogError($"[Download] Failed to fetch folder structure for {virtualPath}: {ex.Message}");
        }
    }

    private async Task ProcessQueueAsync()
    {
        var pendingQueue = items.Values.Where(q => q.Status == "Queued").ToList();

        foreach (var item in pendingQueue)
        {
            if (!activeDownloads.Values.Any(a => a.TargetPeerId == item.TargetPeerId))
            {
                item.Status = "Downloading";
                db.UpsertDownloadItem(item);
                activeDownloads.TryAdd(item.Id, item);
                OnQueueChanged?.Invoke();
                
                _ = DownloadFileAsync(item);
            }
        }
    }

    public void HandleReverseConnection(TcpClient client, string fileHash)
    {
        if (pendingReverseConnections.TryRemove(fileHash, out var tcs))
        {
            tcs.TrySetResult(client);
        }
        else
        {
            // No one is waiting for this? Close it.
            client.Dispose();
        }
    }

    public void HandleReverseConnectionRequest(string identifier, TaskCompletionSource<TcpClient> tcs)
    {
        pendingReverseConnections[identifier] = tcs;
    }

    private async Task DownloadFileAsync(DownloadItem item)
    {
        string targetDir = string.IsNullOrEmpty(item.RelativePath) 
            ? DownloadsFolder 
            : Path.Combine(DownloadsFolder, item.RelativePath);
            
        Directory.CreateDirectory(targetDir);
        string filePath = Path.Combine(targetDir, item.FileName);

        try
        {
            // Collision Handling: Ensure we don't overwrite existing files
            if (item.BytesDownloaded == 0)
            {
                filePath = GetUniqueFilePath(targetDir, item.FileName);
                item.FileName = Path.GetFileName(filePath);
            }

            logger.LogInfo($"[Download] Starting: {item.FileName} from {item.TargetPeer.Name}");
            using var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None);
            
            item.BytesDownloaded = fs.Length;
            if (item.BytesDownloaded >= item.FileSize && item.FileSize > 0)
            {
                logger.LogInfo($"[Download] Already finished: {item.FileName}");
                CompleteDownload(item);
                return;
            }
            
            fs.Seek(item.BytesDownloaded, SeekOrigin.Begin);

            TcpClient client;
            Stream stream;

            if (item.TargetPeer.IsPassive)
            {
                logger.LogInfo($"[Download] Peer {item.TargetPeer.Name} is passive. Requesting ConnectBack...");
                var tcs = new TaskCompletionSource<TcpClient>();
                pendingReverseConnections[item.Tth] = tcs;

                if (RequestConnectBackAction != null)
                {
                    await RequestConnectBackAction(item.TargetPeerId, item.Tth);
                }

                // Wait for the reverse connection
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000, shutdownCts.Token));
                if (completedTask != tcs.Task)
                {
                    pendingReverseConnections.TryRemove(item.Tth, out _);
                    throw new Exception("Timeout waiting for uploader to connect back or shutdown requested.");
                }

                client = await tcs.Task;
                stream = client.GetStream();
                
                // Now that we have the reverse connection, we must send the REQ_FILE command
                // so the uploader (performing ConnectBack) knows what to send.
                var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                long bytesRemaining = item.FileSize - item.BytesDownloaded;
                await writer.WriteLineAsync($"REQ_FILE|{item.Tth}|{item.BytesDownloaded}|{bytesRemaining}");
                await writer.FlushAsync();
            }
            else
            {
                // Stage 3: Negotiation
                logger.LogInfo($"[Download] Negotiating connection for file {item.FileName} with {item.TargetPeer.Name}...");
                IEnumerable<string> candidates = new List<string> { item.TargetPeer.DirectIp };
                if (RequestNegotiationAction != null)
                {
                    candidates = await RequestNegotiationAction(item.TargetPeerId, item.Id);
                }

                TcpClient? finalClient = null;
                Stream? finalStream = null;
                bool connected = false;
                foreach (var ip in candidates.Distinct())
                {
                    if (string.IsNullOrEmpty(ip)) continue;
                    TcpClient candidateClient = new TcpClient();
                    try
                    {
                        logger.LogInfo($"[Download] Trying candidate: {ip}:{item.TargetPeer.TransferPort} for {item.FileName}...");
                        var connectTask = candidateClient.ConnectAsync(ip, item.TargetPeer.TransferPort, shutdownCts.Token).AsTask();
                        if (await Task.WhenAny(connectTask, Task.Delay(3000, shutdownCts.Token)) == connectTask)
                        {
                            await connectTask;
                            connected = true;
                            finalClient = candidateClient;
                            finalStream = candidateClient.GetStream();
                            logger.LogInfo($"[Download] Connected to {item.TargetPeer.Name} for {item.FileName} via {ip}");
                            break;
                        }
                        else
                        {
                            logger.LogWarning($"[Download] Connection timeout for {ip}");
                            candidateClient.Dispose();
                        }
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning($"[Download] Connection failed for {ip}: {ex.Message}");
                        candidateClient.Dispose();
                    }
                }

                if (!connected || finalClient == null || finalStream == null) 
                {
                    throw new Exception($"Failed to connect after trying all candidates ({string.Join(", ", candidates)}).");
                }
                client = finalClient;
                stream = finalStream;
                
                using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                long bytesRemaining = item.FileSize - item.BytesDownloaded;
                await writer.WriteLineAsync($"REQ_FILE|{item.Tth}|{item.BytesDownloaded}|{bytesRemaining}");
                await writer.FlushAsync();
            }
            
            using (client)
            using (stream)
            {
                byte[] buffer = new byte[81920];
            int read;
            bool wasAborted = false;

            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length, shutdownCts.Token)) > 0)
            {
                // Check if we were removed from queue during download
                if (!items.ContainsKey(item.Id))
                {
                    logger.LogWarning($"[Download] Aborted: {item.FileName} (Removed from Queue)");
                    wasAborted = true;
                    break;
                }

                await fs.WriteAsync(buffer, 0, read);
                item.BytesDownloaded += read;

                // Throttle progress updates to ~5 times per second
                long currentTicks = progressStopwatch.ElapsedMilliseconds;
                if (currentTicks - lastProgressTicks > 200)
                {
                    lastProgressTicks = currentTicks;
                    db.UpsertDownloadItem(item); // Save progress periodically
                    OnQueueChanged?.Invoke();
                }
            }
            
            // Handle Cleanup if Aborted
            if (wasAborted)
            {
                fs.Close();
                if (File.Exists(filePath)) 
                {
                    File.Delete(filePath);
                    logger.LogInfo($"[Download] Cleaned up partial file: {filePath}");
                }
                return;
            }

            // Final update after loop to ensure 100% shows
            OnQueueChanged?.Invoke();

            if (item.BytesDownloaded >= item.FileSize)
            {
                CompleteDownload(item);
            }
            else
            {
                item.Status = "Error (Incomplete)";
                logger.LogWarning($"[Download] Incomplete: {item.FileName} ({item.BytesDownloaded}/{item.FileSize} bytes)");
                CompleteDownload(item);
            }
            }
        }
        catch(Exception ex)
        {
            // Only update status if item is still in dictionary
            if (items.TryGetValue(item.Id, out var currentItem))
            {
                currentItem.Status = "Error";
                logger.LogError($"[Download] Failed {item.FileName}: {ex.Message}");
                CompleteDownload(currentItem);
            }
        }
    }

    private void CompleteDownload(DownloadItem item)
    {
        if (item.Status == "Downloading") 
        {
            item.Status = "Finished";
            logger.LogInfo($"[Download] Success: {item.FileName}");
        }
        db.UpsertDownloadItem(item); // Final status save
        activeDownloads.TryRemove(item.Id, out _);
        OnQueueChanged?.Invoke();
        _ = ProcessQueueAsync();
    }

    private string GetUniqueFilePath(string folder, string fileName)
    {
        string fullPath = Path.Combine(folder, fileName);
        if (!File.Exists(fullPath)) return fullPath;

        string name = Path.GetFileNameWithoutExtension(fileName);
        string ext = Path.GetExtension(fileName);
        int count = 1;

        while (File.Exists(fullPath))
        {
            fullPath = Path.Combine(folder, $"{name} ({count++}){ext}");
        }

        return fullPath;
    }
}
