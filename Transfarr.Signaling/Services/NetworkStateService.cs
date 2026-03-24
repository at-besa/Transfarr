using System.Collections.Concurrent;
using Transfarr.Shared.Models;

namespace Transfarr.Signaling.Services;

public class NetworkStateService
{
    private readonly ConcurrentDictionary<string, PeerInfo> _peers = new();
    
    public DateTime StartupTime { get; } = DateTime.UtcNow;
    public int PeakUserCount { get; private set; }
    public long PeakTotalSharedBytes { get; private set; }
    
    public long TotalBytesTransferred { get; private set; }
    public long BytesInLastSecond { get; private set; }
    public long CurrentTransferRateBps { get; private set; }
    
    public int LoginAttempts { get; private set; }
    public int TotalErrors { get; private set; }

    private readonly System.Timers.Timer _rateTimer;

    public NetworkStateService()
    {
        _rateTimer = new System.Timers.Timer(1000);
        _rateTimer.Elapsed += (s, e) => {
            CurrentTransferRateBps = BytesInLastSecond;
            BytesInLastSecond = 0;
        };
        _rateTimer.Start();
    }

    public IReadOnlyCollection<PeerInfo> ActivePeers => _peers.Values.ToList().AsReadOnly();

    public void AddOrUpdatePeer(string connectionId, PeerInfo peer)
    {
        _peers[connectionId] = peer;
        UpdatePeaks();
    }

    public void RemovePeer(string connectionId)
    {
        _peers.TryRemove(connectionId, out _);
    }

    private void UpdatePeaks()
    {
        if (_peers.Count > PeakUserCount) PeakUserCount = _peers.Count;
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

    public long GetTotalSharedBytes() => _peers.Values.Sum(p => p.SharedBytes);
    public int GetActiveNodeCount() => _peers.Count;
}
