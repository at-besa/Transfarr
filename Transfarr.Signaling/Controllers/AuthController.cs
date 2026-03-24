using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Transfarr.Shared.Models;
using Transfarr.Signaling.Data;
using BCrypt.Net;
using Transfarr.Signaling.Services;

namespace Transfarr.Signaling.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(UserDatabase db, IConfiguration config, NetworkStateService networkState) : ControllerBase
{
    [HttpPost("register")]
    public IActionResult Register([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
            return BadRequest("Username and Password are required.");

        var existing = db.GetUser(request.Username);
        if (existing != null)
            return BadRequest("User already exists.");

        string hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        
        // First user is Admin, others are Users
        string role = db.GetAllUsernames().Count == 0 ? "Admin" : "User";

        if (db.CreateUser(request.Username, hash, role))
            return Ok(new { Message = "Registration successful" });
        
        return StatusCode(500, "Failed to create user");
    }

    [HttpPost("login")]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var user = db.GetUser(request.Username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Value.Hash))
        {
            networkState.RecordError();
            return Unauthorized(new AuthResponse { Success = false, Error = "Invalid username or password" });
        }

        if (user.Value.IsSuspended)
        {
            return Unauthorized(new AuthResponse { Success = false, Error = "Your account has been suspended by an administrator." });
        }

        networkState.RecordLoginAttempt();
        var token = GenerateJwtToken(request.Username, user.Value.Role);
        return Ok(new AuthResponse 
        { 
            Success = true, 
            Token = token, 
            Username = request.Username 
        });
    }

    private string GenerateJwtToken(string username, string role)
    {
        var jwtKey = config["Jwt:Key"] ?? "TransfarrSuperSecretKey1234567890123456";
        var key = Encoding.ASCII.GetBytes(jwtKey);
        
        var tokenHandler = new JwtSecurityTokenHandler();
        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(new[] 
            { 
                new Claim(ClaimTypes.Name, username),
                new Claim(ClaimTypes.Role, role)
            }),
            Expires = DateTime.UtcNow.AddDays(7),
            SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(key), SecurityAlgorithms.HmacSha256Signature)
        };
        
        var token = tokenHandler.CreateToken(tokenDescriptor);
        return tokenHandler.WriteToken(token);
    }
}
