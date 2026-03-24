using System;
using System.IO;
using System.Text.Json;
using Transfarr.Node.Core;
using Transfarr.Shared.Models;
using Xunit;

namespace Transfarr.Tests;

public class ShareManagerTests : IDisposable
{
    private readonly string _testDir;
    private readonly ShareManager _sm;

    public ShareManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), "TransfarrTestShare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_testDir);
        File.WriteAllText(Path.Combine(_testDir, "test1.txt"), "hello world");
        
        var subDir = Path.Combine(_testDir, "SubFolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "test2.txt"), "another file");

        _sm = new ShareManager();
    }

    [Fact]
    public void ShareManager_Should_Hash_And_Serialize_Correctly()
    {
        _sm.AddSharedDirectory("TestShare", _testDir);
        
        Assert.True(_sm.TotalSharedBytes > 0);

        string json = _sm.GetLocalFileListJson();
        Assert.False(string.IsNullOrEmpty(json));

        var list = JsonSerializer.Deserialize<FileList>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        Assert.NotNull(list);
        Assert.NotEmpty(list.Items);
        Assert.Contains(list.Items, i => i.Name == "test1.txt" && !i.IsDirectory);
        Assert.Contains(list.Items, i => i.Name == "SubFolder" && i.IsDirectory);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testDir))
            Directory.Delete(_testDir, true);
    }
}
