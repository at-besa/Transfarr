using Microsoft.AspNetCore.SignalR;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Transfarr.Shared.Models;

namespace Transfarr.Signaling.Hubs;

public class SignalingHub : Hub
{
    private static readonly ConcurrentDictionary<string, PeerInfo> Peers = new();

    public override async Task OnDisconnectedAsync(System.Exception? exception)
    {
        if (Peers.TryRemove(Context.ConnectionId, out var peer))
        {
            await Clients.Others.SendAsync("PeerLeft", peer.PeerId);
        }
        await base.OnDisconnectedAsync(exception);
    }

    public async Task Join(string peerId, string name, long sharedBytes = 0)
    {
        var peer = new PeerInfo(Context.ConnectionId, peerId, name, sharedBytes);
        Peers[Context.ConnectionId] = peer;
        
        // Tell the new peer about everyone
        await Clients.Caller.SendAsync("PeerList", Peers.Values);
        
        // Tell everyone else about the new peer
        await Clients.Others.SendAsync("PeerJoined", peer);
    }

    public async Task JoinAsNode(PeerInfo peer)
    {
        // Force the connection ID to match the real context
        var actualPeer = peer with { ConnectionId = Context.ConnectionId };
        Peers[Context.ConnectionId] = actualPeer;
        
        await Clients.Caller.SendAsync("PeerList", Peers.Values);
        await Clients.Others.SendAsync("PeerJoined", actualPeer);
    }

    public async Task UpdateNodeParams(PeerInfo peer)
    {
        var actualPeer = peer with { ConnectionId = Context.ConnectionId };
        Peers[Context.ConnectionId] = actualPeer;
        await Clients.Others.SendAsync("PeerUpdated", actualPeer);
    }

    public async Task SendSignal(SignalMessage message)
    {
        var target = Peers.Values.FirstOrDefault(p => p.PeerId == message.TargetPeerId);
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
}
