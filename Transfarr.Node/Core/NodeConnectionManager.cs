using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Core;

public class NodeConnectionManager(ShareManager shareManager, TransferServer transferServer, ShareDatabase db) : IHostedService
{
    private HubConnection? hub;
    
    public string PeerId { get; } = Guid.NewGuid().ToString("N");
    public string NodeName { get; set; } = "DesktopNode_" + Random.Shared.Next(100, 999);
    
    public string GlobalHubUrl { get; private set; } = string.Empty;
    public bool IsConnectedToGlobalHub => hub?.State == HubConnectionState.Connected;
    private DateTime lastShareSizeBroadcast = DateTime.MinValue;

    public List<PeerInfo> OnlinePeers { get; } = new();

    public event Action? OnStateChanged;
    public event Action<string, string>? OnFilelistReceived;
    public event Action<SearchResult>? OnSearchResultReceived;
    public event Action<string, string>? OnPrivateMsgReceived;
    public event Action<string, string>? OnGlobalChatReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        transferServer.Start();
        
        // Load custom NodeName if saved
        var savedName = db.GetSetting("NodeName");
        if (!string.IsNullOrEmpty(savedName)) NodeName = savedName;

        // Auto-Reconnect Logic
        var lastHub = db.GetSetting("LastHubUrl");
        var lastUser = db.GetSetting("LastUsername");
        if (!string.IsNullOrEmpty(lastHub) && !string.IsNullOrEmpty(lastUser))
        {
            Console.WriteLine($"[GlobalHub] Auto-reconnecting to {lastHub} as {lastUser}...");
            _ = ConnectToGlobalHub(lastHub, lastUser);
        }

        shareManager.OnShareSizeChanged += async (size) => {
            if ((DateTime.Now - lastShareSizeBroadcast).TotalSeconds > 5)
            {
                lastShareSizeBroadcast = DateTime.Now;
                await BroadcastUpdate();
            }
            OnStateChanged?.Invoke();
        };

        await Task.CompletedTask;
    }

    public async Task ConnectToGlobalHub(string url, string username)
    {
        if (hub != null) await hub.DisposeAsync();
        
        // Normalize URL
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        
        if (!url.EndsWith("/signaling", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/signaling";
        }

        NodeName = username;
        GlobalHubUrl = url;

        // Persist for Auto-Reconnect
        db.SaveSetting("LastHubUrl", url);
        db.SaveSetting("LastUsername", username);
        
        hub = new HubConnectionBuilder()
            .WithUrl(url)
            .WithAutomaticReconnect()
            .Build();

        SetupHubEvents();

        try
        {
            await hub.StartAsync();
            OnStateChanged?.Invoke();
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, "", transferServer.ListenPort);
            await hub.InvokeAsync("JoinAsNode", peerInfo);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[GlobalHub] Failed to connect: {ex.Message}");
        }
        
        OnStateChanged?.Invoke();
    }

    public async Task DisconnectFromGlobalHub()
    {
        if (hub != null)
        {
            await hub.DisposeAsync();
            hub = null;
        }
        
        GlobalHubUrl = "";
        
        // Clear last used to prevent auto-reconnect on next startup if desired?
        // Let's keep it for now unless user wants to clear it.
        
        OnStateChanged?.Invoke();
    }

    private void SetupHubEvents()
    {
        if (hub == null) return;

        hub.On<IEnumerable<PeerInfo>>("PeerList", list =>
        {
            OnlinePeers.Clear();
            OnlinePeers.AddRange(list);
            OnStateChanged?.Invoke();
        });

        hub.On<PeerInfo>("PeerJoined", p =>
        {
            if (!OnlinePeers.Any(x => x.PeerId == p.PeerId)) OnlinePeers.Add(p);
            OnStateChanged?.Invoke();
        });

        hub.On<string>("PeerLeft", id =>
        {
            OnlinePeers.RemoveAll(x => x.PeerId == id);
            OnStateChanged?.Invoke();
        });

        hub.On<PeerInfo>("PeerUpdated", p =>
        {
            var idx = OnlinePeers.FindIndex(x => x.PeerId == p.PeerId);
            if (idx >= 0) OnlinePeers[idx] = p;
            OnStateChanged?.Invoke();
        });

        hub.On<SignalMessage>("ReceiveSignal", async msg =>
        {
            await Task.CompletedTask;
        });

        hub.On<SearchRequest, string>("SearchRequest", async (req, requesterConnId) =>
        {
            var results = shareManager.SearchLocal(req.Query);
            foreach (var res in results)
            {
                var searchResult = new SearchResult(res, new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, "", transferServer.ListenPort));
                await hub.InvokeAsync("SubmitSearchResult", requesterConnId, searchResult);
            }
        });

        hub.On<SearchResult>("ReceiveSearchResult", async (res) =>
        {
            OnSearchResultReceived?.Invoke(res);
        });

        hub.On<string, string, string>("ReceivePrivateMessage", (targetId, senderId, content) =>
        {
            if (targetId == PeerId)
            {
                OnPrivateMsgReceived?.Invoke(senderId, content);
            }
        });

        hub.On<string, string>("ReceiveChat", (senderName, message) =>
        {
            OnGlobalChatReceived?.Invoke(senderName, message);
        });

        hub.Closed += async (error) => { OnStateChanged?.Invoke(); await Task.CompletedTask; };
        hub.Reconnected += async (connectionId) => { OnStateChanged?.Invoke(); await Task.CompletedTask; };
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (hub != null) await hub.StopAsync(cancellationToken);
    }

    public async Task RequestRemoteFileListTcp(string targetPeerId)
    {
        if (targetPeerId == PeerId)
        {
            var json = shareManager.GetLocalFileListJson();
            OnFilelistReceived?.Invoke(targetPeerId, json);
            return;
        }

        var targetPeer = OnlinePeers.FirstOrDefault(p => p.PeerId == targetPeerId);
        if (targetPeer == null) return;
        
        string ip = string.IsNullOrEmpty(targetPeer.DirectIp) ? "127.0.0.1" : targetPeer.DirectIp;
        int port = targetPeer.TransferPort;
        if (port == 0) return;

        try 
        {
            using var client = new System.Net.Sockets.TcpClient();
            await client.ConnectAsync(ip, port);
            using var stream = client.GetStream();
            using var writer = new System.IO.StreamWriter(stream, System.Text.Encoding.UTF8, leaveOpen: true);
            
            await writer.WriteLineAsync("REQ_LIST|");
            await writer.FlushAsync();
            
            byte[] lenBuffer = new byte[4];
            int bytesRead = 0;
            while(bytesRead < 4) {
                int r = await stream.ReadAsync(lenBuffer.AsMemory(bytesRead, 4 - bytesRead));
                if (r == 0) return;
                bytesRead += r;
            }
            int length = BitConverter.ToInt32(lenBuffer, 0);
            
            byte[] dataBuffer = new byte[length];
            bytesRead = 0;
            while(bytesRead < length) {
                int r = await stream.ReadAsync(dataBuffer.AsMemory(bytesRead, length - bytesRead));
                if (r == 0) return;
                bytesRead += r;
            }
            
            string json = System.Text.Encoding.UTF8.GetString(dataBuffer);
            OnFilelistReceived?.Invoke(targetPeerId, json);
        } catch { }
    }

    public async Task SendMessage(string targetPeerId, string data)
    {
        if (hub != null && hub.State == HubConnectionState.Connected)
        {
            await hub.InvokeAsync("SendSignal", new SignalMessage(targetPeerId, PeerId, data));
        }
    }

    public async Task BroadcastUpdate()
    {
        if (hub != null && hub.State == HubConnectionState.Connected)
        {
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, "", transferServer.ListenPort);
            await hub.InvokeAsync("UpdateNodeParams", peerInfo);
        }
    }

    public async Task PerformGlobalSearch(string query)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("Search", new SearchRequest(query));
    }

    public async Task SendPrivateMessage(string targetPeerId, string content)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SendPrivateMessage", targetPeerId, PeerId, content);
    }

    public async Task SendGlobalChat(string message)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("Chat", NodeName, message);
    }

    public void SetNodeName(string name)
    {
        NodeName = name;
        db.SaveSetting("NodeName", name);
        _ = BroadcastUpdate();
        OnStateChanged?.Invoke();
    }
}
