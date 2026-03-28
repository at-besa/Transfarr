namespace Transfarr.Shared.Models;

public class AuthResponse
{
    public bool Success { get; set; }
    public string Token { get; set; } = "";
    public string Error { get; set; } = "";
    public string Username { get; set; } = "";
}
