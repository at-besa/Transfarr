using System.Net.Http;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Collections.Concurrent;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Configuration;
using Mono.Nat;
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
    private readonly IConfiguration configuration;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IEnumerable<string>>> pendingNegotiations = new();
    private readonly CancellationTokenSource serviceCts = new();

    public NodeConnectionManager(ShareManager shareManager, TransferServer transferServer, ShareDatabase db, DownloadManager downloadManager, SystemLogger logger, IConfiguration configuration)
    {
        this.shareManager = shareManager;
        this.transferServer = transferServer;
        this.db = db;
        this.downloadManager = downloadManager;
        this.logger = logger;
        this.configuration = configuration;
        
        // Link DownloadManager to Hub signaling
        this.downloadManager.RequestConnectBackAction = async (targetId, hash) => {
            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("RequestConnectBack", targetId, hash);
        };

        this.downloadManager.RequestNegotiationAction = async (targetId, requestId) => {
            var tcs = new TaskCompletionSource<IEnumerable<string>>();
            pendingNegotiations[requestId] = tcs;

            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("InitiateConnectionNegotiation", targetId, requestId);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000, serviceCts.Token));
            if (completedTask == tcs.Task) return await tcs.Task;
            
            pendingNegotiations.TryRemove(requestId, out _);
            return new List<string>();
        };
    }
    public string PeerId { get; } = Guid.NewGuid().ToString("N");
    public string NodeName { get; set; } = "DesktopNode_" + Random.Shared.Next(100, 999);
    
    public string GlobalHubUrl { get; private set; } = string.Empty;
    public bool IsConnectedToGlobalHub => hub?.State == HubConnectionState.Connected;
    private DateTime lastShareSizeBroadcast = DateTime.MinValue;

    public List<PeerInfo> OnlinePeers { get; } = new();
    
    public enum ConnectivityMode { Auto = 0, ForceActive = 1, ForcePassive = 2 }
    public ConnectivityMode CurrentConnectivityMode { get; private set; } = ConnectivityMode.Auto;
    public string? ManualPublicIp { get; private set; }
    
    private string _localIp = "127.0.0.1";
    private string? _upnpExternalIp;
    public bool IsPassive { get; private set; } = false;

    public event Action? OnStateChanged;
    public event Action<string, string>? OnFilelistReceived;
    public event Action<string, string>? OnFilelistStatusUpdate; // targetPeerId, status
    public event Action<SearchResult>? OnSearchResultReceived;
    public event Action<string, string>? OnPrivateMsgReceived;
    public event Action<string, string>? OnGlobalChatReceived;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        transferServer.Start();
        
        var enableUPnP = configuration.GetValue<bool>("P2PSettings:EnableUPnP", true);
        if (enableUPnP)
        {
            await SetupUPnP(serviceCts.Token);
        }
        
        // Load custom Hub/Node settings
        var savedMode = db.GetSetting("ConnectivityMode");
        if (Enum.TryParse<ConnectivityMode>(savedMode, out var mode)) CurrentConnectivityMode = mode;
        
        ManualPublicIp = db.GetSetting("ManualPublicIp");

        // Load custom NodeName if saved
        var savedName = db.GetSetting("NodeName");
        if (!string.IsNullOrEmpty(savedName)) NodeName = savedName;

        // Auto-Reconnect Logic with Auth support
        var lastHub = db.GetSetting("LastHubUrl");
        var lastUser = db.GetSetting("LastUsername");
        if (!string.IsNullOrEmpty(lastHub) && !string.IsNullOrEmpty(lastUser))
        {
            Console.WriteLine($"[GlobalHub] Auto-reconnecting to {lastHub} as {lastUser}...");
            _ = ConnectToGlobalHub(lastHub, lastUser, cancellationToken: serviceCts.Token);
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

    public async Task ConnectToGlobalHub(string url, string username, string? token = null, CancellationToken cancellationToken = default)
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
                await hub.StartAsync(cancellationToken);
                
                // 1. Detect Local IP
                var host = await System.Net.Dns.GetHostEntryAsync(System.Net.Dns.GetHostName(), cancellationToken);
                _localIp = host.AddressList.FirstOrDefault(ip => ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)?.ToString() ?? "127.0.0.1";
                logger.LogInfo($"[Node] Local IP detected: {_localIp}");

                // 2. Get Public IP from Hub (Mirroring)
                var publicIp = await hub.InvokeAsync<string>("GetMyPublicIp", cancellationToken);
                logger.LogInfo($"[Node] Public IP (via Hub): {publicIp}");

                OnStateChanged?.Invoke();

                // 3. Perform Connectivity Detection
                if (CurrentConnectivityMode == ConnectivityMode.ForcePassive)
                {
                    IsPassive = true;
                }
                else if (CurrentConnectivityMode == ConnectivityMode.ForceActive)
                {
                    IsPassive = false;
                }
                else
                {
                    bool isActive = await hub.InvokeAsync<bool>("TestConnectivity", _localIp, transferServer.ListenPort, cancellationToken);
                    if (!isActive && publicIp != _localIp)
                    {
                        // Try testing with public IP too
                        isActive = await hub.InvokeAsync<bool>("TestConnectivity", publicIp, transferServer.ListenPort, cancellationToken);
                    }
                    IsPassive = !isActive;
                }
                
                string effectivePublicIp = publicIp;
                if (IsPrivateIp(publicIp) && !string.IsNullOrEmpty(_upnpExternalIp) && !IsPrivateIp(_upnpExternalIp))
                {
                    effectivePublicIp = _upnpExternalIp;
                    logger.LogInfo($"[Node] Overriding public IP with UPnP external IP: {effectivePublicIp} (Hub reported local IP {publicIp})");
                }
                
                string directIp = !string.IsNullOrWhiteSpace(ManualPublicIp) ? ManualPublicIp : (IsPassive ? _localIp : effectivePublicIp);
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, directIp, transferServer.ListenPort, IsPassive, _localIp, effectivePublicIp);
            await hub.InvokeAsync("JoinAsNode", peerInfo, cancellationToken);
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
                var selfInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, "", transferServer.ListenPort, IsPassive, _localIp, "");
                var searchResult = new SearchResult(res, selfInfo);
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

        hub.On<List<string>, int, string>("OnConnectBackRequested", async (ips, port, hash) =>
        {
            logger.LogInfo($"[Node] Received ConnectBack request for {hash}. Candidates: {string.Join(", ", ips)}");
            foreach (var ip in ips)
            {
                try 
                {
                    logger.LogInfo($"[Node] Attempting ConnectBack to {ip}:{port} for {hash}...");
                    // Basic check to see if we can reach it
                    using var client = new System.Net.Sockets.TcpClient();
                    var connectTask = client.ConnectAsync(ip, port, serviceCts.Token).AsTask();
                    if (await Task.WhenAny(connectTask, Task.Delay(2000, serviceCts.Token)) == connectTask)
                    {
                        await connectTask;
                        logger.LogInfo($"[Node] ConnectBack SUCCESS via {ip}:{port}");
                        await transferServer.HandlePreConnectedClient(client, hash, ip, serviceCts.Token);
                        break; // Success
                    }
                    else
                    {
                        logger.LogWarning($"[Node] ConnectBack timed out for {ip}:{port}");
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning($"[Node] ConnectBack failed for {ip}:{port}: {ex.Message}");
                }
            }
        });

        transferServer.OnReverseConnectionReceived += (client, hash) =>
        {
            downloadManager.HandleReverseConnection(client, hash);
        };

        hub.On<string, string>("ReceiveChat", (senderName, message) =>
        {
            OnGlobalChatReceived?.Invoke(senderName, message);
        });

        hub.On<string, string>("OnNegotiationRequested", async (requesterPeerId, requestId) =>
        {
            var candidates = new List<string> { _localIp };
            var publicIp = await hub.InvokeAsync<string>("GetMyPublicIp");
            if (publicIp != _localIp) candidates.Add(publicIp);
            if (!string.IsNullOrWhiteSpace(ManualPublicIp)) candidates.Add(ManualPublicIp);
            
            await hub.InvokeAsync("SubmitIceCandidates", requesterPeerId, requestId, candidates);
        });

        hub.On<string, IEnumerable<string>>("OnIceCandidatesReceived", (requestId, candidates) =>
        {
            if (pendingNegotiations.TryRemove(requestId, out var tcs))
            {
                tcs.SetResult(candidates);
            }
        });

        hub.On("OnSuspended", async () =>
        {
            Console.WriteLine("[GlobalHub] Your account has been suspended by an administrator. Disconnecting...");
            await DisconnectFromGlobalHub();
        });

        hub.Closed += async (error) => { OnStateChanged?.Invoke(); await Task.CompletedTask; };
        hub.Reconnected += async (connectionId) => { OnStateChanged?.Invoke(); await Task.CompletedTask; };
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        serviceCts.Cancel();
        transferServer.Stop();
        downloadManager.Shutdown();
        shareManager.Shutdown();
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
        
        TcpClient client = new TcpClient();
        Stream stream;
        
        if (targetPeer.IsPassive)
        {
            logger.LogInfo($"[Node] Peer {targetPeer.Name} is passive. Requesting ConnectBack for filelist...");
            OnFilelistStatusUpdate?.Invoke(targetPeerId, "Peer is passive. Requesting ConnectBack...");
            
            var tcs = new TaskCompletionSource<TcpClient>();
            downloadManager.HandleReverseConnectionRequest("ADL_LIST", tcs);
            
            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("RequestConnectBack", targetPeerId, "ADL_LIST");
                 
            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(10000, serviceCts.Token));
            if (completedTask != tcs.Task) 
            { 
                client.Dispose(); 
                throw new Exception("ConnectBack timeout. The remote peer failed to connect back within 10 seconds. This usually means your port 5151 is not reachable from the outside."); 
            }
             
            client = await tcs.Task;
            stream = client.GetStream();
        }
        else
        {
            // Stage 3: Negotiation
            logger.LogInfo($"[Node] Negotiating connection with {targetPeer.Name}...");
            OnFilelistStatusUpdate?.Invoke(targetPeerId, "Negotiating direct connection...");
            
            var requestId = Guid.NewGuid().ToString("N");
            var tcs = new TaskCompletionSource<IEnumerable<string>>();
            pendingNegotiations[requestId] = tcs;

            if (hub?.State == HubConnectionState.Connected)
                await hub.InvokeAsync("InitiateConnectionNegotiation", targetPeerId, requestId);

            var completedTask = await Task.WhenAny(tcs.Task, Task.Delay(5000, serviceCts.Token));
            IEnumerable<string> candidates;
            if (completedTask == tcs.Task) candidates = await tcs.Task;
            else { candidates = new List<string> { targetPeer.DirectIp }; pendingNegotiations.TryRemove(requestId, out _); }

            bool connected = false;
            int port = targetPeer.TransferPort;
            
            foreach (var ip in candidates.Distinct())
            {
                if (string.IsNullOrEmpty(ip)) continue;
                try
                {
                    logger.LogInfo($"[Node] Trying candidate: {ip}:{port}...");
                    OnFilelistStatusUpdate?.Invoke(targetPeerId, $"Trying candidate: {ip}...");
                    
                    var connectTask = client.ConnectAsync(ip, port, serviceCts.Token).AsTask();
                    if (await Task.WhenAny(connectTask, Task.Delay(2000, serviceCts.Token)) == connectTask)
                    {
                        await connectTask;
                        connected = true;
                        logger.LogInfo($"[Node] Connected to {targetPeer.Name} via {ip}:{port}");
                        break;
                    }
                }
                catch { /* Try next candidate */ }
            }

            if (!connected)
            {
                client.Dispose();
                throw new Exception($"Failed to connect to {targetPeer.Name} after trying all candidates (Direct IP, UPnP, Mirror).");
            }
            stream = client.GetStream();
        }
        
        OnFilelistStatusUpdate?.Invoke(targetPeerId, "Connected! Downloading list...");

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
                    if (r == 0) throw new Exception("Connection closed while reading list length.");
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
            var publicIp = await hub.InvokeAsync<string>("GetMyPublicIp");
            string effectivePublicIp = publicIp;
            if (IsPrivateIp(publicIp) && !string.IsNullOrEmpty(_upnpExternalIp) && !IsPrivateIp(_upnpExternalIp))
            {
                effectivePublicIp = _upnpExternalIp;
            }

            string directIp = !string.IsNullOrWhiteSpace(ManualPublicIp) ? ManualPublicIp : (IsPassive ? _localIp : effectivePublicIp);
            var peerInfo = new PeerInfo(hub.ConnectionId ?? "", PeerId, NodeName, shareManager.TotalSharedBytes, directIp, transferServer.ListenPort, IsPassive, _localIp, effectivePublicIp);
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

    public async Task SetConnectivityMode(ConnectivityMode mode)
    {
        CurrentConnectivityMode = mode;
        db.SaveSetting("ConnectivityMode", mode.ToString());
        
        if (hub?.State == HubConnectionState.Connected)
        {
            // Re-detect or override
            if (mode == ConnectivityMode.ForcePassive) IsPassive = true;
            else if (mode == ConnectivityMode.ForceActive) IsPassive = false;
            else 
            {
                bool isActive = await hub.InvokeAsync<bool>("TestConnectivity", _localIp, transferServer.ListenPort);
                IsPassive = !isActive;
            }
            await BroadcastUpdate();
        }
        OnStateChanged?.Invoke();
    }

    public async Task SetManualPublicIp(string? ip)
    {
        ManualPublicIp = string.IsNullOrWhiteSpace(ip) ? null : ip;
        db.SaveSetting("ManualPublicIp", ManualPublicIp ?? "");
        
        if (hub?.State == HubConnectionState.Connected)
        {
            await BroadcastUpdate();
        }
        OnStateChanged?.Invoke();
    }

    private async Task SetupUPnP(CancellationToken token)
    {
        try
        {
            var tcs = new TaskCompletionSource<INatDevice>();
            
            void DeviceFoundHandler(object? sender, DeviceEventArgs args)
            {
                tcs.TrySetResult(args.Device);
            }

            NatUtility.DeviceFound += DeviceFoundHandler;
            NatUtility.StartDiscovery();
            
            logger.LogInfo("[UPnP] Searching for NAT devices...");

            using var timeoutCts = new CancellationTokenSource(10000);
            using var combinedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);
            
            try
            {
                var device = await tcs.Task.WaitAsync(combinedCts.Token);
                NatUtility.StopDiscovery();
                NatUtility.DeviceFound -= DeviceFoundHandler;

                var ip = await device.GetExternalIPAsync();
                _upnpExternalIp = ip.ToString();
                logger.LogInfo($"[UPnP] Router detected! External IP: {_upnpExternalIp}");

                await device.CreatePortMapAsync(new Mapping(Protocol.Tcp, transferServer.ListenPort, transferServer.ListenPort, 0, "Transfarr P2P"));
                logger.LogInfo($"[UPnP] Port {transferServer.ListenPort} successfully mapped on router via Mono.Nat.");
                
                IsPassive = false;
            }
            catch (OperationCanceledException)
            {
                NatUtility.StopDiscovery();
                NatUtility.DeviceFound -= DeviceFoundHandler;
                logger.LogWarning("[UPnP] No NAT devices found within 10 seconds.");
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning($"[UPnP] Failed to map port: {ex.Message}");
        }
    }

    private bool IsPrivateIp(string ip)
    {
        if (System.Net.IPAddress.TryParse(ip, out var address))
        {
            byte[] bytes = address.GetAddressBytes();
            if (bytes[0] == 10) return true;
            if (bytes[0] == 172 && bytes[1] >= 16 && bytes[1] <= 31) return true;
            if (bytes[0] == 192 && bytes[1] == 168) return true;
            if (bytes[0] == 127) return true;
            if (bytes[0] == 169 && bytes[1] == 254) return true; // Link-local
        }
        return false;
    }
}
