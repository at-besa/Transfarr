using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.SignalR;
using Transfarr.Node.Core;
using Transfarr.Node.Hubs;
using Transfarr.Shared.Models;
using System.Linq;
using System.IO;

using Transfarr.Node.Options;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Transfarr.Node.Services;
using Microsoft.AspNetCore.Diagnostics;

var builder = WebApplication.CreateBuilder(args);

// Bind Options
builder.Services.Configure<NodeOptions>(builder.Configuration.GetSection(NodeOptions.SectionName));

// Local Options for Startup
var nodeOptions = builder.Configuration.GetSection(NodeOptions.SectionName).Get<NodeOptions>() ?? new NodeOptions();
var nodePort = nodeOptions.NodePort;
builder.WebHost.UseUrls($"http://localhost:{nodePort}");

// ... usings ...
builder.Services.AddCors(options =>
{
    options.AddPolicy("CorsPolicy", policy => policy
        .SetIsOriginAllowed(origin => true)
        .AllowAnyMethod()
        .AllowAnyHeader()
        .AllowCredentials());
});
builder.Services.AddSignalR();
builder.Services.AddControllers();

// API Documentation & Exception Handling
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
	c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo 
	{ 
		Title = "Transfarr Node API", 
		Version = "v1",
		Description = "Local node API for managing P2P transfers and internal state."
	});

	var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
	var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
	if (File.Exists(xmlPath))
	{
		c.IncludeXmlComments(xmlPath);
	}

	c.AddSignalRSwaggerGen();
});

// Register Core Services
builder.Services.AddSingleton<SystemLogger>();
builder.Services.AddSingleton<ShareDatabase>();
builder.Services.AddSingleton<ShareManager>();
builder.Services.AddSingleton<BandwidthController>();
builder.Services.AddSingleton<CryptoManager>();
builder.Services.AddSingleton<TransferServer>();

builder.Services.AddSingleton<DownloadManager>();
builder.Services.AddSingleton<NodeConnectionManager>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<NodeConnectionManager>());

var app = builder.Build();

app.UseExceptionHandler(); // Uses Problem Details automatically
app.UseStatusCodePages(); // For static status code responses

if (app.Environment.IsDevelopment())
{
    app.UseWebAssemblyDebugging();
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transfarr Node API v1");
        c.RoutePrefix = "swagger";
    });
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
var crypto = app.Services.GetRequiredService<CryptoManager>();
var bc = app.Services.GetRequiredService<BandwidthController>();

// Initialize Bandwidth Limits
var db = app.Services.GetRequiredService<ShareDatabase>();
int ulL = int.TryParse(db.GetSetting("UploadLimitMBps"), out var ulV) ? ulV : 0;
int dlL = int.TryParse(db.GetSetting("DownloadLimitMBps"), out var dlV) ? dlV : 0;
bc.SetUploadLimitMBps(ulL);
bc.SetDownloadLimitMBps(dlL);

sm.Initialize();
dl.Initialize();
// ts.Start() is now called by NodeConnectionManager.StartAsync() automatically.

node.OnStateChanged += () => {
    hubContext.Clients.All.SendAsync("StateUpdate", node.OnlinePeers);
    hubContext.Clients.All.SendAsync("GlobalHubStatus", node.IsConnectedToGlobalHub, node.GlobalHubUrl, node.NodeName);
    hubContext.Clients.All.SendAsync("ConfigurationDefaults", nodeOptions.DefaultHubUrls, nodeOptions.DefaultNodeName);
    
    var self = new PeerInfo("", node.PeerId, node.NodeName, sm.TotalSharedBytes, "127.0.0.1", ts.ListenPort, false, "", "", crypto.CertificateThumbprint);
    hubContext.Clients.All.SendAsync("SelfStatus", self);
};

node.OnFilelistReceived += (peerId, json) => {
    hubContext.Clients.All.SendAsync("FileListReceived", peerId, json);
};

node.OnSearchResultReceived += (res) => {
    hubContext.Clients.All.SendAsync("ReceiveSearchResult", res);
};

node.OnFilelistStatusUpdate += (targetId, status) => {
    hubContext.Clients.All.SendAsync("FilelistStatus", targetId, status);
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
        var self = new PeerInfo("", node.PeerId, node.NodeName, sm.TotalSharedBytes, "127.0.0.1", ts.ListenPort, false, "", "", crypto.CertificateThumbprint);
        hubContext.Clients.All.SendAsync("SelfStatus", self);
    }
};

logger.OnLog += (entry) => {
    hubContext.Clients.All.SendAsync("SystemLog", entry);
};

ts.OnUploadsChanged += (uploads) => {
    hubContext.Clients.All.SendAsync("UploadUpdate", uploads);
};

// Start Global Bandwidth monitor task
_ = Task.Run(async () => {
    while (true)
    {
        bc.TickReport();
        await hubContext.Clients.All.SendAsync("GlobalBandwidthReport", bc.InstantaneousUploadMBps, bc.InstantaneousDownloadMBps);
        await Task.Delay(1000);
    }
});

app.MapControllers();
app.MapFallbackToFile("index.html");
app.Run();
