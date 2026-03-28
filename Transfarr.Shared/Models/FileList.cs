using System.Collections.Generic;

namespace Transfarr.Shared.Models;

public class FileList
{
    public string Generator { get; set; } = "Transfarr 1.0";
    public List<FileListItem> Items { get; set; } = new();
}
