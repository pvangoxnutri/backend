using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly AppDbContext _db;

    public UsersController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet("{userId}/profile")]
    public async Task<ActionResult<UserProfileDto>> GetUserProfile(Guid userId)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
            return NotFound();

        var tripsJoined = await _db.TripMembers.CountAsync(tm => tm.UserId == userId);
        var sidequestsCreated = await _db.Trips.CountAsync(t => t.OwnerId == userId);

        var countriesVisited = await _db.TripMembers
            .Where(tm => tm.UserId == userId)
            .Join(_db.Trips, tm => tm.TripId, t => t.Id, (tm, t) => t.Destination)
            .Where(d => d != null && d != "")
            .Select(d => d.ToLower())
            .Distinct()
            .CountAsync();

        return Ok(new UserProfileDto
        {
            Id = user.Id,
            Name = user.Name,
            Bio = user.Bio,
            AvatarUrl = user.AvatarUrl,
            TripsJoined = tripsJoined,
            SidequestsCreated = sidequestsCreated,
            CountriesVisited = countriesVisited,
            IsOnline = PresenceHelper.IsOnline(user.LastSeenAt)
        });
    }

    // ── PUT /api/users/me/heartbeat ───────────────────────────────────────────
    // The presence middleware in Program.cs stamps LastSeenAt on every
    // authenticated request (throttled to one write per user per 60s), so
    // the body-less beat stays a zero-cost request to make while idle in
    // the foreground. The optional body handles the two transitions the
    // throttle can't:
    //   { "online": false } — app backgrounded: backdate LastSeenAt past the
    //     online window so the user reads offline immediately instead of
    //     after the 2-minute fallback.
    //   { "online": true }  — foreground (re)entry: stamp explicitly; after
    //     a backdate the middleware throttle may skip writes for up to 60s,
    //     which would leave an actively-returning user looking offline.

    [HttpPut("me/heartbeat")]
    [Authorize]
    public async Task<ActionResult> Heartbeat(
        [FromBody(EmptyBodyBehavior = Microsoft.AspNetCore.Mvc.ModelBinding.EmptyBodyBehavior.Allow)] HeartbeatDto? dto)
    {
        if (dto?.Online is bool online)
        {
            var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);
            var stamp = online ? DateTime.UtcNow : DateTime.UtcNow - PresenceHelper.OnlineWindow;
            await _db.Users
                .Where(u => u.Id == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, stamp));
        }
        return NoContent();
    }
}

// Optional heartbeat body — absent for the plain idle beat.
public class HeartbeatDto
{
    public bool? Online { get; set; }
}

public class UserProfileDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Bio { get; set; }
    public string? AvatarUrl { get; set; }
    public int TripsJoined { get; set; }
    public int SidequestsCreated { get; set; }
    public int CountriesVisited { get; set; }
    public bool IsOnline { get; set; }
}
