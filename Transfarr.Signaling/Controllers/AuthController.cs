using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Transfarr.Shared.Models;
using Transfarr.Signaling.Data;
using BCrypt.Net;
using Transfarr.Signaling.Services;

using Transfarr.Signaling.Options;
using Microsoft.Extensions.Options;

namespace Transfarr.Signaling.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController(UserDatabase db, IOptions<HubOptions> options, NetworkStateService networkState, ILogger<AuthController> logger) : ControllerBase
{
    private readonly HubOptions options = options.Value;

    /// <summary>
    /// Registers a new user in the signaling database.
    /// </summary>
    /// <param name="request">Registration credentials.</param>
    /// <returns>A status message indicating success or failure.</returns>
    /// <response code="200">User created successfully.</response>
    /// <response code="400">Invalid input or user already exists.</response>
    /// <response code="500">Unexpected server error.</response>
    [HttpPost("register")]
    [ProducesResponseType(typeof(object), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status500InternalServerError)]
    public IActionResult Register([FromBody] LoginRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Username) || string.IsNullOrWhiteSpace(request.Password))
        {
            logger.LogWarning("Registration failed: Missing Username or Password attempt from IP {IP}", HttpContext.Connection.RemoteIpAddress);
            return BadRequest("Username and Password are required.");
        }

        var existing = db.GetUser(request.Username);
        if (existing != null)
        {
            logger.LogWarning("Registration failed: User '{Username}' already exists. IP: {IP}", request.Username, HttpContext.Connection.RemoteIpAddress);
            return BadRequest("User already exists.");
        }

        string hash = BCrypt.Net.BCrypt.HashPassword(request.Password);
        
        // First user is Admin, others are Users
        string role = db.GetAllUsernames().Count == 0 ? "Admin" : "User";

        if (db.CreateUser(request.Username, hash, role))
        {
            logger.LogInformation("Registration successful: New user '{Username}' created with role '{Role}' from IP: {IP}", request.Username, role, HttpContext.Connection.RemoteIpAddress);
            return Ok(new { Message = "Registration successful" });
        }
        
        logger.LogError("Registration failed: Database constraint or unknown error while creating '{Username}'.", request.Username);
        return StatusCode(500, "Failed to create user");
    }

    /// <summary>
    /// Authenticates a user and generates a JWT token.
    /// </summary>
    /// <param name="request">Login credentials.</param>
    /// <returns>An authorization response containing the JWT token.</returns>
    /// <response code="200">Authentication successful.</response>
    /// <response code="401">Invalid credentials or account suspended.</response>
    [HttpPost("login")]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(AuthResponse), StatusCodes.Status401Unauthorized)]
    public IActionResult Login([FromBody] LoginRequest request)
    {
        var user = db.GetUser(request.Username);
        if (user == null || !BCrypt.Net.BCrypt.Verify(request.Password, user.Value.Hash))
        {
            logger.LogWarning("Login failed: Invalid credentials for user '{Username}' from IP: {IP}", request.Username, HttpContext.Connection.RemoteIpAddress);
            networkState.RecordError();
            return Unauthorized(new AuthResponse { Success = false, Error = "Invalid username or password" });
        }

        if (user.Value.IsSuspended)
        {
            logger.LogWarning("Login failed: Suspended account '{Username}' attempted login from IP: {IP}", request.Username, HttpContext.Connection.RemoteIpAddress);
            return Unauthorized(new AuthResponse { Success = false, Error = "Your account has been suspended by an administrator." });
        }

        logger.LogInformation("Login successful: '{Username}' authenticated successfully from IP: {IP}", request.Username, HttpContext.Connection.RemoteIpAddress);
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
        var jwtKey = options.Jwt.Key;
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
