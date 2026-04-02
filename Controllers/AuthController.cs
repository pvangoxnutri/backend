using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AuthController : ControllerBase
{
    private readonly AppDbContext _db;

    public AuthController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost("sync")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> Sync([FromBody] SyncAuthUserDto? dto, CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(dto, cancellationToken);
        return Ok(CreateAuthResponse(user));
    }

    [HttpGet("me")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> GetMe(CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(null, cancellationToken);
        return Ok(CreateAuthResponse(user));
    }

    [HttpPatch("profile")]
    [Authorize]
    public async Task<ActionResult<AuthResponseDto>> UpdateProfile([FromBody] UpdateProfileDto dto, CancellationToken cancellationToken)
    {
        var user = await GetOrCreateCurrentUserAsync(null, cancellationToken);

        if (!string.IsNullOrWhiteSpace(dto.Name)) user.Name = dto.Name.Trim();
        if (dto.AvatarUrl != null) user.AvatarUrl = dto.AvatarUrl;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(CreateAuthResponse(user));
    }

    private async Task<User> GetOrCreateCurrentUserAsync(SyncAuthUserDto? dto, CancellationToken cancellationToken)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
        var email = User.FindFirstValue(ClaimTypes.Email)?.Trim().ToLowerInvariant();

        if (string.IsNullOrWhiteSpace(email))
            throw new InvalidOperationException("Authenticated Supabase user is missing an email claim.");

        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user == null)
        {
            user = new User
            {
                Id = userId,
                Email = email,
                Name = dto?.Name?.Trim() ?? User.FindFirstValue(ClaimTypes.Name) ?? email,
                AvatarUrl = dto?.AvatarUrl,
                Role = "user"
            };

            _db.Users.Add(user);
        }
        else
        {
            user.Email = email;

            if (!string.IsNullOrWhiteSpace(dto?.Name))
                user.Name = dto.Name.Trim();

            if (dto?.AvatarUrl != null)
                user.AvatarUrl = dto.AvatarUrl;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return user;
    }

    private static AuthResponseDto CreateAuthResponse(User user)
    {
        return new AuthResponseDto
        {
            Token = string.Empty,
            Id = user.Id,
            Name = user.Name,
            Email = user.Email,
            EmailVerified = true,
            AvatarUrl = user.AvatarUrl,
            Role = user.Role
        };
    }
}
