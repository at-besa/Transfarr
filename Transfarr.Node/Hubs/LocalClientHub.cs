using Microsoft.AspNetCore.SignalR;
using System.Linq;
using System.Threading.Tasks;
using Transfarr.Node.Core;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Hubs;

public class LocalClientHub(NodeConnectionManager node, DownloadManager downloads, ShareManager shares, ShareDatabase db, TransferServer ts, SystemLogger logger) : Hub
{
    public override async Task OnConnectedAsync()
    {
        logger.LogInfo($"Local UI connected successfully to internal Core daemon.");

        var history = logger.History;
        for (int i = history.Count - 1; i >= 0; i--)
        {
            await Clients.Caller.SendAsync("SystemLog", history[i]);
        }

        // Send Current Node's Self Info (Extension 10)
        var selfInfo = new PeerInfo(Context.ConnectionId ?? "", node.PeerId, node.NodeName, shares.TotalSharedBytes, "127.0.0.1", ts.ListenPort);
        await Clients.Caller.SendAsync("SelfStatus", selfInfo);

        await Clients.Caller.SendAsync("StateUpdate", node.OnlinePeers);
        await Clients.Caller.SendAsync("QueueUpdate", downloads.AllItems);
        await Clients.Caller.SendAsync("UploadUpdate", ts.ActiveUploads.Values.ToList());
        await Clients.Caller.SendAsync("SharesUpdate", shares.GetSharedDirectories());
        await Clients.Caller.SendAsync("GlobalHubStatus", node.IsConnectedToGlobalHub, node.GlobalHubUrl, node.NodeName);
        await Clients.Caller.SendAsync("ConnectivityModeUpdate", (int)node.CurrentConnectivityMode);
        await base.OnConnectedAsync();
    }

    public async Task<AuthResponse> Login(string url, string username, string password)
    {
        var result = await node.LoginAsync(url, username, password);
        if (result.Success)
        {
            await node.ConnectToGlobalHub(url, username, result.Token);
            await Clients.All.SendAsync("GlobalHubStatus", node.IsConnectedToGlobalHub, node.GlobalHubUrl, node.NodeName);
        }
        return result;
    }

    public async Task ConnectToGlobalHub(string url, string username)
    {
        await node.ConnectToGlobalHub(url, username);
        await Clients.All.SendAsync("GlobalHubStatus", node.IsConnectedToGlobalHub, node.GlobalHubUrl, node.NodeName);
        
        // Update self status after connecting
        var selfInfo = new PeerInfo(Context.ConnectionId ?? "", node.PeerId, node.NodeName, shares.TotalSharedBytes, "127.0.0.1", ts.ListenPort);
        await Clients.Caller.SendAsync("SelfStatus", selfInfo);
    }

    public async Task RequestRemoteFileList(string targetPeerId)
    {
        await node.RequestRemoteFileListTcp(targetPeerId);
    }

    public async Task PerformSearch(string query)
    {
        await node.PerformGlobalSearch(query);
    }

    public async Task SendPrivateMessage(string targetPeerId, string content)
    {
        await node.SendPrivateMessage(targetPeerId, content);
    }

    public async Task SendGlobalChat(string message)
    {
        await node.SendGlobalChat(message);
    }

    public List<DownloadItem> GetDownloadQueue() => downloads.AllItems;

    public void RemoveFromDownload(string itemId) => downloads.RemoveFromQueue(itemId);

    public async Task AddToDownloadQueue(string peerId, string filename, long size, string tth)
    {
        var peer = node.OnlinePeers.FirstOrDefault(p => p.PeerId == peerId);
        if (peer != null)
        {
            downloads.AddToQueue(peer, filename, size, tth);
            await Clients.All.SendAsync("QueueUpdate", downloads.AllItems);
        }
    }

    public async Task AddFolderToQueue(string targetPeerId, FileListItem folder)
    {
        var peer = node.OnlinePeers.FirstOrDefault(p => p.PeerId == targetPeerId);
        if (peer != null)
        {
            downloads.AddFolderToQueue(peer, folder);
        }
    }

    public async Task AddFolderByPathToQueue(string targetPeerId, string virtualPath)
    {
        var peer = node.OnlinePeers.FirstOrDefault(p => p.PeerId == targetPeerId);
        if (peer != null)
        {
            await downloads.AddFolderByPathToQueue(peer, virtualPath);
        }
    }

    public string GetDownloadPath() => downloads.DownloadsFolder;

    public bool IsDownloadPathConfigured() => downloads.IsConfigured;

    public void SetDownloadPath(string path)
    {
        downloads.SetDownloadsFolder(path);
        _ = Clients.All.SendAsync("SettingsUpdate", true);
    }

    public int GetP2PPort() 
    {
        var portStr = db.GetSetting("P2PPort");
        if (int.TryParse(portStr, out var p)) return p;
        return 0; // 0 means random
    }

    public int GetCurrentListenPort() => ts.ListenPort;

    public void SetP2PPort(int port)
    {
        db.SaveSetting("P2PPort", port.ToString());
        _ = Clients.All.SendAsync("SettingsUpdate", true);
    }

    public Task<string[]> GetOSDirectories(string path)
    {
        return Task.FromResult(shares.GetOSDirectories(path));
    }

    public void RefreshShares() => shares.StartRebuild();

    public Dictionary<string, long> GetShareSizes() => shares.GetShareSizes();

    public bool HaveFile(string tth) => shares.HasFileByTth(tth);

    public Dictionary<string, List<string>> GetDuplicates() {
        var all = new Dictionary<string, List<string>>();
        foreach(var tth in shares.GetLocalFileList().Items.SelectMany(i => GetAllTths(i))) {
            var paths = shares.GetLocalPathsByTth(tth);
            if (paths.Count > 1) all[tth] = paths;
        }
        return all;
    }

    private IEnumerable<string> GetAllTths(FileListItem item) {
        if (!item.IsDirectory && !string.IsNullOrEmpty(item.Tth)) yield return item.Tth;
        if (item.Children != null) {
            foreach(var child in item.Children) {
                foreach(var t in GetAllTths(child)) yield return t;
            }
        }
    }

    public List<string> GetLocalTths() => shares.GetLocalFileList().Items.SelectMany(i => GetAllTths(i)).Distinct().ToList();

    public async Task AddSharedDirectory(string virtualName, string path)
    {
        shares.AddSharedDirectory(virtualName, path);
        await Clients.All.SendAsync("SharesUpdate", shares.GetSharedDirectories());
        await node.BroadcastUpdate();
    }

    public async Task RemoveSharedDirectory(string virtualName)
    {
        shares.RemoveSharedDirectory(virtualName);
        await Clients.All.SendAsync("SharesUpdate", shares.GetSharedDirectories());
        await node.BroadcastUpdate();
    }

    public List<HubFavorite> GetHubFavorites()
    {
        return db.GetHubFavorites().Select(f => new HubFavorite { Url = f.Url, Name = f.Name }).ToList();
    }

    public void AddHubFavorite(string url, string name)
    {
        // Normalize for storage
        if (!url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && 
            !url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            url = "http://" + url;
        }
        
        if (!url.EndsWith("/signaling", StringComparison.OrdinalIgnoreCase))
        {
            url = url.TrimEnd('/') + "/signaling";
        }

        db.AddHubFavorite(url, name);
    }

    public void RemoveHubFavorite(string url)
    {
        db.RemoveHubFavorite(url);
    }

    public void SetNodeName(string name)
    {
        node.SetNodeName(name);
    }

    public async Task DisconnectFromGlobalHub()
    {
        await node.DisconnectFromGlobalHub();
    }

    public int GetConnectivityMode() => (int)node.CurrentConnectivityMode;

    public async Task SetConnectivityMode(int mode)
    {
        await node.SetConnectivityMode((NodeConnectionManager.ConnectivityMode)mode);
        await Clients.All.SendAsync("ConnectivityModeUpdate", mode);
    }

    public string? GetManualPublicIp() => node.ManualPublicIp;

    public async Task SetManualPublicIp(string? ip)
    {
        await node.SetManualPublicIp(ip);
        await Clients.All.SendAsync("ManualPublicIpUpdate", ip);
    }
}
