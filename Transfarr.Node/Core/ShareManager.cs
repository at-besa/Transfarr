using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Transfarr.Shared.Hashing;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Core;

// Represents a queued file for the Parallel CPU worker
public class HashJob
{
    public string PhysicalPath { get; set; } = "";
    public FileListItem Node { get; set; } = default!;
}

public class ShareManager(SystemLogger logger, ShareDatabase db)
{
    private FileList localFileList = new();
    private readonly Dictionary<string, List<string>> filePaths = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, string> sharedDirectories = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (long Size, DateTime LastWriteTime, string Tth)> hashCache = new(StringComparer.OrdinalIgnoreCase);
    
    private System.Timers.Timer? refreshTimer;
    private CancellationTokenSource? hashCts;
    private readonly object cacheLock = new();

    public long TotalSharedBytes { get; private set; }
    
    public HashProgressState CurrentProgress { get; } = new();
    public event Action<HashProgressState>? OnHashProgress;
    public event Action? OnFilelistUpdated;
    public event Action<long>? OnShareSizeChanged; // Added for real-time sync

    // Initializer replacement for constructor logic
    public void Initialize()
    {
        db.InitializeDatabase();
        LoadCache();
        SetupRefreshTimer();
    }

    private void SetupRefreshTimer()
    {
        refreshTimer?.Dispose();
        refreshTimer = new System.Timers.Timer(TimeSpan.FromMinutes(5).TotalMilliseconds);
        refreshTimer.Elapsed += (s, e) => {
            if (!CurrentProgress.IsHashing)
            {
                logger.LogInfo("Triggering periodic background share refresh...");
                StartRebuild();
            }
        };
        refreshTimer.AutoReset = true;
        refreshTimer.Start();
    }

    private void LoadCache()
    {
        try
        {
            sharedDirectories = db.GetDirectories();
            hashCache = db.GetHashCache();
            
            // Initialize total size from cache immediately
            TotalSharedBytes = hashCache.Values.Sum(v => v.Size);
            
            logger.LogInfo($"Successfully loaded {sharedDirectories.Count} shares & {hashCache.Count} hashes from SQLite. Initial share size: {TotalSharedBytes / 1024.0 / 1024.0:F2} MB");
        }
        catch (Exception ex)
        {
            logger.LogError($"Failed to load local SQlite database: {ex.Message}");
        }

        if (sharedDirectories.Any())
        {
            StartRebuild();
        }
    }

    public FileList GetLocalFileList() => localFileList;
    public Dictionary<string, string> GetSharedDirectories() => sharedDirectories;

    public Dictionary<string, long> GetShareSizes()
    {
        return localFileList.Items.ToDictionary(i => i.Name, i => i.Size);
    }

    public string[] GetOSDirectories(string path)
    {
        try
        {
            if (string.IsNullOrEmpty(path))
                return DriveInfo.GetDrives().Where(d => d.IsReady).Select(d => d.RootDirectory.FullName).ToArray();
            return Directory.GetDirectories(path);
        }
        catch { return Array.Empty<string>(); }
    }

    public List<string> GetLocalPathsByTth(string tth)
    {
        lock (filePaths)
        {
            return filePaths.TryGetValue(tth, out var paths) ? paths : new List<string>();
        }
    }

    public bool HasFileByTth(string tth)
    {
        lock (filePaths)
        {
            return filePaths.ContainsKey(tth);
        }
    }

    public string GetLocalFileListJson()
    {
        return JsonSerializer.Serialize(localFileList);
    }

    public List<FileMetadata> SearchLocal(string query)
    {
        var results = new List<FileMetadata>();
        foreach (var item in localFileList.Items)
        {
            SearchRecursive(item, item.Name, query, results);
        }
        return results;
    }

    private void SearchRecursive(FileListItem item, string currentPath, string query, List<FileMetadata> results)
    {
        if (item.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
        {
            results.Add(new FileMetadata(item.Name, item.Size, item.Tth, item.IsDirectory, currentPath));
        }
        
        if (item.Children != null)
        {
            foreach (var child in item.Children)
            {
                SearchRecursive(child, currentPath + "/" + child.Name, query, results);
            }
        }
    }

    public FileListItem? GetFileListItemByPath(string virtualPath)
    {
        var parts = virtualPath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        var current = localFileList.Items.FirstOrDefault(i => i.Name.Equals(parts[0], StringComparison.OrdinalIgnoreCase));
        if (current == null) return null;

        for (int i = 1; i < parts.Length; i++)
        {
            current = current.Children?.FirstOrDefault(c => c.Name.Equals(parts[i], StringComparison.OrdinalIgnoreCase));
            if (current == null) return null;
        }

        return current;
    }

    public void AddSharedDirectory(string virtualName, string directoryPath)
    {
        if (!Directory.Exists(directoryPath)) return;
        sharedDirectories[virtualName] = directoryPath;
        logger.LogInfo($"Added share '{virtualName}' mapping to {directoryPath}");
        db.SaveDirectories(sharedDirectories);
        StartRebuild();
    }

    public void RemoveSharedDirectory(string virtualName)
    {
        if (sharedDirectories.Remove(virtualName))
        {
            logger.LogInfo($"Removed share '{virtualName}'");
            db.SaveDirectories(sharedDirectories);
            StartRebuild();
        }
    }

    public void StartRebuild()
    {
        hashCts?.Cancel();
        hashCts?.Dispose();
        hashCts = new CancellationTokenSource();
        _ = RebuildFileListAsync(hashCts.Token);
    }

    private async Task RebuildFileListAsync(CancellationToken token)
    {
        CurrentProgress.IsHashing = true;
        CurrentProgress.HashedBytes = 0;
        CurrentProgress.TotalBytes = 0;
        CurrentProgress.SpeedMBps = 0;
        OnHashProgress?.Invoke(CurrentProgress);

        long incrementalSharedBytes = 0;
        DateTime lastUiUpdate = DateTime.MinValue;

        try
        {
            var newFileList = new FileList();
            var jobs = new List<HashJob>();

            // Pass 1: Build structural FileList tree and extract HashJobs
            foreach (var kvp in sharedDirectories)
            {
                token.ThrowIfCancellationRequested();
                var rootItem = new FileListItem { Name = kvp.Key, IsDirectory = true };
                ExtractDirectoryTree(kvp.Value, rootItem, jobs, token);
                newFileList.Items.Add(rootItem);
            }
            
            // UI telemetry update for total bytes max bar
            OnHashProgress?.Invoke(CurrentProgress);

            // Pass 2: Heavy multi-threaded cryptography
            long hashedBytesAtomic = 0;
            long bytesSinceLastTickAtomic = 0;
            int newlyHashedCountAtomic = 0;
            var newFilePaths = new ConcurrentDictionary<string, ConcurrentBag<string>>();
            var validPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            using var speedTimer = new System.Timers.Timer(500);
            speedTimer.Elapsed += (s, e) =>
            {
                long delta = Interlocked.Exchange(ref bytesSinceLastTickAtomic, 0);
                CurrentProgress.SpeedMBps = (delta / 1024.0 / 1024.0) / 0.5;
                OnHashProgress?.Invoke(CurrentProgress);
            };
            speedTimer.Start();

            var parallelOptions = new ParallelOptions 
            { 
                MaxDegreeOfParallelism = Environment.ProcessorCount,
                CancellationToken = token
            };

            await Parallel.ForEachAsync(jobs, parallelOptions, (job, ct) =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();
                    var fileInfo = new FileInfo(job.PhysicalPath);
                    string tth = string.Empty;
                    bool cacheHitStatus = false;

                    lock (cacheLock)
                    {
                        if (hashCache.TryGetValue(job.PhysicalPath, out var cached))
                        {
                            if (cached.Size == fileInfo.Length && cached.LastWriteTime.Ticks == fileInfo.LastWriteTimeUtc.Ticks)
                            {
                                tth = cached.Tth;
                                cacheHitStatus = true;
                            }
                        }
                    }

                    if (!cacheHitStatus)
                    {
                        using var baseStream = File.OpenRead(job.PhysicalPath);
                        using var progressStream = new ProgressStream(baseStream, bytesRead => 
                        {
                            Interlocked.Add(ref bytesSinceLastTickAtomic, bytesRead);
                            Interlocked.Add(ref hashedBytesAtomic, bytesRead);
                        });
                        
                        tth = TigerTreeHash.ComputeTTH(progressStream);
                        
                        lock (cacheLock)
                        {
                            hashCache[job.PhysicalPath] = (fileInfo.Length, fileInfo.LastWriteTimeUtc, tth);
                        }
                        
                        db.UpsertHashCache(job.PhysicalPath, fileInfo.Length, fileInfo.LastWriteTimeUtc, tth);
                    }
                    else
                    {
                        Interlocked.Add(ref hashedBytesAtomic, job.Node.Size);
                    }

                    job.Node.Tth = tth;
                    
                    var bag = newFilePaths.GetOrAdd(tth, _ => new ConcurrentBag<string>());
                    bag.Add(job.PhysicalPath);

                    // Make file immediately available for download
                    lock (filePaths)
                    {
                        if (!filePaths.ContainsKey(tth)) filePaths[tth] = new List<string>();
                        if (!filePaths[tth].Contains(job.PhysicalPath)) filePaths[tth].Add(job.PhysicalPath);
                    }
                    
                    lock(validPaths) validPaths.Add(job.PhysicalPath);

                    CurrentProgress.HashedBytes = Interlocked.Read(ref hashedBytesAtomic);
                    
                    // Increment immediate share size availability
                    long currentHashed = Interlocked.Add(ref incrementalSharedBytes, job.Node.Size);
                    TotalSharedBytes = currentHashed;

                    // Throttle UI and peer updates during high-speed hashing
                    if ((DateTime.Now - lastUiUpdate).TotalMilliseconds > 1000)
                    {
                        lastUiUpdate = DateTime.Now;
                        OnShareSizeChanged?.Invoke(TotalSharedBytes);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    logger.LogWarning($"Hash Fault '{Path.GetFileName(job.PhysicalPath)}': {ex.Message}");
                }
                
                return ValueTask.CompletedTask;
            });

            speedTimer.Stop();

            lock (filePaths)
            {
                filePaths.Clear();
                foreach (var kvp in newFilePaths) 
                {
                    filePaths[kvp.Key] = kvp.Value.ToList();
                }
            }
            
            db.CleanupHashCache(validPaths);
            lock (cacheLock)
            {
                var keysToRemove = hashCache.Keys.Where(k => !validPaths.Contains(k)).ToList();
                foreach (var k in keysToRemove) hashCache.Remove(k);
            }
            
            localFileList = newFileList;
            TotalSharedBytes = incrementalSharedBytes;
            OnShareSizeChanged?.Invoke(TotalSharedBytes);

            CurrentProgress.IsHashing = false;
            CurrentProgress.HashedBytes = CurrentProgress.TotalBytes;
            CurrentProgress.SpeedMBps = 0;
            OnHashProgress?.Invoke(CurrentProgress);
            OnFilelistUpdated?.Invoke();

            logger.LogInfo($"Indexing completed: {jobs.Count} files in share ({newlyHashedCountAtomic} newly hashed, {jobs.Count - newlyHashedCountAtomic} from cache).");
        }
        catch (OperationCanceledException)
        {
            logger.LogWarning("Background indexing sequence aborted.");
        }
        catch (Exception ex)
        {
            logger.LogError($"Critical daemon indexing fault: {ex.Message}");
            CurrentProgress.IsHashing = false;
            OnHashProgress?.Invoke(CurrentProgress);
        }
    }

    private void ExtractDirectoryTree(string dirPath, FileListItem parent, List<HashJob> jobs, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        try
        {
            foreach (var file in Directory.GetFiles(dirPath))
            {
                token.ThrowIfCancellationRequested();
                try 
                {
                    var fileInfo = new FileInfo(file);
                    var item = new FileListItem
                    {
                        Name = Path.GetFileName(file),
                        IsDirectory = false,
                        Size = fileInfo.Length,
                        Tth = "" 
                    };

                    parent.Children.Add(item);
                    parent.Size += item.Size;
                    
                    CurrentProgress.TotalBytes += item.Size;
                    jobs.Add(new HashJob { PhysicalPath = file, Node = item });
                } 
                catch (Exception ex) 
                { 
                    logger.LogWarning($"Skipped unreadable file '{Path.GetFileName(file)}': {ex.Message}");
                }
            }

            foreach (var dir in Directory.GetDirectories(dirPath))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var item = new FileListItem
                    {
                        Name = Path.GetFileName(dir),
                        IsDirectory = true
                    };
                    ExtractDirectoryTree(dir, item, jobs, token);
                    parent.Children.Add(item);
                    parent.Size += item.Size;
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"Skipped unreadable subsystem directory '{Path.GetFileName(dir)}': {ex.Message}");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogWarning($"Access Denied extracting structure from '{dirPath}': {ex.Message}");
        }
    }
}
