namespace Transfarr.Shared.Models;

public record PeerInfo(string ConnectionId, string PeerId, string Name, long SharedBytes = 0, string DirectIp = "", int TransferPort = 0);
