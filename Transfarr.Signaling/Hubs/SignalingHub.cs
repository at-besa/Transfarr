using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Transfarr.Signaling.Data;
using Transfarr.Signaling.Services;
using Transfarr.Shared.Models;

namespace Transfarr.Signaling.Hubs;

[Authorize]
public class SignalingHub(NetworkStateService networkState, UserDatabase db) : Hub
{

    public override async Task OnDisconnectedAsync(System.Exception? exception)
    {
        var peer = networkState.ActivePeers.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
        if (peer != null)
        {
            networkState.RemovePeer(Context.ConnectionId);
            await Clients.Others.SendAsync("PeerLeft", peer.PeerId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Join(string peerId, string name, long sharedBytes = 0)
    {
        var peer = new PeerInfo(Context.ConnectionId, peerId, name, sharedBytes);
        networkState.AddOrUpdatePeer(Context.ConnectionId, peer);
        
        await Clients.Caller.SendAsync("PeerList", networkState.ActivePeers);
        await Clients.Others.SendAsync("PeerJoined", peer);
    }

    public async Task JoinAsNode(PeerInfo peer)
    {
        string username = Context.User?.Identity?.Name ?? peer.Name;
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (remoteIp == "::1") remoteIp = "127.0.0.1";

        // Use provided DirectIp if present, otherwise fall back to auto-detected remote IP
        string resolvedIp = string.IsNullOrWhiteSpace(peer.DirectIp) ? remoteIp : peer.DirectIp;

        var actualPeer = peer with { 
            ConnectionId = Context.ConnectionId,
            DirectIp = resolvedIp,
            Name = username 
        };
        networkState.AddOrUpdatePeer(Context.ConnectionId, actualPeer);
        
        await Clients.Caller.SendAsync("PeerList", networkState.ActivePeers);
        await Clients.Others.SendAsync("PeerJoined", actualPeer);
    }

    public async Task UpdateNodeParams(PeerInfo peer)
    {
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (remoteIp == "::1") remoteIp = "127.0.0.1";

        string resolvedIp = string.IsNullOrWhiteSpace(peer.DirectIp) ? remoteIp : peer.DirectIp;

        var actualPeer = peer with { 
            ConnectionId = Context.ConnectionId,
            DirectIp = resolvedIp 
        };
        networkState.AddOrUpdatePeer(Context.ConnectionId, actualPeer);
        await Clients.Others.SendAsync("PeerUpdated", actualPeer);
    }

    public async Task SendSignal(SignalMessage message)
    {
        var target = networkState.ActivePeers.FirstOrDefault(p => p.PeerId == message.TargetPeerId);
        if (target != null)
        {
            await Clients.Client(target.ConnectionId).SendAsync("ReceiveSignal", message);
        }
    }

    public async Task Search(SearchRequest request)
    {
        // Broadcast search to all peers (including self if desired by protocol)
        await Clients.All.SendAsync("SearchRequest", request, Context.ConnectionId);
    }

    public async Task SubmitSearchResult(string requesterConnectionId, SearchResult result)
    {
        await Clients.Client(requesterConnectionId).SendAsync("ReceiveSearchResult", result);
    }

    public async Task SendPrivateMessage(string targetPeerId, string senderPeerId, string content)
    {
        await Clients.All.SendAsync("ReceivePrivateMessage", targetPeerId, senderPeerId, content);
    }

    public async Task Chat(string sender, string message)
    {
        await Clients.All.SendAsync("ReceiveChat", sender, message);
    }

    public async Task ReportUploadComplete(long bytes)
    {
        string? username = Context.User?.Identity?.Name;
        if (string.IsNullOrEmpty(username)) return;

        networkState.RecordTransfer(bytes);

        // Reward 1 point per 10GB uploaded (min 1 point for any success)
        int points = (int)Math.Max(1, bytes / (1024L * 1024 * 1024 * 10));
        db.UpdateReputation(username, points);
    }

    public async Task RequestConnectBack(string targetPeerId, string fileHash)
    {
        var targetPeer = networkState.ActivePeers.FirstOrDefault(p => p.PeerId == targetPeerId);
        if (targetPeer == null) return;

        var requesterPeer = networkState.ActivePeers.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
        if (requesterPeer == null) return;

        // Signal to the target (passive uploader) to connect to the requester (active downloader)
        await Clients.Client(targetPeer.ConnectionId).SendAsync("OnConnectBackRequested", requesterPeer.DirectIp, requesterPeer.TransferPort, fileHash);
    }

    public async Task<bool> TestConnectivity(string ip, int port)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();
            var connectTask = client.ConnectAsync(ip, port, Context.ConnectionAborted).AsTask();
            if (await Task.WhenAny(connectTask, Task.Delay(2000, Context.ConnectionAborted)) == connectTask)
            {
                await connectTask;
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    public string GetMyPublicIp()
    {
        var remoteIp = Context.GetHttpContext()?.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
        if (remoteIp == "::1") remoteIp = "127.0.0.1";
        return remoteIp;
    }

    public async Task InitiateConnectionNegotiation(string targetPeerId, string requestId)
    {
        var targetPeer = networkState.ActivePeers.FirstOrDefault(p => p.PeerId == targetPeerId);
        if (targetPeer == null) return;

        var requesterPeer = networkState.ActivePeers.FirstOrDefault(p => p.ConnectionId == Context.ConnectionId);
        if (requesterPeer == null) return;

        await Clients.Client(targetPeer.ConnectionId).SendAsync("OnNegotiationRequested", requesterPeer.PeerId, requestId);
    }

    public async Task SubmitIceCandidates(string requesterPeerId, string requestId, IEnumerable<string> candidates)
    {
        var requester = networkState.ActivePeers.FirstOrDefault(p => p.PeerId == requesterPeerId);
        if (requester == null) return;

        await Clients.Client(requester.ConnectionId).SendAsync("OnIceCandidatesReceived", requestId, candidates);
    }
}
