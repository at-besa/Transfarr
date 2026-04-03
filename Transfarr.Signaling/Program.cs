using Transfarr.Signaling.Hubs;
using Transfarr.Signaling.Data;
using Transfarr.Signaling.Services;
using Transfarr.Signaling.Options;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
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

// API Documentation & Exception Handling
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
	c.SwaggerDoc("v1", new Microsoft.OpenApi.OpenApiInfo 
	{ 
		Title = "Transfarr Signaling API", 
		Version = "v1",
		Description = "Central signaling and authentication service for the Transfarr P2P network."
	});
	
	c.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.OpenApiSecurityScheme
	{
		Description = "JWT Authorization header using the Bearer scheme. Enter 'Bearer' [space] and then your token. Example: 'Bearer 12345abcdef'",
		Name = "Authorization",
		In = Microsoft.OpenApi.ParameterLocation.Header,
		Type = Microsoft.OpenApi.SecuritySchemeType.ApiKey,
		Scheme = "Bearer"
	});

	c.AddSecurityRequirement(_ => new Microsoft.OpenApi.OpenApiSecurityRequirement
	{
		{
			new Microsoft.OpenApi.OpenApiSecuritySchemeReference("Bearer", null, null),
			new List<string>()
		}
	});

	var xmlFile = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
	var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
	if (File.Exists(xmlPath))
	{
		c.IncludeXmlComments(xmlPath);
	}

	c.AddSignalRSwaggerGen();
});

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

    x.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            var path = context.HttpContext.Request.Path;
            if (!string.IsNullOrEmpty(accessToken) && path.StartsWithSegments("/signaling"))
            {
                context.Token = accessToken;
            }
            return Task.CompletedTask;
        },
        OnAuthenticationFailed = context =>
        {
            var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
            logger.LogWarning("Authentication failed for request from: {IP}. Reason: {Exception}", context.HttpContext.Connection.RemoteIpAddress, context.Exception.Message);
            return Task.CompletedTask;
        },
        OnChallenge = context =>
        {
            if (context.AuthenticateFailure != null)
            {
                var logger = context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>();
                logger.LogWarning("Authentication challenge triggered from: {IP}. Failure: {FailureInfo}", context.HttpContext.Connection.RemoteIpAddress, context.AuthenticateFailure.Message);
            }
            return Task.CompletedTask;
        }
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
app.UseExceptionHandler(); // Uses Problem Details automatically
app.UseStatusCodePages(); // For static status code responses

if (app.Environment.IsDevelopment())
{
	app.UseSwagger();
	app.UseSwaggerUI(c =>
	{
		c.SwaggerEndpoint("/swagger/v1/swagger.json", "Transfarr Signaling API v1");
		c.RoutePrefix = "swagger"; // Standard URL: /swagger
	});
}

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
