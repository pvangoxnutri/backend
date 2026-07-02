using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/admin")]
public class AdminController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly IConfiguration _config;

    public AdminController(AppDbContext db, IConfiguration config)
    {
        _db = db;
        _config = config;
    }

    private bool IsAdminAuthorized()
    {
        var key = HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();
        var expected = _config["Admin:ApiKey"];
        return !string.IsNullOrWhiteSpace(expected) && key == expected;
    }

    // GET /api/admin/users?search=
    [HttpGet("users")]
    public async Task<ActionResult> GetUsers([FromQuery] string? search, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var query = _db.Users.AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim().ToLower();
            query = query.Where(u => u.Name.ToLower().Contains(s) || u.Email.ToLower().Contains(s));
        }

        var users = await query
            .OrderByDescending(u => u.CreatedAt)
            .Take(50)
            .Select(u => new
            {
                id = u.Id,
                name = u.Name,
                email = u.Email,
                avatarUrl = u.AvatarUrl,
                isBanned = u.IsBanned,
                role = u.Role,
                createdAt = u.CreatedAt,
            })
            .ToListAsync(ct);

        return Ok(users);
    }

    // POST /api/admin/users/{userId}/ban
    [HttpPost("users/{userId:guid}/ban")]
    public async Task<ActionResult> BanUser(Guid userId, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var user = await _db.Users.FindAsync([userId], ct);
        if (user == null) return NotFound();

        user.IsBanned = true;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = user.Id, isBanned = true });
    }

    // DELETE /api/admin/users/{userId}/ban
    [HttpDelete("users/{userId:guid}/ban")]
    public async Task<ActionResult> UnbanUser(Guid userId, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var user = await _db.Users.FindAsync([userId], ct);
        if (user == null) return NotFound();

        user.IsBanned = false;
        await _db.SaveChangesAsync(ct);
        return Ok(new { id = user.Id, isBanned = false });
    }

    // POST /api/admin/users/{userId}/warn
    [HttpPost("users/{userId:guid}/warn")]
    public async Task<ActionResult> WarnUser(Guid userId, [FromBody] WarnUserDto dto, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.Title)) return BadRequest("Title is required.");
        if (string.IsNullOrWhiteSpace(dto.Body)) return BadRequest("Body is required.");

        var user = await _db.Users.FindAsync([userId], ct);
        if (user == null) return NotFound();

        var log = new NotificationLog
        {
            Type = "system_message",
            DedupeKey = $"system_message:{userId}:{DateTime.UtcNow.Ticks}",
            RecipientUserId = userId,
            Title = dto.Title.Trim(),
            Body = dto.Body.Trim(),
            DataJson = System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["title"] = dto.Title.Trim(),
                ["body"] = dto.Body.Trim(),
            }),
            Status = "delivered",
            DeliveredAt = DateTime.UtcNow,
        };
        _db.NotificationLogs.Add(log);
        await _db.SaveChangesAsync(ct);

        return Ok(new { id = log.Id });
    }
}

public record WarnUserDto(string Title, string Body);
