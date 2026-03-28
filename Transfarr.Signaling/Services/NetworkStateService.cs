using System.Collections.Concurrent;
using Transfarr.Shared.Models;

namespace Transfarr.Signaling.Services;

public class NetworkStateService : IDisposable
{
    private readonly ConcurrentDictionary<string, PeerInfo> peers = new();
    
    public DateTime StartupTime { get; } = DateTime.UtcNow;
    public int PeakUserCount { get; private set; }
    public long PeakTotalSharedBytes { get; private set; }
    
    public long TotalBytesTransferred { get; private set; }
    public long BytesInLastSecond { get; private set; }
    public long CurrentTransferRateBps { get; private set; }
    
    public int LoginAttempts { get; private set; }
    public int TotalErrors { get; private set; }

    private readonly System.Timers.Timer rateTimer;

    public NetworkStateService()
    {
        rateTimer = new System.Timers.Timer(1000);
        rateTimer.Elapsed += (s, e) => {
            CurrentTransferRateBps = BytesInLastSecond;
            BytesInLastSecond = 0;
        };
        rateTimer.Start();
    }

    public IReadOnlyCollection<PeerInfo> ActivePeers => peers.Values.ToList().AsReadOnly();

    public void AddOrUpdatePeer(string connectionId, PeerInfo peer)
    {
        peers[connectionId] = peer;
        UpdatePeaks();
    }

    public void RemovePeer(string connectionId)
    {
        peers.TryRemove(connectionId, out _);
    }

    private void UpdatePeaks()
    {
        if (peers.Count > PeakUserCount) PeakUserCount = peers.Count;
        long total = GetTotalSharedBytes();
        if (total > PeakTotalSharedBytes) PeakTotalSharedBytes = total;
    }

    public void RecordTransfer(long bytes)
    {
        TotalBytesTransferred += bytes;
        BytesInLastSecond += bytes;
    }

    public void RecordLoginAttempt() => LoginAttempts++;
    public void RecordError() => TotalErrors++;

    public long GetTotalSharedBytes() => peers.Values.Sum(p => p.SharedBytes);
    public int GetActiveNodeCount() => peers.Count;

    public void Dispose()
    {
        rateTimer.Stop();
        rateTimer.Dispose();
    }
}
