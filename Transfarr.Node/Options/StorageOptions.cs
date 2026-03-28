namespace Transfarr.Node.Options;

public class StorageOptions
{
    public string DatabasePath { get; set; } = "node.db";
    public string DefaultDownloadsFolder { get; set; } = "Downloads";
    public int ShareRefreshIntervalMinutes { get; set; } = 5;
}
