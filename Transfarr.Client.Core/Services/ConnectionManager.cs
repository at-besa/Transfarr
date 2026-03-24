using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR.Client;
using Transfarr.Shared.Models;

namespace Transfarr.Client.Core.Services;

public class ConnectionManager() : IAsyncDisposable
{
    private HubConnection? hub;
    
    public ObservableCollection<PeerInfo> OnlinePeers { get; } = new();
    public ObservableCollection<string> ConnectedPeers { get; } = new(); 
    public ObservableCollection<DownloadItem> Queue { get; } = new();
    public ObservableCollection<UploadItem> ActiveUploads { get; } = new();
    public Dictionary<string, string> SharedDirectories { get; } = new();
    
    public bool IsConnected => hub?.State == HubConnectionState.Connected;

    public PeerInfo? LocalPeer { get; private set; }
    public string GlobalHubUrl { get; private set; } = "";
    public bool IsConnectedToGlobalHub { get; private set; }
    public string NodeName { get; private set; } = "";

    public List<string> DefaultHubUrls { get; private set; } = new();
    public string DefaultNodeName { get; private set; } = "";   
    public int ConnectivityMode { get; private set; } = 0; // 0=Auto, 1=Active, 2=Passive
    public HashProgressState CurrentHashProgress { get; private set; } = new();
    
    public ObservableCollection<LogEntry> SystemLogs { get; } = new();
    public ObservableCollection<SearchResult> SearchResults { get; } = new();
    public ObservableCollection<ChatMessage> GlobalMessages { get; } = new();
    public Dictionary<string, ObservableCollection<ChatMessage>> PrivateMessages { get; } = new();
    public ObservableCollection<HubFavorite> HubFavorites { get; } = new();

    public event Action? OnStateChanged;
    public event Action<string, string>? OnRawMessageReceived;
    public event Action<SearchResult>? OnSearchResultReceived;

    public async Task ConnectAsync(string hubUrl)
    {
        hub = new HubConnectionBuilder()
            .WithUrl(hubUrl)
            .WithAutomaticReconnect()
            .Build();

        hub.On<IEnumerable<PeerInfo>>("StateUpdate", peers =>
        {
            OnlinePeers.Clear();
            foreach (var p in peers) OnlinePeers.Add(p);
            OnStateChanged?.Invoke();
        });

        hub.On<IEnumerable<DownloadItem>>("QueueUpdate", docs =>
        {
            Queue.Clear();
            foreach (var doc in docs) Queue.Add(doc);
            OnStateChanged?.Invoke();
        });

        hub.On<IEnumerable<UploadItem>>("UploadUpdate", uploads =>
        {
            ActiveUploads.Clear();
            foreach (var up in uploads) ActiveUploads.Add(up);
            OnStateChanged?.Invoke();
        });

        hub.On<Dictionary<string, string>>("SharesUpdate", shares =>
        {
            SharedDirectories.Clear();
            foreach (var kvp in shares) SharedDirectories.Add(kvp.Key, kvp.Value);
            OnStateChanged?.Invoke();
        });

        hub.On<bool, string, string>("GlobalHubStatus", (connected, url, name) =>
        {
            IsConnectedToGlobalHub = connected;
            GlobalHubUrl = url;
            NodeName = name;
            OnStateChanged?.Invoke();
        });

        hub.On<List<string>, string>("ConfigurationDefaults", (urls, name) =>
        {
            DefaultHubUrls = urls;
            DefaultNodeName = name;
            OnStateChanged?.Invoke();
        });

        hub.On<HashProgressState>("HashProgressUpdate", state =>
        {
            CurrentHashProgress = state;
            OnStateChanged?.Invoke();
        });

        hub.On<string, string>("FileListReceived", (peerId, json) =>
        {
            OnRawMessageReceived?.Invoke(peerId, $"RES_FILELIST|{json}");
        });

        hub.On<LogEntry>("SystemLog", entry =>
        {
            lock (SystemLogs)
            {
                SystemLogs.Insert(0, entry); 
                if (SystemLogs.Count > 100) SystemLogs.RemoveAt(SystemLogs.Count - 1);
            }
            OnStateChanged?.Invoke();
        });

        hub.On<SearchResult>("ReceiveSearchResult", res =>
        {
            SearchResults.Add(res);
            OnSearchResultReceived?.Invoke(res);
            OnStateChanged?.Invoke();
        });

        hub.On<string, string>("ReceiveChat", (senderName, message) =>
        {
            var isMention = !string.IsNullOrEmpty(NodeName) && message.Contains("@" + NodeName, StringComparison.OrdinalIgnoreCase);
            GlobalMessages.Add(new ChatMessage { SenderName = senderName, Content = message, IsMention = isMention });
            OnStateChanged?.Invoke();
        });

        hub.On<string, string>("ReceivePrivateMessage", (senderId, content) =>
        {
            if (!PrivateMessages.TryGetValue(senderId, out var msgs))
            {
                msgs = new ObservableCollection<ChatMessage>();
                PrivateMessages[senderId] = msgs;
            }
            var senderName = OnlinePeers.FirstOrDefault(p => p.PeerId == senderId)?.Name ?? "Unknown";
            msgs.Add(new ChatMessage { SenderId = senderId, SenderName = senderName, Content = content, IsPrivate = true });
            OnStateChanged?.Invoke();
        });

        hub.On<int>("ConnectivityModeUpdate", mode =>
        {
            ConnectivityMode = mode;
            OnStateChanged?.Invoke();
        });

        hub.On<HashProgressState>("HashProgressUpdate", state =>
        {
            CurrentHashProgress = state;
            OnStateChanged?.Invoke();
        });

        hub.On<PeerInfo>("SelfStatus", self =>
        {
            LocalPeer = self;
            OnStateChanged?.Invoke();
        });

        await hub.StartAsync();
        
        // Load initial favorites
        await RefreshHubFavorites();

        OnStateChanged?.Invoke();
    }

    public async Task SendMessage(string peerId, string msg)
    {
        if (msg == "REQ_FILELIST")
        {
            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("RequestRemoteFileList", peerId);
        }
    }

    public async Task ConnectToPeer(string targetPeerId)
    {
        if (!ConnectedPeers.Contains(targetPeerId)) ConnectedPeers.Add(targetPeerId);
        await Task.CompletedTask;
    }

    public async Task AddToDownloadQueue(string peerId, string filename, long size, string tth)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("AddToDownloadQueue", peerId, filename, size, tth);
    }

    public async Task RemoveFromDownload(string itemId)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("RemoveFromDownload", itemId);
    }

    public async Task<Dictionary<string, string>> GetSharedDirectories()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<Dictionary<string, string>>("GetSharedDirectories");
        return new Dictionary<string, string>();
    }

    public async Task<Dictionary<string, long>> GetShareSizes()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<Dictionary<string, long>>("GetShareSizes");
        return new Dictionary<string, long>();
    }

    public async Task<string[]> GetOSDirectories(string path)
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<string[]>("GetOSDirectories", path);
        return Array.Empty<string>();
    }

    public async Task RefreshShares()
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.SendAsync("RefreshShares");
    }

    public async Task<bool> HaveFile(string tth)
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<bool>("HaveFile", tth);
        return false;
    }

    public async Task<Dictionary<string, List<string>>> GetDuplicates()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<Dictionary<string, List<string>>>("GetDuplicates");
        return new Dictionary<string, List<string>>();
    }

    public async Task<HashSet<string>> GetLocalTths()
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            var list = await hub.InvokeAsync<List<string>>("GetLocalTths");
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        return new HashSet<string>();
    }

    public async Task PerformSearch(string query)
    {
        SearchResults.Clear();
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("PerformSearch", query);
    }

    public async Task SendGlobalChat(string message)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SendGlobalChat", message);
    }

    public async Task SendPrivateMessage(string targetPeerId, string content)
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            await hub.InvokeAsync("SendPrivateMessage", targetPeerId, content);
            
            // Add to local UI as well
            if (!PrivateMessages.TryGetValue(targetPeerId, out var msgs))
            {
                msgs = new ObservableCollection<ChatMessage>();
                PrivateMessages[targetPeerId] = msgs;
            }
            msgs.Add(new ChatMessage { SenderId = LocalPeer?.PeerId ?? "Me", SenderName = "Me", Content = content, IsPrivate = true, IsMe = true });
            OnStateChanged?.Invoke();
        }
    }

    public async Task AddSharedDirectory(string virtualName, string path)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("AddSharedDirectory", virtualName, path);
    }

    public async Task<AuthResponse> Login(string url, string username, string password)
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            var result = await hub.InvokeAsync<AuthResponse>("Login", url, username, password);
            if (result.Success)
            {
                IsConnectedToGlobalHub = true;
                GlobalHubUrl = url;
                NodeName = username;
                OnStateChanged?.Invoke();
            }
            return result;
        }
        return new AuthResponse { Success = false, Error = "Not connected to local daemon" };
    }

    public async Task ConnectToGlobalHub(string url, string username)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("ConnectToGlobalHub", url, username);
    }

    public async Task RemoveSharedDirectory(string virtualName)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("RemoveSharedDirectory", virtualName);
    }

    public async Task RequestFolderDownload(string targetPeerId, FileListItem folder)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("AddFolderToQueue", targetPeerId, folder);
    }

    public async Task RequestFolderDownloadByPath(string targetPeerId, string virtualPath)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("AddFolderByPathToQueue", targetPeerId, virtualPath);
    }

    public async Task<string> GetDownloadPath()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<string>("GetDownloadPath");
        return "";
    }

    public async Task<bool> IsDownloadPathConfigured()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<bool>("IsDownloadPathConfigured");
        return true; // Default to true to not block if disconnected
    }

    public async Task SetDownloadPath(string path)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SetDownloadPath", path);
    }

    public async Task<int> GetConnectivityMode()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<int>("GetConnectivityMode");
        return 0;
    }

    public async Task SetConnectivityMode(int mode)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SetConnectivityMode", mode);
    }

    public async Task<int> GetP2PPort()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<int>("GetP2PPort");
        return 0;
    }

    public async Task<int> GetCurrentListenPort()
    {
        if (hub?.State == HubConnectionState.Connected)
            return await hub.InvokeAsync<int>("GetCurrentListenPort");
        return 0;
    }

    public async Task SetP2PPort(int port)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SetP2PPort", port);
    }

    public async Task RefreshHubFavorites()
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            var favs = await hub.InvokeAsync<List<HubFavorite>>("GetHubFavorites");
            HubFavorites.Clear();
            foreach (var f in favs) HubFavorites.Add(f);
            OnStateChanged?.Invoke();
        }
    }

    public async Task AddHubFavorite(string url, string name)
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            await hub.InvokeAsync("AddHubFavorite", url, name);
            await RefreshHubFavorites();
        }
    }

    public async Task RemoveHubFavorite(string url)
    {
        if (hub?.State == HubConnectionState.Connected)
        {
            await hub.InvokeAsync("RemoveHubFavorite", url);
            await RefreshHubFavorites();
        }
    }

    public async Task SetNodeName(string name)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("SetNodeName", name);
    }

    public async Task DisconnectFromGlobalHub()
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("DisconnectFromGlobalHub");
    }

    public async ValueTask DisposeAsync()
    {
        if (hub != null) await hub.DisposeAsync();
    }
}
