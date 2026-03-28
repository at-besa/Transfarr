using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Transfarr.Node.Core;
using Transfarr.Shared.Models;
using Xunit;

using Microsoft.Extensions.Options;
using Transfarr.Node.Options;

namespace Transfarr.Tests;

public class DownloadManagerTests : IDisposable
{
    private readonly TcpListener dummyListener;
    private readonly int testPort = 54911;

    public DownloadManagerTests()
    {
        // Start a dummy listener so DownloadManager gets a connected socket
        dummyListener = new TcpListener(IPAddress.Loopback, testPort);
        dummyListener.Start();
        
        Task.Run(async () => {
            while (true) {
                try {
                    var client = await dummyListener.AcceptTcpClientAsync();
                } catch { break; }
            }
        });
    }

    private string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "TransfarrTest_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void DownloadManager_Should_Enforce_Strict_Concurrency_Limit()
    {
        var tempDir = CreateTempDirectory();
        var tempDb = Path.Combine(tempDir, "test.db");
        var downloadsDir = Path.Combine(tempDir, "Downloads");

        var options = Options.Create(new NodeOptions { 
            Storage = new StorageOptions { DatabasePath = tempDb, DefaultDownloadsFolder = downloadsDir }
        });
        var logger = new SystemLogger();
        var db = new ShareDatabase(options);
        db.InitializeDatabase();
        
        var dm = new DownloadManager(db, logger, options);
        dm.Initialize();
        dm.SetDownloadsFolder(downloadsDir);
        var peer = new PeerInfo("conn1", "peer1", "TestUser", 1000, "127.0.0.1", testPort);

        dm.AddToQueue(peer, "file1.txt", 1000, "tth1");
        dm.AddToQueue(peer, "file2.txt", 1000, "tth2");
        dm.AddToQueue(peer, "file3.txt", 1000, "tth3");

        // Wait a brief moment for the background processing loop to pick up the items
        Thread.Sleep(500);

        var queued = dm.AllItems;
        var downloading = queued.Where(q => q.Status == "Downloading").ToList();
        var waiting = queued.Where(q => q.Status == "Queued").ToList();
        
        // Assert exactly 1 file is actively downloading from this peer
        Assert.Single(downloading);
        Assert.Equal("file1.txt", downloading[0].FileName);
        
        // Assert the remaining 2 are strictly blocked entirely in the "Queued" state
        Assert.Equal(2, waiting.Count);
    }

    public void Dispose()
    {
        dummyListener.Stop();
        var dbPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "shares.db"); // Default or whatever was used
        // Clean up DB files if possible, but at least stop the listener
    }
}
