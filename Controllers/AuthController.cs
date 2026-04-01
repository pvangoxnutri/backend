using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;
    private readonly GoogleTokenVerifier _googleTokenVerifier;

    public AuthController(AppDbContext db, IConfiguration config, GoogleTokenVerifier googleTokenVerifier)
    {
        _db = db;
        _config = config;
        _googleTokenVerifier = googleTokenVerifier;
    }

    [HttpPost("register")]
    public async Task<ActionResult<AuthResponseDto>> Register([FromBody] RegisterDto dto)
    {
        if (await _db.Users.AnyAsync(u => u.Email == dto.Email))
            return Conflict("A user with this email already exists.");

        var user = new User
        {
            Name = dto.Name,
            Email = dto.Email,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.Password)
        };

        _db.Users.Add(user);
        await _db.SaveChangesAsync();

        return Ok(new AuthResponseDto
        {
            Token = GenerateToken(user),
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            Role = user.Role
        });
    }

    [HttpPost("login")]
    public async Task<ActionResult<AuthResponseDto>> Login([FromBody] LoginDto dto)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Email == dto.Email);

        if (user == null)
            return Unauthorized("Invalid email or password.");

        if (string.IsNullOrWhiteSpace(user.PasswordHash))
            return Unauthorized("This account uses Google sign-in.");

        if (!BCrypt.Net.BCrypt.Verify(dto.Password, user.PasswordHash))
            return Unauthorized("Invalid email or password.");

        return Ok(CreateAuthResponse(user));
    }

    [HttpPost("google")]
    public async Task<ActionResult<AuthResponseDto>> GoogleLogin([FromBody] GoogleLoginDto dto, CancellationToken cancellationToken)
    {
        var clientId = _config["GoogleAuth:ClientId"];
        if (string.IsNullOrWhiteSpace(clientId))
            return StatusCode(StatusCodes.Status503ServiceUnavailable, "Google sign-in is not configured.");

        var payload = await _googleTokenVerifier.VerifyAsync(dto.IdToken, clientId, cancellationToken);
        if (payload == null)
            return Unauthorized("Google sign-in could not be verified.");

        var normalizedEmail = payload.Email.Trim().ToLowerInvariant();

        var user = await _db.Users.FirstOrDefaultAsync(
            u => u.AuthProvider == "google" && u.AuthProviderSubject == payload.Subject,
            cancellationToken);

        if (user == null)
        {
            user = await _db.Users.FirstOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);
        }

        if (user == null)
        {
            user = new User
            {
                Name = payload.Name.Trim(),
                Email = normalizedEmail,
                PasswordHash = string.Empty,
                AuthProvider = "google",
                AuthProviderSubject = payload.Subject,
                AvatarUrl = payload.Picture
            };

            _db.Users.Add(user);
        }
        else
        {
            user.AuthProvider = "google";
            user.AuthProviderSubject = payload.Subject;

            if (string.IsNullOrWhiteSpace(user.Name))
                user.Name = payload.Name.Trim();

            if (string.IsNullOrWhiteSpace(user.AvatarUrl) && !string.IsNullOrWhiteSpace(payload.Picture))
                user.AvatarUrl = payload.Picture;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(CreateAuthResponse(user));
    }

    private AuthResponseDto CreateAuthResponse(User user)
    {
        return new AuthResponseDto
        {
            Token = GenerateToken(user),
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            Role = user.Role
        };
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> GetMe()
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        return Ok(CreateProfileResponse(user));
    }

    [HttpPatch("profile")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> UpdateProfile([FromBody] UpdateProfileDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
        if (dto.AvatarUrl != null) user.AvatarUrl = dto.AvatarUrl;

        await _db.SaveChangesAsync();

        return Ok(CreateAuthResponse(user));
    }

    [HttpPatch("password")]
    [Authorize]
    public async Task<ActionResult> ChangePassword([FromBody] ChangePasswordDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var user = await _db.Users.FindAsync(userId);
        if (user == null) return NotFound();

        if (!BCrypt.Net.BCrypt.Verify(dto.CurrentPassword, user.PasswordHash))
            return BadRequest("Current password is incorrect.");

        user.PasswordHash = BCrypt.Net.BCrypt.HashPassword(dto.NewPassword);
        await _db.SaveChangesAsync();

        return Ok();
    }

    private AuthResponseDto CreateProfileResponse(User user)
    {
        return new AuthResponseDto
        {
            Token = string.Empty,
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            AvatarUrl = user.AvatarUrl,
            Role = user.Role
        };
    }

    private string GenerateToken(User user)
    {
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_config["Jwt:Key"]!));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(ClaimTypes.Name, user.Name),
            new Claim(ClaimTypes.Role, user.Role)
        };

        var token = new JwtSecurityToken(
            issuer: _config["Jwt:Issuer"],
            audience: _config["Jwt:Audience"],
            claims: claims,
            expires: DateTime.UtcNow.AddDays(7),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
