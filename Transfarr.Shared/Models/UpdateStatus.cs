namespace Transfarr.Shared.Models;

public class UpdateStatus
{
    public bool IsChecking { get; set; }
    public bool HasUpdate { get; set; }
    public string LatestVersion { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
    public string DownloadUrl { get; set; } = "";
    public bool IsRunningInDocker { get; set; }
    public int DownloadProgress { get; set; }
    public bool IsInstalling { get; set; }
    public string ErrorMessage { get; set; } = "";
}
