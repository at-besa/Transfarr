using System;

namespace Transfarr.Signaling.Options;

public class HubOptions
{
    public const string SectionName = "Transfarr";

    public string HubUrl { get; set; } = "http://0.0.0.0:5135";
    public DatabaseOptions Database { get; set; } = new();
    public AdminUserOptions AdminUser { get; set; } = new();
    public JwtOptions Jwt { get; set; } = new();
}

public class DatabaseOptions
{
    public string Path { get; set; } = "users.db";
}

public class AdminUserOptions
{
    public string Username { get; set; } = "admin";
    public string Password { get; set; } = "";
}

public class JwtOptions
{
    public string Key { get; set; } = "TransfarrSuperSecretKey1234567890123456";
}
