using System;

namespace Transfarr.Shared.Models;

public class DownloadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string TargetPeerId { get; set; } = "";
    public PeerInfo TargetPeer { get; set; } = default!;
    public string FileName { get; set; } = "";
    public long FileSize { get; set; }
    public string Tth { get; set; } = "";
    public long BytesDownloaded { get; set; }
    public string Bitfield { get; set; } = ""; // Serialized bitset for 20MB segments
    public string Status { get; set; } = "Queued";
    public string RelativePath { get; set; } = "";
    public List<string> DiscoveredPeers { get; set; } = new();

    public double Progress => FileSize == 0 ? 0 : (double)BytesDownloaded / FileSize;
}
