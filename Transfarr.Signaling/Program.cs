using Transfarr.Signaling.Hubs;
using Transfarr.Signaling.Data;
using Transfarr.Signaling.Services;
using Transfarr.Signaling.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using System.Text;

var builder = WebApplication.CreateBuilder(args);

// Bind Options
builder.Services.Configure<HubOptions>(builder.Configuration.GetSection(HubOptions.SectionName));

// Local Options for Startup
var hubOptions = builder.Configuration.GetSection(HubOptions.SectionName).Get<HubOptions>() ?? new HubOptions();
builder.WebHost.UseUrls(hubOptions.HubUrl);

// Add services to the container.
builder.Services.AddSignalR();
builder.Services.AddControllers();
builder.Services.AddSingleton<UserDatabase>();
builder.Services.AddSingleton<NetworkStateService>();
builder.Services.AddScoped<HubAuthService>();

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Configure JWT
var jwtKey = hubOptions.Jwt.Key;
var key = Encoding.ASCII.GetBytes(jwtKey);

builder.Services.AddAuthentication(x =>
{
    x.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    x.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
})
.AddJwtBearer(x =>
{
    x.RequireHttpsMetadata = false;
    x.SaveToken = true;
    x.TokenValidationParameters = new TokenValidationParameters
    {
        ValidateIssuerSigningKey = true,
        IssuerSigningKey = new SymmetricSecurityKey(key),
        ValidateIssuer = false,
        ValidateAudience = false
    };
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader()
              .AllowAnyMethod()
              .SetIsOriginAllowed(_ => true) // Allow any origin in dev
              .AllowCredentials();
    });
});

var app = builder.Build();

// Configure the HTTP request pipeline.
app.UseCors();
app.UseAntiforgery(); // Required for Blazor Server
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapHub<SignalingHub>("/signaling");
app.MapControllers();
app.MapRazorComponents<Transfarr.Signaling.Components.App>()
    .AddInteractiveServerRenderMode();

app.Run();
