using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using Transfarr.Shared.Models;

namespace Transfarr.Signaling.Data;

public class UserDatabase
{
    private readonly string _connectionString;
    private readonly object _dbLock = new();

    public UserDatabase(IConfiguration config)
    {
        string dbPath = config.GetValue<string>("Database:Path") ?? "users.db";
        _connectionString = $"Data Source={dbPath}";
        InitializeDatabase();
        SeedAdminUser(config);
    }

    private void SeedAdminUser(IConfiguration config)
    {
        var adminUser = config["AdminUser:Username"];
        var adminPass = config["AdminUser:Password"];
        
        if (!string.IsNullOrEmpty(adminUser) && !string.IsNullOrEmpty(adminPass))
        {
            if (GetUser(adminUser) == null)
            {
                string hash = BCrypt.Net.BCrypt.HashPassword(adminPass);
                CreateUser(adminUser, hash, "Admin");
            }
        }
    }

    private void InitializeDatabase()
    {
        lock (_dbLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            
            var command = connection.CreateCommand();
            command.CommandText = @"
                CREATE TABLE IF NOT EXISTS Users (
                    Username TEXT PRIMARY KEY,
                    PasswordHash TEXT NOT NULL,
                    Role TEXT NOT NULL,
                    Reputation INTEGER DEFAULT 0,
                    CreatedAt DATETIME DEFAULT CURRENT_TIMESTAMP
                );
            ";
            command.ExecuteNonQuery();
        }
    }

    public bool CreateUser(string username, string passwordHash, string role = "User")
    {
        lock (_dbLock)
        {
            try 
            {
                using var connection = new SqliteConnection(_connectionString);
                connection.Open();
                var cmd = connection.CreateCommand();
                cmd.CommandText = "INSERT INTO Users (Username, PasswordHash, Role) VALUES (@u, @p, @r)";
                cmd.Parameters.AddWithValue("@u", username);
                cmd.Parameters.AddWithValue("@p", passwordHash);
                cmd.Parameters.AddWithValue("@r", role);
                return cmd.ExecuteNonQuery() > 0;
            }
            catch { return false; }
        }
    }

    public (string Hash, string Role, int Reputation)? GetUser(string username)
    {
        lock (_dbLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            var cmd = connection.CreateCommand();
            cmd.CommandText = "SELECT PasswordHash, Role, Reputation FROM Users WHERE Username = @u";
            cmd.Parameters.AddWithValue("@u", username);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                return (reader.GetString(0), reader.GetString(1), reader.GetInt32(2));
            }
            return null;
        }
    }

    public List<string> GetAllUsernames()
    {
        lock (_dbLock)
        {
            var list = new List<string>();
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = new SqliteCommand("SELECT Username FROM Users", connection);
            using var reader = command.ExecuteReader();
            while (reader.Read()) list.Add(reader.GetString(0));
            return list;
        }
    }

    public void UpdateReputation(string username, int delta)
    {
        lock (_dbLock)
        {
            using var connection = new SqliteConnection(_connectionString);
            connection.Open();
            using var command = new SqliteCommand("UPDATE Users SET Reputation = Reputation + @delta WHERE Username = @u", connection);
            command.Parameters.AddWithValue("@delta", delta);
            command.Parameters.AddWithValue("@u", username);
            command.ExecuteNonQuery();
        }
    }
}
