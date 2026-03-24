namespace Transfarr.Shared.Models;

public record FileMetadata(string Name, long Size, string Tth, bool IsDirectory = false, string Path = "");
