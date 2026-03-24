using System;

namespace Transfarr.Shared.Models;

public class UploadItem
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string RemoteIp { get; set; } = "";
    public string FileName { get; set; } = "";
    public string Tth { get; set; } = "";
    public long TotalSize { get; set; }
    public long BytesTransferred { get; set; }
    public double SpeedMBps { get; set; }
    public DateTime StartTime { get; set; } = DateTime.Now;
}
