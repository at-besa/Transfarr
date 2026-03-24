using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Core;

public class DownloadManager(ShareDatabase db, SystemLogger logger)
{
    private readonly ConcurrentDictionary<string, DownloadItem> items = new();
    private readonly ConcurrentDictionary<string, DownloadItem> activeDownloads = new();
    private readonly System.Diagnostics.Stopwatch progressStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private long lastProgressTicks = 0;
    
    public event Action? OnQueueChanged;

    public List<DownloadItem> AllItems => items.Values.ToList();

    public string DownloadsFolder { get; private set; } = "";
    public bool IsConfigured { get; private set; }

    public void Initialize()
    {
        var savedPath = db.GetSetting("DownloadsFolder");
        if (!string.IsNullOrEmpty(savedPath))
        {
            DownloadsFolder = savedPath;
            IsConfigured = true;
            Directory.CreateDirectory(DownloadsFolder);
        }

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
            string ip = string.IsNullOrEmpty(targetPeer.DirectIp) ? "127.0.0.1" : targetPeer.DirectIp;
            int port = targetPeer.TransferPort;
            if (port == 0) return;

            using var client = new TcpClient();
            await client.ConnectAsync(ip, port);
            using var stream = client.GetStream();
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
                // We want the folder itself to be at the root of the downloads, not its parents
                AddFolderToQueue(targetPeer, folder, "");
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

    private async Task DownloadFileAsync(DownloadItem item)
    {
        string targetDir = string.IsNullOrEmpty(item.RelativePath) 
            ? DownloadsFolder 
            : Path.Combine(DownloadsFolder, item.RelativePath);
            
        Directory.CreateDirectory(targetDir);
        string filePath = Path.Combine(targetDir, item.FileName);

        try
        {
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

            string ip = string.IsNullOrEmpty(item.TargetPeer.DirectIp) ? "127.0.0.1" : item.TargetPeer.DirectIp;
            int port = item.TargetPeer.TransferPort;
            if (port == 0) throw new Exception("Remote peer has no active transfer port.");

            using var client = new TcpClient();
            await client.ConnectAsync(ip, port);
            
            using var stream = client.GetStream();
            using var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
            
            long bytesRemaining = item.FileSize - item.BytesDownloaded;
            await writer.WriteLineAsync($"REQ_FILE|{item.Tth}|{item.BytesDownloaded}|{bytesRemaining}");
            await writer.FlushAsync();
            
            byte[] buffer = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                // Check if we were removed from queue during download
                if (!items.ContainsKey(item.Id))
                {
                    logger.LogWarning($"[Download] Aborted: {item.FileName} (Removed from Queue)");
                    return; 
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
            
            // Checking one last time if still in items
            if (!items.ContainsKey(item.Id)) return;

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
}
