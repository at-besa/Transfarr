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
