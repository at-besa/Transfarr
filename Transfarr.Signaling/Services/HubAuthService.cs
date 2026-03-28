using System;

namespace Transfarr.Signaling.Services;

/// <summary>
/// Manages the authentication state for the Hub's administrative UI.
/// This is a scoped service, providing a unique state per browser session.
/// </summary>
public class HubAuthService
{
    public bool IsAuthenticated { get; private set; }
    public string? Username { get; private set; }
    public string? Role { get; private set; }

    public void Login(string username, string role)
    {
        IsAuthenticated = true;
        Username = username;
        Role = role;
    }

    public void Logout()
    {
        IsAuthenticated = false;
        Username = null;
        Role = null;
    }
}
