using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Core;

public class NodeConnectionManager : IHostedService
{
    private HubConnection? hub;
    private readonly HttpClient httpClient = new();
    private readonly ShareManager shareManager;
    private readonly TransferServer transferServer;
    private readonly ShareDatabase db;
    private readonly DownloadManager downloadManager;
    private readonly SystemLogger logger;

    public NodeConnectionManager(ShareManager shareManager, TransferServer transferServer, ShareDatabase db, DownloadManager downloadManager, SystemLogger logger)
    {
        this.shareManager = shareManager;
        this.transferServer = transferServer;
        this.db = db;
        this.downloadManager = downloadManager;
        this.logger = logger;
        
        // Link DownloadManager to Hub signaling
        this.downloadManager.RequestConnectBackAction = async (targetId, hash) => {
            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("RequestConnectBack", targetId, hash);
        };
    }
    public string PeerId { get; } = Guid.NewGuid().ToString("N");
    public string NodeName { get; set; } = "DesktopNode_" + Random.Shared.Next(100, 999);
    
    public string GlobalHubUrl { get; private set; } = string.Empty;
    public bool IsConnectedToGlobalHub => hub?.State == HubConnectionState.Connected;
    private DateTime lastShareSizeBroadcast = DateTime.MinValue;

    public List<PeerInfo> OnlinePeers { get; } = new();
    
    private string _localIp = "127.0.0.1";
    public bool IsPassive { get; private set; } = false;

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

        // Auto-Reconnect Logic with Auth support
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

        transferServer.OnUploadComplete += async (bytes) => {
            await NotifyUploadComplete(bytes);
        };

        await Task.CompletedTask;
    }

    public async Task<AuthResponse> LoginAsync(string url, string username, string password)
    {
        // Normalize URL for API - strip /signaling if present
        var apiUrl = url;
        if (!apiUrl.StartsWith("http")) apiUrl = "http://" + apiUrl;
        
        var uri = new Uri(apiUrl);
        var baseAddress = $"{uri.Scheme}://{uri.Authority}";
        apiUrl = baseAddress.TrimEnd('/') + "/api/auth/login";

        try
        {
            var response = await httpClient.PostAsJsonAsync(apiUrl, new LoginRequest { Username = username, Password = password });
            
            if (response.IsSuccessStatusCode)
            {
                var result = await response.Content.ReadFromJsonAsync<AuthResponse>();
                if (result != null && result.Success)
                {
                    db.SaveSetting("LastHubUrl", url);
                    db.SaveSetting("LastUsername", username);
                    db.SaveSetting("AuthToken", result.Token);
                    return result;
                }
                return result ?? new AuthResponse { Success = false, Error = "Invalid response from server" };
            }
            else
            {
                var errorMsg = await response.Content.ReadAsStringAsync();
                return new AuthResponse { Success = false, Error = $"Server error ({response.StatusCode}): {errorMsg}" };
            }
        }
        catch (Exception ex)
        {
            return new AuthResponse { Success = false, Error = ex.Message };
        }
    }

    public async Task ConnectToGlobalHub(string url, string username, string? token = null)
    {
        if (hub != null) await hub.DisposeAsync();
        
        // Normalize URL
        if (!url.StartsWith("http")) url = "http://" + url;
        var signalingUrl = url.TrimEnd('/') + "/signaling";

        // Use provided token or look in DB
        var authToken = token ?? db.GetSetting("AuthToken");

        NodeName = username;
        GlobalHubUrl = url;

        db.SaveSetting("LastHubUrl", url);
        db.SaveSetting("LastUsername", username);
        
        hub = new HubConnectionBuilder()
            .WithUrl(signalingUrl, options => {
                if (!string.IsNullOrEmpty(authToken))
                {
                    options.AccessTokenProvider = () => Task.FromResult<string?>(authToken);
                }
            })
            .WithAutomaticReconnect()
            .Build();

        SetupHubEvents();

        try
        {
            await hub.StartAsync();
            OnStateChanged?.Invoke();

            // Detect Local IP (via Hub connection info or simple heuristic)
            // For now we assume the Hub will tell us or we use a basic check
            // A more advanced Node might use a public IP API.
            
            // Perform Connectivity Test
            bool isActive = await hub.InvokeAsync<bool>("TestConnectivity", _localIp, transferServer.ListenPort);
            IsPassive = !isActive;
            
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, _localIp, transferServer.ListenPort, IsPassive);
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

        hub.On<string, int, string>("OnConnectBackRequested", async (ip, port, hash) =>
        {
            await transferServer.ConnectToPassiveDownloader(ip, port, hash);
        });

        transferServer.OnReverseConnectionReceived += (client, hash) =>
        {
            downloadManager.HandleReverseConnection(client, hash);
        };

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
        
        TcpClient client;
        Stream stream;
        
        if (targetPeer.IsPassive)
        {
            logger.LogInfo($"[Node] Peer {targetPeer.Name} is passive. Requesting ConnectBack for filelist...");
            var tcs = new TaskCompletionSource<TcpClient>();
            downloadManager.HandleReverseConnectionRequest("ADL_LIST", tcs);
            
            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("RequestConnectBack", targetPeerId, "ADL_LIST");
                
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000));
            if (completedTask != tcs.Task) return;
            
            client = await tcs.Task;
            stream = client.GetStream();
        }
        else
        {
            string ip = string.IsNullOrEmpty(targetPeer.DirectIp) ? "127.0.0.1" : targetPeer.DirectIp;
            int port = targetPeer.TransferPort;
            if (port == 0) return;

            client = new TcpClient();
            await client.ConnectAsync(ip, port);
            stream = client.GetStream();
        }

        try 
        {
            using (client)
            using (stream)
            {
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
            }
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
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, _localIp, transferServer.ListenPort, IsPassive);
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

    public async Task NotifyUploadComplete(long bytes)
    {
        if (hub?.State == HubConnectionState.Connected)
            await hub.InvokeAsync("ReportUploadComplete", bytes);
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
