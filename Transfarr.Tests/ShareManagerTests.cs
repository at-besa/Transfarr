using System;
using System.IO;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Transfarr.Node.Core;
using Transfarr.Shared.Models;
using Transfarr.Node.Options;
using Microsoft.Extensions.Options;
using Xunit;

namespace Transfarr.Tests;

public class ShareManagerTests : IDisposable
{
    private readonly string testDir;
    private readonly ShareManager sm;

    public ShareManagerTests()
    {
        testDir = Path.Combine(Path.GetTempPath(), "TransfarrTestShare_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testDir);
        File.WriteAllText(Path.Combine(testDir, "test1.txt"), "hello world");
        
        var subDir = Path.Combine(testDir, "SubFolder");
        Directory.CreateDirectory(subDir);
        File.WriteAllText(Path.Combine(subDir, "test2.txt"), "another file");

        var tempDb = Path.Combine(Path.GetTempPath(), "TransfarrTestDb_" + Guid.NewGuid().ToString("N") + ".db");
        var options = Options.Create(new NodeOptions { 
            Storage = new StorageOptions { DatabasePath = tempDb } 
        });
        var logger = new SystemLogger();
        var db = new ShareDatabase(options);
        db.InitializeDatabase();
        sm = new ShareManager(logger, db, options);
        sm.Initialize();
    }
    [Fact]
    public void ShareManager_Should_Hash_And_Serialize_Correctly()
    {
        sm.AddSharedDirectory("TestShare", testDir);
        
        // Wait for hashing to start and then complete
        Thread.Sleep(200);
        int attempts = 0;
        while (sm.CurrentProgress.IsHashing && attempts++ < 100) Thread.Sleep(100);

        Assert.True(sm.TotalSharedBytes > 0);

        string json = sm.GetLocalFileListJson();
        Assert.False(string.IsNullOrEmpty(json));

        var list = JsonSerializer.Deserialize<FileList>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
        
        Assert.NotNull(list);
        Assert.NotEmpty(list.Items);
        
        var testShare = list.Items.FirstOrDefault(i => i.Name == "TestShare");
        Assert.NotNull(testShare);
        Assert.Contains(testShare.Children, i => i.Name == "test1.txt" && !i.IsDirectory);
        Assert.Contains(testShare.Children, i => i.Name == "SubFolder" && i.IsDirectory);
    }

    public void Dispose()
    {
        sm.Shutdown();
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(testDir))
        {
            try { Directory.Delete(testDir, true); } catch { }
        }
        // Database path is in tempDb, we could delete it too if we tracked it, 
        // but Path.GetTempPath() usually gets cleaned up.
    }
}
