using System;
using System.Collections.Generic;
using Microsoft.Data.Sqlite;
using System.IO;
using Microsoft.Extensions.Options;
using Transfarr.Node.Options;

namespace Transfarr.Node.Core;

public class ShareDatabase
{
    private readonly string connectionString;
    private readonly object dbLock = new();
    private readonly NodeOptions options;

    public ShareDatabase(IOptions<NodeOptions> options)
    {
        this.options = options.Value;
        connectionString = $"Data Source={Path.Combine(AppDomain.CurrentDomain.BaseDirectory, this.options.Storage.DatabasePath)}";
    }

    static ShareDatabase()
    {
        // Initializing logic if needed, but we call InitializeDatabase in instance.
    }

    // Since we used an empty primary constructor, we can still have initialization logic.
    // However, C# 12 primary constructors on classes without parameters are just syntactic sugar.
    // I'll keep it simple for now as it has no DI dependencies.

    public void InitializeDatabase()
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();

            var createDirectoriesTable = @"
                CREATE TABLE IF NOT EXISTS Directories (
                    VirtualName TEXT PRIMARY KEY,
                    Path TEXT NOT NULL
                );";
            using (var command = new SqliteCommand(createDirectoriesTable, connection))
                command.ExecuteNonQuery();

            // Schema Migration: Check if we need to upgrade HashCache
            bool needsRecreate = false;
            using (var cmd = new SqliteCommand("PRAGMA table_info(HashCache)", connection))
            using (var reader = cmd.ExecuteReader())
            {
                bool hasNewColumn = false;
                while (reader.Read())
                {
                    if (reader.GetString(1) == "LastWriteTimeTicks") hasNewColumn = true;
                }
                // If table exists but has no new column, we must recreate
                if (!hasNewColumn && reader.HasRows) needsRecreate = true;
            }

            if (needsRecreate)
            {
                using (var cmd = new SqliteCommand("DROP TABLE HashCache", connection))
                    cmd.ExecuteNonQuery();
            }

            var createHashCacheTable = @"
                CREATE TABLE IF NOT EXISTS HashCache (
                    FilePath TEXT PRIMARY KEY,
                    Size INTEGER NOT NULL,
                    LastWriteTimeTicks INTEGER NOT NULL,
                    Tth TEXT NOT NULL
                );";
            using (var command = new SqliteCommand(createHashCacheTable, connection))
                command.ExecuteNonQuery();

            var createSettingsTable = @"
                CREATE TABLE IF NOT EXISTS Settings (
                    Key TEXT PRIMARY KEY,
                    Value TEXT NOT NULL
                );";
            using (var command = new SqliteCommand(createSettingsTable, connection))
                command.ExecuteNonQuery();

            var createFavoritesTable = @"
                CREATE TABLE IF NOT EXISTS HubFavorites (
                    Url TEXT PRIMARY KEY,
                    Name TEXT NOT NULL
                );";
            using (var command = new SqliteCommand(createFavoritesTable, connection))
                command.ExecuteNonQuery();

            var createQueueTable = @"
                CREATE TABLE IF NOT EXISTS DownloadQueue (
                    Id TEXT PRIMARY KEY,
                    TargetPeerId TEXT NOT NULL,
                    TargetPeerName TEXT NOT NULL,
                    FileName TEXT NOT NULL,
                    FileSize INTEGER NOT NULL,
                    Tth TEXT NOT NULL,
                    Status TEXT NOT NULL,
                    BytesDownloaded INTEGER NOT NULL,
                    RelativePath TEXT NOT NULL,
                    Bitfield TEXT DEFAULT '',
                    DiscoveredPeers TEXT DEFAULT ''
                );";
            using (var command = new SqliteCommand(createQueueTable, connection))
                command.ExecuteNonQuery();

            // Migration: Add columns if they don't exist
            using (var cmd = new SqliteCommand("PRAGMA table_info(DownloadQueue)", connection))
            using (var reader = cmd.ExecuteReader())
            {
                bool hasBitfield = false;
                bool hasPeers = false;
                while (reader.Read())
                {
                    var colName = reader.GetString(1);
                    if (colName == "Bitfield") hasBitfield = true;
                    if (colName == "DiscoveredPeers") hasPeers = true;
                }
                if (!hasBitfield)
                {
                    using var alter = new SqliteCommand("ALTER TABLE DownloadQueue ADD COLUMN Bitfield TEXT DEFAULT ''", connection);
                    alter.ExecuteNonQuery();
                }
                if (!hasPeers)
                {
                    using var alter = new SqliteCommand("ALTER TABLE DownloadQueue ADD COLUMN DiscoveredPeers TEXT DEFAULT ''", connection);
                    alter.ExecuteNonQuery();
                }
            }

            var createFileIndexTable = @"
                CREATE VIRTUAL TABLE IF NOT EXISTS FileIndex USING fts5(
                    FileName,
                    VirtualPath UNINDEXED,
                    Size UNINDEXED,
                    Tth UNINDEXED,
                    IsDirectory UNINDEXED,
                    tokenize='unicode61'
                );";
            using (var command = new SqliteCommand(createFileIndexTable, connection))
                command.ExecuteNonQuery();
        }
    }

    public string? GetSetting(string key)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT Value FROM Settings WHERE Key = @k", connection);
            command.Parameters.AddWithValue("@k", key);
            return command.ExecuteScalar()?.ToString();
        }
    }

    public void SaveSetting(string key, string value)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var sql = "INSERT INTO Settings (Key, Value) VALUES (@k, @v) ON CONFLICT(Key) DO UPDATE SET Value = @v";
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@k", key);
            command.Parameters.AddWithValue("@v", value);
            command.ExecuteNonQuery();
        }
    }

    public Dictionary<string, string> GetDirectories()
    {
        lock (dbLock)
        {
            var result = new Dictionary<string, string>();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT VirtualName, Path FROM Directories", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = reader.GetString(1);
            }
            return result;
        }
    }

    public void SaveDirectories(Dictionary<string, string> directories)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();

            using (var cmd = new SqliteCommand("DELETE FROM Directories", connection, transaction))
                cmd.ExecuteNonQuery();

            foreach (var kvp in directories)
            {
                using var cmd = new SqliteCommand("INSERT INTO Directories (VirtualName, Path) VALUES (@v, @p)", connection, transaction);
                cmd.Parameters.AddWithValue("@v", kvp.Key);
                cmd.Parameters.AddWithValue("@p", kvp.Value);
                cmd.ExecuteNonQuery();
            }
            transaction.Commit();
        }
    }

    public Dictionary<string, (long Size, DateTime LastWriteTimeUtc, string Tth)> GetHashCache()
    {
        lock (dbLock)
        {
            var result = new Dictionary<string, (long, DateTime, string)>();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT FilePath, Size, LastWriteTimeTicks, Tth FROM HashCache", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result[reader.GetString(0)] = (
                    reader.GetInt64(1),
                    new DateTime(reader.GetInt64(2), DateTimeKind.Utc),
                    reader.GetString(3)
                );
            }
            return result;
        }
    }

    public void UpsertHashCache(string filePath, long size, DateTime lastWriteTimeUtc, string tth)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var sql = @"
                INSERT INTO HashCache (FilePath, Size, LastWriteTimeTicks, Tth) 
                VALUES (@f, @s, @l, @t) 
                ON CONFLICT(FilePath) DO UPDATE SET 
                Size=excluded.Size, LastWriteTimeTicks=excluded.LastWriteTimeTicks, Tth=excluded.Tth;";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@f", filePath);
            cmd.Parameters.AddWithValue("@s", size);
            cmd.Parameters.AddWithValue("@l", lastWriteTimeUtc.Ticks);
            cmd.Parameters.AddWithValue("@t", tth);
            cmd.ExecuteNonQuery();
        }
    }

    public void CleanupHashCache(HashSet<string> validPaths)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var existing = new List<string>();
            using (var cmd = new SqliteCommand("SELECT FilePath FROM HashCache", connection))
            using (var reader = cmd.ExecuteReader())
            {
                while(reader.Read()) existing.Add(reader.GetString(0));
            }
            
            using var transaction = connection.BeginTransaction();
            using var deleteCmd = new SqliteCommand("DELETE FROM HashCache WHERE FilePath = @path", connection, transaction);
            var p = deleteCmd.Parameters.Add("@path", SqliteType.Text);
            
            int removedFiles = 0;
            foreach (var path in existing)
            {
                if (!validPaths.Contains(path))
                {
                    p.Value = path;
                    deleteCmd.ExecuteNonQuery();
                    removedFiles++;
                }
            }
            transaction.Commit();

            if (removedFiles > 0)
            {
                using var vacuumCmd = new SqliteCommand("VACUUM", connection);
                vacuumCmd.ExecuteNonQuery();
            }
        }
    }
    public List<(string Url, string Name)> GetHubFavorites()
    {
        lock (dbLock)
        {
            var result = new List<(string, string)>();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT Url, Name FROM HubFavorites", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add((reader.GetString(0), reader.GetString(1)));
            }
            return result;
        }
    }

    public void AddHubFavorite(string url, string name)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var sql = "INSERT INTO HubFavorites (Url, Name) VALUES (@u, @n) ON CONFLICT(Url) DO UPDATE SET Name = @n";
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@u", url);
            command.Parameters.AddWithValue("@n", name);
            command.ExecuteNonQuery();
        }
    }

    public void RemoveHubFavorite(string url)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("DELETE FROM HubFavorites WHERE Url = @u", connection);
            command.Parameters.AddWithValue("@u", url);
            command.ExecuteNonQuery();
        }
    }

    public List<Transfarr.Shared.Models.DownloadItem> GetDownloadQueue()
    {
        lock (dbLock)
        {
            var result = new List<Transfarr.Shared.Models.DownloadItem>();
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT Id, TargetPeerId, TargetPeerName, FileName, FileSize, Tth, Status, BytesDownloaded, RelativePath, Bitfield, DiscoveredPeers FROM DownloadQueue", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                result.Add(new Transfarr.Shared.Models.DownloadItem {
                    Id = reader.GetString(0),
                    TargetPeerId = reader.GetString(1),
                    // We re-create a dummy PeerInfo, but will try to match real Peer in DownloadManager
                    TargetPeer = new Transfarr.Shared.Models.PeerInfo("", reader.GetString(1), reader.GetString(2), 0, "", 0),
                    FileName = reader.GetString(3),
                    FileSize = reader.GetInt64(4),
                    Tth = reader.GetString(5),
                    Status = reader.GetString(6),
                    BytesDownloaded = reader.GetInt64(7),
                    RelativePath = reader.GetString(8),
                    Bitfield = reader.IsDBNull(9) ? "" : reader.GetString(9),
                    DiscoveredPeers = (reader.IsDBNull(10) ? "" : reader.GetString(10)).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList()
                });
            }
            return result;
        }
    }

    public void UpsertDownloadItem(Transfarr.Shared.Models.DownloadItem item)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var sql = @"
                INSERT INTO DownloadQueue (Id, TargetPeerId, TargetPeerName, FileName, FileSize, Tth, Status, BytesDownloaded, RelativePath, Bitfield, DiscoveredPeers)
                VALUES (@id, @pid, @pname, @fname, @fsize, @tth, @status, @bytes, @rel, @bit, @peers)
                ON CONFLICT(Id) DO UPDATE SET 
                Status=excluded.Status, BytesDownloaded=excluded.BytesDownloaded, Bitfield=excluded.Bitfield, DiscoveredPeers=excluded.DiscoveredPeers, FileName=excluded.FileName;";
            using var command = new SqliteCommand(sql, connection);
            command.Parameters.AddWithValue("@id", item.Id);
            command.Parameters.AddWithValue("@pid", item.TargetPeerId);
            command.Parameters.AddWithValue("@pname", item.TargetPeer?.Name ?? "Unknown");
            command.Parameters.AddWithValue("@fname", item.FileName);
            command.Parameters.AddWithValue("@fsize", item.FileSize);
            command.Parameters.AddWithValue("@tth", item.Tth);
            command.Parameters.AddWithValue("@status", item.Status);
            command.Parameters.AddWithValue("@bytes", item.BytesDownloaded);
            command.Parameters.AddWithValue("@rel", item.RelativePath);
            command.Parameters.AddWithValue("@bit", item.Bitfield ?? "");
            command.Parameters.AddWithValue("@peers", string.Join(',', item.DiscoveredPeers));
            command.ExecuteNonQuery();
        }
    }

    public void RemoveDownloadItem(string id)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var command = new SqliteCommand("DELETE FROM DownloadQueue WHERE Id = @id", connection);
            command.Parameters.AddWithValue("@id", id);
            command.ExecuteNonQuery();
        }
    }

    public void RebuildSearchIndex(IEnumerable<Transfarr.Shared.Models.FileMetadata> files)
    {
        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            using var transaction = connection.BeginTransaction();
            
            using (var cmd = new SqliteCommand("DELETE FROM FileIndex", connection, transaction))
                cmd.ExecuteNonQuery();

            var sql = "INSERT INTO FileIndex (FileName, VirtualPath, Size, Tth, IsDirectory) VALUES (@fn, @vp, @s, @t, @d)";
            using var insertCmd = new SqliteCommand(sql, connection, transaction);
            var fnParam = insertCmd.Parameters.Add("@fn", SqliteType.Text);
            var vpParam = insertCmd.Parameters.Add("@vp", SqliteType.Text);
            var sParam = insertCmd.Parameters.Add("@s", SqliteType.Integer);
            var tParam = insertCmd.Parameters.Add("@t", SqliteType.Text);
            var dParam = insertCmd.Parameters.Add("@d", SqliteType.Integer);

            foreach (var f in files)
            {
                fnParam.Value = f.Name;
                vpParam.Value = f.Path;
                sParam.Value = f.Size;
                tParam.Value = f.Tth ?? "";
                dParam.Value = f.IsDirectory ? 1 : 0;
                insertCmd.ExecuteNonQuery();
            }
            
            transaction.Commit();
        }
    }

    public List<Transfarr.Shared.Models.FileMetadata> SearchFiles(string rawQuery, int limit = 100)
    {
        var results = new List<Transfarr.Shared.Models.FileMetadata>();
        if (string.IsNullOrWhiteSpace(rawQuery)) return results;

        var sanitized = rawQuery.Replace("\"", "").Replace("'", "").Trim();
        if (string.IsNullOrEmpty(sanitized)) return results;
        
        var queryParts = sanitized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var ftsQuery = string.Join(" AND ", System.Linq.Enumerable.Select(queryParts, p => $"\"{p}\"*"));

        lock (dbLock)
        {
            using var connection = new SqliteConnection(connectionString);
            connection.Open();
            var sql = @"
                SELECT FileName, VirtualPath, Size, Tth, IsDirectory 
                FROM FileIndex 
                WHERE FileIndex MATCH @q 
                ORDER BY rank 
                LIMIT @l";
            using var cmd = new SqliteCommand(sql, connection);
            cmd.Parameters.AddWithValue("@q", ftsQuery);
            cmd.Parameters.AddWithValue("@l", limit);
            
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new Transfarr.Shared.Models.FileMetadata(
                    reader.GetString(0),
                    reader.GetInt64(2),
                    reader.GetString(3),
                    reader.GetInt32(4) == 1,
                    reader.GetString(1)
                ));
            }
        }
        return results;
    }
}
