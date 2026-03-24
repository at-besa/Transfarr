namespace Transfarr.Shared.Models;

public class LoginRequest
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

public class AuthResponse
{
    public bool Success { get; set; }
    public string Token { get; set; } = "";
    public string Error { get; set; } = "";
    public string Username { get; set; } = "";
}
