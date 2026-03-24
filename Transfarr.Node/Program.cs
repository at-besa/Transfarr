using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Transfarr.Node.Core;
using Transfarr.Node.Hubs;
using Transfarr.Shared.Models;
using System.Linq;
using System.IO;

var builder = WebApplication.CreateBuilder(args);

var nodePort = builder.Configuration.GetValue<int>("Transfarr:NodePort", 5150);
builder.WebHost.UseUrls($"http://localhost:{nodePort}");

builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy => policy
        .SetIsOriginAllowed(origin => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});
builder.Services.AddSignalR();

// Load Transfarr Config
var transfarrConfig = builder.Configuration.GetSection("Transfarr");
var defaultHubUrls = transfarrConfig.GetSection("DefaultHubUrls").Get<string[]>() ?? Array.Empty<string>();
var defaultNodeName = transfarrConfig.GetValue<string>("DefaultNodeName") ?? "Transfarr-Node";

// Register Core Services
builder.Services.AddSingleton<SystemLogger>();
builder.Services.AddSingleton<ShareDatabase>();
builder.Services.AddSingleton<ShareManager>();
builder.Services.AddSingleton<TransferServer>();
builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddSingleton<NodeConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeConnectionManager>());

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
}

app.UseCors("CorsPolicy");
app.UseBlazorFrameworkFiles();
app.UseStaticFiles();
app.MapHub<LocalClientHub>("/localhub");

// Setup event bridges to push state to the UI via LocalClientHub
var hubContext = app.Services.GetRequiredService<IHubContext<LocalClientHub>>();
var node = app.Services.GetRequiredService<NodeConnectionManager>();
var dl = app.Services.GetRequiredService<DownloadManager>();
var sm = app.Services.GetRequiredService<ShareManager>();
var logger = app.Services.GetRequiredService<SystemLogger>();
var ts = app.Services.GetRequiredService<TransferServer>();

sm.Initialize();
dl.Initialize();
// ts.Start() is now called by NodeConnectionManager.StartAsync() automatically.

node.OnStateChanged += () => {
    hubContext.Clients.All.SendAsync("StateUpdate", node.OnlinePeers);
    hubContext.Clients.All.SendAsync("GlobalHubStatus", node.IsConnectedToGlobalHub, node.GlobalHubUrl, node.NodeName);
    hubContext.Clients.All.SendAsync("ConfigurationDefaults", defaultHubUrls, defaultNodeName);
    
    var self = new PeerInfo("", node.PeerId, node.NodeName, sm.TotalSharedBytes, "127.0.0.1", ts.ListenPort);
    hubContext.Clients.All.SendAsync("SelfStatus", self);
};

node.OnFilelistReceived += (peerId, json) => {
    hubContext.Clients.All.SendAsync("FileListReceived", peerId, json);
};

node.OnSearchResultReceived += (res) => {
    hubContext.Clients.All.SendAsync("ReceiveSearchResult", res);
};

node.OnPrivateMsgReceived += (senderId, content) => {
    hubContext.Clients.All.SendAsync("ReceivePrivateMessage", senderId, content);
};

node.OnGlobalChatReceived += (senderName, message) => {
    hubContext.Clients.All.SendAsync("ReceiveChat", senderName, message);
};

dl.OnQueueChanged += () => {
    hubContext.Clients.All.SendAsync("QueueUpdate", dl.AllItems);
};

sm.OnHashProgress += (state) => {
    hubContext.Clients.All.SendAsync("HashProgressUpdate", state);
    if (!state.IsHashing) {
        var self = new PeerInfo("", node.PeerId, node.NodeName, sm.TotalSharedBytes, "127.0.0.1", ts.ListenPort);
        hubContext.Clients.All.SendAsync("SelfStatus", self);
    }
};

logger.OnLog += (entry) => {
    hubContext.Clients.All.SendAsync("SystemLog", entry);
};



app.MapFallbackToFile("index.html");
app.Run();
