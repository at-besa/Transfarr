using System.Collections.Generic;

namespace Transfarr.Shared.Models;

public class FileListItem
{
    public string Name { get; set; } = "";
    public bool IsDirectory { get; set; }
    public long Size { get; set; }
    public string Tth { get; set; } = "";
    public List<FileListItem> Children { get; set; } = new();
}
