namespace Transfarr.Node.Options;

public class NetworkOptions
{
    public string StunServer { get; set; } = "stun.l.google.com:19302";
    public int TransferPort { get; set; } = 5133;
    public bool EnableUPnP { get; set; } = true;
}
