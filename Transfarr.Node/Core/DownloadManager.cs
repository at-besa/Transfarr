using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using Transfarr.Shared.Models;

using Microsoft.Extensions.Options;
using Transfarr.Node.Options;

namespace Transfarr.Node.Core;

public class DownloadManager(ShareDatabase db, SystemLogger logger, IOptions<NodeOptions> options, BandwidthController bandwidthController)
{
    private readonly NodeOptions options = options.Value;
    private readonly ConcurrentDictionary<string, DownloadItem> items = new();
    private readonly ConcurrentDictionary<string, DownloadItem> activeDownloads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<TcpClient>> pendingReverseConnections = new();
    private readonly CancellationTokenSource shutdownCts = new();
    private readonly ConcurrentDictionary<string, List<string>> swarmPeers = new(); // TTH -> List of Peer IDs
    public Func<IEnumerable<PeerInfo>>? NodePeersProvider { get; set; }

    private const long SegmentSize = 20 * 1024 * 1024; // 20MB
    private readonly System.Diagnostics.Stopwatch progressStopwatch = System.Diagnostics.Stopwatch.StartNew();
    private long lastProgressTicks = 0;

    private int GetSegmentCount(long fileSize) => (int)Math.Ceiling((double)fileSize / SegmentSize);
    
    private byte[] GetBitfieldBytes(string hex)
    {
        if (string.IsNullOrEmpty(hex)) return Array.Empty<byte>();
        try { return Enumerable.Range(0, hex.Length / 2).Select(x => Convert.ToByte(hex.Substring(x * 2, 2), 16)).ToArray(); }
        catch { return Array.Empty<byte>(); }
    }

    private string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "");

    private bool IsSegmentFinished(byte[] bitfield, int index)
    {
        int byteIdx = index / 8;
        if (byteIdx >= bitfield.Length) return false;
        int bitIdx = index % 8;
        return (bitfield[byteIdx] & (1 << bitIdx)) != 0;
    }

    private void MarkSegmentFinished(byte[] bitfield, int index)
    {
        int byteIdx = index / 8;
        if (byteIdx >= bitfield.Length) return;
        int bitIdx = index % 8;
        bitfield[byteIdx] |= (byte)(1 << bitIdx);
    }

    private async Task<Stream> SecureStreamAsync(TcpClient client, Stream baseStream, string expectedThumbprint, CancellationToken token)
    {
        var remoteEp = client.Client.RemoteEndPoint as System.Net.IPEndPoint;
        if (remoteEp != null && !CryptoManager.IsPrivateOrLocalIp(remoteEp.Address))
        {
            logger.LogInfo($"[DownloadManager] Public connection to {remoteEp.Address}. Enforcing E2EE (TLS)...");
            var sslStream = new SslStream(baseStream, false, (sender, cert, chain, err) => {
                if (cert == null) return false;
                bool match = string.Equals(cert.GetCertHashString(), expectedThumbprint, StringComparison.OrdinalIgnoreCase);
                if (!match) logger.LogWarning($"[DownloadManager] E2EE Certificate mismatch! Expected {expectedThumbprint}, Got {cert.GetCertHashString()}");
                return match;
            });
            await sslStream.AuthenticateAsClientAsync(new SslClientAuthenticationOptions {
                TargetHost = "TransfarrP2PPeer",
                EnabledSslProtocols = System.Security.Authentication.SslProtocols.Tls12 | System.Security.Authentication.SslProtocols.Tls13,
                CertificateRevocationCheckMode = X509RevocationMode.NoCheck
            }, token);
            return sslStream;
        }
        return baseStream;
    }
    
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
                stream = await SecureStreamAsync(client, client.GetStream(), targetPeer.CertificateThumbprint, shutdownCts.Token);
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
                stream = await SecureStreamAsync(client, finalStream, targetPeer.CertificateThumbprint, shutdownCts.Token);
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
            // Only start if we aren't already downloading this file
            if (!activeDownloads.ContainsKey(item.Id))
            {
                item.Status = "Downloading";
                db.UpsertDownloadItem(item);
                activeDownloads.TryAdd(item.Id, item);
                OnQueueChanged?.Invoke();
                
                _ = DownloadFileSwarmAsync(item);
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

    private async Task DownloadFileSwarmAsync(DownloadItem item)
    {
        string targetDir = string.IsNullOrEmpty(item.RelativePath) 
            ? DownloadsFolder 
            : Path.Combine(DownloadsFolder, item.RelativePath);
            
        Directory.CreateDirectory(targetDir);
        string filePath = Path.Combine(targetDir, item.FileName);

        try
        {
            // Initialize Bitfield if empty
            int segmentCount = GetSegmentCount(item.FileSize);
            int bitfieldByteCount = (int)Math.Ceiling(segmentCount / 8.0);
            byte[] bitfield = GetBitfieldBytes(item.Bitfield);
            if (bitfield.Length != bitfieldByteCount)
            {
                bitfield = new byte[bitfieldByteCount];
                item.Bitfield = ToHex(bitfield);
                item.BytesDownloaded = 0;
            }

            logger.LogInfo($"[Swarm] Starting: {item.FileName} ({segmentCount} segments) from Swarm");
            
            // Open file for shared writing
            using var fs = new FileStream(filePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.ReadWrite);
            if (fs.Length < item.FileSize) fs.SetLength(item.FileSize); // Pre-allocate

            var activeWorkers = new ConcurrentDictionary<string, bool>(); // PeerId -> Working
            var semaphore = new SemaphoreSlim(5); // Max 5 parallel connections
            var segmentLock = new object();
            
            // Main Swarm Control Loop: Parallel Worker Management
            int maxParallel = 5;
            var segments = Enumerable.Range(0, segmentCount).ToList();
            var activeSegments = new ConcurrentDictionary<int, bool>();
            var uploaderPool = new ConcurrentBag<PeerInfo>();
            if (item.TargetPeer != null) uploaderPool.Add(item.TargetPeer);
            
            // Add discovered peers from DB
            foreach (var peerId in item.DiscoveredPeers)
            {
                var p = NodePeersProvider?.Invoke().FirstOrDefault(x => x.PeerId == peerId);
                if (p != null) uploaderPool.Add(p);
            }

            var workerTasks = new List<Task>();
            for (int w = 0; w < maxParallel; w++)
            {
                workerTasks.Add(Task.Run(async () => {
                    while (!shutdownCts.Token.IsCancellationRequested && items.ContainsKey(item.Id))
                    {
                        // 1. Pick a segment
                        int segIdx = -1;
                        lock (segmentLock)
                        {
                            for (int i = 0; i < segmentCount; i++)
                            {
                                if (!IsSegmentFinished(bitfield, i) && !activeSegments.ContainsKey(i))
                                {
                                    segIdx = i;
                                    activeSegments[i] = true;
                                    break;
                                }
                            }
                        }
                        if (segIdx == -1) break; // Finished!

                        // 2. Pick an uploader
                        var peer = uploaderPool.FirstOrDefault(p => !activeWorkers.ContainsKey(p.PeerId));
                        if (peer == null) 
                        {
                            activeSegments.TryRemove(segIdx, out _);
                            await Task.Delay(1000);
                            continue;
                        }

                        activeWorkers[peer.PeerId] = true;
                        try 
                        {
                            long offset = segIdx * SegmentSize;
                            long length = Math.Min(SegmentSize, item.FileSize - offset);
                            
                            bool success = await DownloadSegmentAsync(item, peer, offset, length, fs);
                            if (success)
                            {
                                lock (segmentLock) MarkSegmentFinished(bitfield, segIdx);
                                UpdateProgress(item, bitfield, segmentCount);
                            }
                            else
                            {
                                // Uploader failed, remove it from pool for this session?
                                // For now just cool down
                                activeSegments.TryRemove(segIdx, out _);
                                await Task.Delay(3000);
                            }
                        }
                        finally { activeWorkers.TryRemove(peer.PeerId, out _); }
                    }
                }));
            }

            await Task.WhenAll(workerTasks);
            
            if (item.BytesDownloaded >= item.FileSize)
            {
                CompleteDownload(item);
            }
        }
        catch(Exception ex)
        {
            if (items.TryGetValue(item.Id, out var currentItem))
            {
                currentItem.Status = "Error";
                logger.LogError($"[Swarm] Failed {item.FileName}: {ex.Message}");
                CompleteDownload(currentItem);
            }
        }
    }

    private void UpdateProgress(DownloadItem item, byte[] bitfield, int segmentCount)
    {
        long total = 0;
        for (int s = 0; s < segmentCount; s++)
        {
            if (IsSegmentFinished(bitfield, s))
                total += Math.Min(SegmentSize, item.FileSize - (s * SegmentSize));
        }
        item.BytesDownloaded = total;
        item.Bitfield = ToHex(bitfield);
        
        long currentTicks = progressStopwatch.ElapsedMilliseconds;
        if (currentTicks - lastProgressTicks > 500)
        {
            lastProgressTicks = currentTicks;
            db.UpsertDownloadItem(item);
            OnQueueChanged?.Invoke();
        }
    }

    public void RegisterDiscoveredPeer(string tth, PeerInfo peer)
    {
        var item = items.Values.FirstOrDefault(i => i.Tth == tth);
        if (item != null)
        {
            if (!item.DiscoveredPeers.Contains(peer.PeerId))
            {
                item.DiscoveredPeers.Add(peer.PeerId);
                db.UpsertDownloadItem(item);
                logger.LogInfo($"[Swarm] New uploader for {item.FileName}: {peer.Name}");
            }
        }
    }

    public void MatchQueueWithPeer(PeerInfo peer, string fileListJson)
    {
        try
        {
            var root = System.Text.Json.JsonSerializer.Deserialize<FileListItem>(fileListJson);
            if (root == null) return;

            var peerFiles = Flatten(root);
            var queue = items.Values.Where(i => i.Status != "Finished").ToList();
            int matches = 0;

            foreach (var qItem in queue)
            {
                if (peerFiles.Any(f => f.Tth == qItem.Tth))
                {
                    if (!qItem.DiscoveredPeers.Contains(peer.PeerId))
                    {
                        qItem.DiscoveredPeers.Add(peer.PeerId);
                        db.UpsertDownloadItem(qItem);
                        matches++;
                    }
                }
            }

            if (matches > 0)
            {
                logger.LogInfo($"[Swarm] Matched {matches} items from queue with peer {peer.Name}");
                OnQueueChanged?.Invoke();
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"[Swarm] Failed to match queue with peer {peer.Name}: {ex.Message}");
        }
    }

    private List<FileListItem> Flatten(FileListItem item)
    {
        var list = new List<FileListItem>();
        if (!item.IsDirectory) list.Add(item);
        if (item.Children != null)
        {
            foreach (var child in item.Children)
            {
                list.AddRange(Flatten(child));
            }
        }
        return list;
    }

    private async Task<bool> DownloadSegmentAsync(DownloadItem item, PeerInfo peer, long offset, long length, FileStream fs)
    {
        try
        {
            TcpClient client;
            Stream stream;

            if (peer.IsPassive)
            {
                var tcs = new TaskCompletionSource<TcpClient>();
                var reqId = $"SW_{item.Tth}_{offset}";
                pendingReverseConnections[reqId] = tcs;

                if (RequestConnectBackAction != null) await RequestConnectBackAction(peer.PeerId, reqId);
                var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000, shutdownCts.Token));
                if (completedTask != tcs.Task) return false;
                client = await tcs.Task;
                stream = await SecureStreamAsync(client, client.GetStream(), peer.CertificateThumbprint, shutdownCts.Token);
            }
            else
            {
                client = new TcpClient();
                var connectTask = client.ConnectAsync(peer.DirectIp, peer.TransferPort, shutdownCts.Token).AsTask();
                if (await Task.WhenAny(connectTask, Task.Delay(5000, shutdownCts.Token)) != connectTask) return false;
                await connectTask;
                stream = await SecureStreamAsync(client, client.GetStream(), peer.CertificateThumbprint, shutdownCts.Token);
            }

            using (client)
            using (stream)
            {
                var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true);
                await writer.WriteLineAsync($"REQ_FILE|{item.Tth}|{offset}|{length}");
                await writer.FlushAsync();

                byte[] buffer = new byte[81920];
                long totalRead = 0;
                
                while (totalRead < length)
                {
                    int read = await stream.ReadAsync(buffer, 0, (int)Math.Min(buffer.Length, length - totalRead), shutdownCts.Token);
                    if (read == 0) break;

                    await bandwidthController.ConsumeDownloadAsync(read, shutdownCts.Token);
                    
                    lock (fs)
                    {
                        fs.Seek(offset + totalRead, SeekOrigin.Begin);
                        fs.Write(buffer, 0, read);
                    }
                    totalRead += read;
                }
                
                return totalRead == length;
            }
        }
        catch { return false; }
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
