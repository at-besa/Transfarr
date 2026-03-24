using System.Collections.Generic;

namespace Transfarr.Shared.Models;

public class HashProgressState
{
    public bool IsHashing { get; set; }
    public long TotalBytes { get; set; }
    public long HashedBytes { get; set; }
    public double SpeedMBps { get; set; }
    public double Progress => TotalBytes == 0 ? 0 : (double)HashedBytes / TotalBytes;
}
