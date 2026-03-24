using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Transfarr.Node.Core;
using Transfarr.Shared.Models;
using Xunit;

namespace Transfarr.Tests;

public class DownloadManagerTests : IDisposable
{
    private readonly TcpListener _dummyListener;
    private readonly int _testPort = 54911;

    public DownloadManagerTests()
    {
        // Start a dummy listener so DownloadManager gets a connected socket instead of "Connection Refused",
        // ensuring it enters and stays in the "Downloading" state indefinitely for our concurrency assertions.
        _dummyListener = new TcpListener(IPAddress.Loopback, _testPort);
        _dummyListener.Start();
        
        Task.Run(async () => {
            while (true) {
                try {
                    var client = await _dummyListener.AcceptTcpClientAsync();
                    // We just accept and hold the socket open; simulating a very slow peer.
                } catch { break; }
            }
        });
    }

    [Fact]
    public void DownloadManager_Should_Enforce_Strict_Concurrency_Limit()
    {
        var dm = new DownloadManager();
        var peer = new PeerInfo("conn1", "peer1", "TestUser", 1000, "127.0.0.1", _testPort);

        dm.AddToQueue(peer, "file1.txt", 1000, "tth1");
        dm.AddToQueue(peer, "file2.txt", 1000, "tth2");
        dm.AddToQueue(peer, "file3.txt", 1000, "tth3");

        // Wait a brief moment for the background processing loop to pick up the items
        Thread.Sleep(500);

        var queued = dm.Queue.ToList();
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
        _dummyListener.Stop();
    }
}
