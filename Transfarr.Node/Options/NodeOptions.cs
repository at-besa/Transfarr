using System;

namespace Transfarr.Node.Options;

public class NodeOptions
{
    public const string SectionName = "Transfarr";

    public int NodePort { get; set; } = 5150;
    public string DefaultNodeName { get; set; } = "Transfarr-Node";
    public string[] DefaultHubUrls { get; set; } = Array.Empty<string>();
    
    public StorageOptions Storage { get; set; } = new();
    public NetworkOptions Network { get; set; } = new();
}

public class StorageOptions
{
    public string DatabasePath { get; set; } = "node.db";
    public string DefaultDownloadsFolder { get; set; } = "Downloads";
    public int ShareRefreshIntervalMinutes { get; set; } = 5;
}

public class NetworkOptions
{
    public string StunServer { get; set; } = "stun.l.google.com:19302";
    public int TransferPort { get; set; } = 5133;
    public bool EnableUPnP { get; set; } = true;
}
