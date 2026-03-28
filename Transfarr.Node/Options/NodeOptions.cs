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
