using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.AspNetCore.SignalR;
using Transfarr.Node.Core;
using Transfarr.Node.Hubs;
using Transfarr.Shared;
using Transfarr.Shared.Models;

namespace Transfarr.Node.Services;

public class UpdateService
{
    private readonly SystemLogger _logger;
    private readonly IHubContext<LocalClientHub> _hubContext;
    private readonly HttpClient _httpClient;
    
    private UpdateStatus _status = new();
    private readonly object _statusLock = new();

    public UpdateService(SystemLogger logger, IHubContext<LocalClientHub> hubContext)
    {
        _logger = logger;
        _hubContext = hubContext;
        _httpClient = new HttpClient();
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Transfarr-Node-Updater");
        
        // Initialize basic status
        _status.IsRunningInDocker = Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER") == "true";
    }

    public UpdateStatus GetStatus()
    {
        lock (_statusLock)
        {
            return new UpdateStatus
            {
                IsChecking = _status.IsChecking,
                HasUpdate = _status.HasUpdate,
                LatestVersion = _status.LatestVersion,
                ReleaseNotes = _status.ReleaseNotes,
                DownloadUrl = _status.DownloadUrl,
                IsRunningInDocker = _status.IsRunningInDocker,
                DownloadProgress = _status.DownloadProgress,
                IsInstalling = _status.IsInstalling,
                ErrorMessage = _status.ErrorMessage
            };
        }
    }

    private void UpdateState(Action<UpdateStatus> action)
    {
        lock (_statusLock)
        {
            action(_status);
        }
        // Broadcast to clients via SignalR
        _hubContext.Clients.All.SendAsync("UpdateStatusChanged", GetStatus());
    }

    public async Task<UpdateStatus> CheckForUpdateAsync()
    {
        UpdateState(s => {
            s.IsChecking = true;
            s.ErrorMessage = "";
        });

        try
        {
            _logger.LogInfo("Checking for application updates via GitHub API...");
            var response = await _httpClient.GetStringAsync("https://api.github.com/repos/at-besa/Transfarr/releases/latest");
            using var doc = JsonDocument.Parse(response);
            var root = doc.RootElement;
            
            var tagName = root.GetProperty("tag_name").GetString() ?? "";
            var releaseNotes = root.GetProperty("body").GetString() ?? "";
            
            // Extract clean version (remove 'v' prefix)
            var cleanTagName = tagName.TrimStart('v');
            var currentVersion = AppVersion.Current.TrimStart('v');

            if (Version.TryParse(cleanTagName, out var latest) && Version.TryParse(currentVersion, out var current))
            {
                if (latest > current)
                {
                    _logger.LogInfo($"New version {tagName} found! (Current: {AppVersion.Current})");
                    
                    // Look for matching asset
                    string downloadUrl = "";
                    if (!_status.IsRunningInDocker)
                    {
                        var assets = root.GetProperty("assets");
                        var targetAssetPattern = GetTargetAssetPattern();
                        
                        foreach (var asset in assets.EnumerateArray())
                        {
                            var assetName = asset.GetProperty("name").GetString() ?? "";
                            if (assetName.Contains(targetAssetPattern, StringComparison.OrdinalIgnoreCase))
                            {
                                downloadUrl = asset.GetProperty("browser_download_url").GetString() ?? "";
                                break;
                            }
                        }
                    }

                    UpdateState(s => {
                        s.IsChecking = false;
                        s.HasUpdate = true;
                        s.LatestVersion = tagName;
                        s.ReleaseNotes = releaseNotes;
                        s.DownloadUrl = downloadUrl;
                    });
                }
                else
                {
                    _logger.LogInfo($"Transfarr is up to date (Version: {AppVersion.Current})");
                    UpdateState(s => {
                        s.IsChecking = false;
                        s.HasUpdate = false;
                        s.LatestVersion = tagName;
                        s.ReleaseNotes = "";
                        s.DownloadUrl = "";
                    });
                }
            }
            else
            {
                throw new Exception($"Failed to parse version strings: Latest '{tagName}', Current '{AppVersion.Current}'");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Update check failed: {ex.Message}");
            UpdateState(s => {
                s.IsChecking = false;
                s.ErrorMessage = $"Error checking for updates: {ex.Message}";
            });
        }

        return GetStatus();
    }

    private string GetTargetAssetPattern()
    {
        // Guess runtime assets based on current OS and architecture
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win" :
                     RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux" :
                     RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx" : "unknown";
                     
        string arch = RuntimeInformation.ProcessArchitecture == Architecture.X64 ? "x64" :
                      RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "arm64" : "x86";

        return $"{os}-{arch}";
    }

    public async Task InstallUpdateAsync()
    {
        var status = GetStatus();
        if (!status.HasUpdate || string.IsNullOrEmpty(status.DownloadUrl))
        {
            _logger.LogWarning("No update available or download URL is missing.");
            return;
        }

        if (status.IsRunningInDocker)
        {
            _logger.LogWarning("Application is running inside Docker. Auto-update is disabled.");
            return;
        }

        UpdateState(s => {
            s.IsInstalling = true;
            s.DownloadProgress = 0;
            s.ErrorMessage = "";
        });

        try
        {
            _logger.LogInfo($"Downloading update from: {status.DownloadUrl}");
            
            // Get temporary file path
            var tempFile = Path.Combine(Path.GetTempPath(), $"transfarr_update_{Guid.NewGuid()}.zip");
            
            // Download file with progress reporting
            using (var response = await _httpClient.GetAsync(status.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                
                using (var contentStream = await response.Content.ReadAsStreamAsync())
                using (var fileStream = new FileStream(tempFile, FileMode.Create, FileAccess.Write, FileShare.None, 8192, true))
                {
                    var buffer = new byte[8192];
                    var totalRead = 0L;
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer, 0, buffer.Length)) > 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, read);
                        totalRead += read;
                        if (totalBytes != -1)
                        {
                            var progress = (int)((double)totalRead / totalBytes * 100);
                            UpdateState(s => s.DownloadProgress = progress);
                        }
                    }
                }
            }

            _logger.LogInfo("Download finished. Preparing update extraction...");
            var appDir = AppDomain.CurrentDomain.BaseDirectory;
            var tempExtractDir = Path.Combine(Path.GetTempPath(), $"transfarr_extract_{Guid.NewGuid()}");
            Directory.CreateDirectory(tempExtractDir);
            
            // Extract ZIP
            ZipFile.ExtractToDirectory(tempFile, tempExtractDir);
            
            // Cleanup temp zip file
            File.Delete(tempFile);

            _logger.LogInfo("Update extracted. Preparing background installer execution...");
            
            // Generate update script
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                await InstallWindowsUpdate(appDir, tempExtractDir);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux) || RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                await InstallUnixUpdate(appDir, tempExtractDir);
            }
            else
            {
                throw new NotSupportedException("Operating system not supported for auto-updates.");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError($"Update installation failed: {ex.Message}");
            UpdateState(s => {
                s.IsInstalling = false;
                s.ErrorMessage = $"Failed to install update: {ex.Message}";
            });
        }
    }

    private async Task InstallWindowsUpdate(string appDir, string tempExtractDir)
    {
        var batPath = Path.Combine(Path.GetTempPath(), $"transfarr_updater_{Guid.NewGuid()}.bat");
        
        // Find current executable file name (usually Transfarr.Node.exe or dotnet run parent)
        var exeName = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "Transfarr.Node.exe");
        var isDotnetDll = exeName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (isDotnetDll)
        {
            exeName = "Transfarr.Node.exe";
        }

        // Script copies files, waits for process exit, overwrites and restarts the node
        var script = $@"
@echo off
echo Waiting for Transfarr Node to close...
timeout /t 2 /nobreak > nul

echo Overwriting files...
xcopy ""{tempExtractDir}\*"" ""{appDir}"" /y /e /h /k

echo Starting new version...
cd /d ""{appDir}""
start """" ""{appDir}\{exeName}""

echo Cleaning up temp files...
rd /s /q ""{tempExtractDir}""
del ""%~f0""
";

        await File.WriteAllTextAsync(batPath, script);
        _logger.LogWarning($"Shutting down and running update script: {batPath}");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            CreateNoWindow = true,
            UseShellExecute = true
        };
        
        System.Diagnostics.Process.Start(startInfo);
        
        // Graceful exit
        Environment.Exit(0);
    }

    private async Task InstallUnixUpdate(string appDir, string tempExtractDir)
    {
        var shPath = Path.Combine(Path.GetTempPath(), $"transfarr_updater_{Guid.NewGuid()}.sh");
        var exeName = Path.GetFileName(System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName ?? "Transfarr.Node");
        var isDotnetDll = exeName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (isDotnetDll)
        {
            exeName = "Transfarr.Node";
        }

        var script = $@"#!/bin/sh
echo ""Waiting for Transfarr Node to close...""
sleep 2

echo ""Overwriting files...""
cp -R ""{tempExtractDir}/""* ""{appDir}/""

echo ""Setting executable permissions...""
chmod +x ""{appDir}/{exeName}""

echo ""Starting new version...""
cd ""{appDir}""
""./{exeName}"" &

echo ""Cleaning up temp files...""
rm -rf ""{tempExtractDir}""
rm -- ""$0""
";

        await File.WriteAllTextAsync(shPath, script);
        
        // Make script executable
        var chmodProcess = System.Diagnostics.Process.Start("chmod", $"+x \"{shPath}\"");
        await chmodProcess.WaitForExitAsync();

        _logger.LogWarning($"Shutting down and running update script: {shPath}");

        var startInfo = new System.Diagnostics.ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = $"\"{shPath}\"",
            CreateNoWindow = true,
            UseShellExecute = false
        };
        
        System.Diagnostics.Process.Start(startInfo);
        
        // Graceful exit
        Environment.Exit(0);
    }
}
