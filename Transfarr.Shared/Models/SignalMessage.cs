namespace Transfarr.Shared.Models;

public record SignalMessage(string TargetPeerId, string SenderPeerId, string Data);
