using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;
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

    // ── Travel Tracker aggregate stats ───────────────────────────────────
    // The tracker's raw country statuses never leave the device; the app
    // syncs ONLY these aggregate numbers so other members' profiles can
    // show them. GET works for any authenticated user (own or others) and
    // never exposes a country list, e-mail or any other personal data.

    /// <summary>Upserts the caller's own aggregate stats. The caller can
    /// only ever write their own row (id from the JWT, never the body).</summary>
    [Authorize]
    [HttpPut("me/travel-stats")]
    public async Task<ActionResult> PutMyTravelStats([FromBody] UpdateTravelStatsDto dto)
    {
        var idClaim = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (!Guid.TryParse(idClaim, out var userId)) return Unauthorized();

        // Sanity bounds — aggregates only, and never trust absurd values.
        if (dto.CountriesVisited < 0 || dto.CountriesVisited > 300
            || dto.ContinentsReached < 0 || dto.ContinentsReached > 7)
            return BadRequest("Stats out of range.");

        var row = await _db.UserTravelStats.FindAsync(userId);
        if (row == null)
        {
            _db.UserTravelStats.Add(new UserTravelStats
            {
                UserId = userId,
                CountriesVisited = dto.CountriesVisited,
                ContinentsReached = dto.ContinentsReached,
                UpdatedAt = DateTime.UtcNow,
            });
        }
        else
        {
            row.CountriesVisited = dto.CountriesVisited;
            row.ContinentsReached = dto.ContinentsReached;
            row.UpdatedAt = DateTime.UtcNow;
        }
        await _db.SaveChangesAsync();
        return NoContent();
    }

    /// <summary>Aggregate stats for any user (self or others). 404 when the
    /// user does not exist OR has never synced stats — the client hides the
    /// section then instead of showing fabricated zeros.</summary>
    [Authorize]
    [HttpGet("{userId:guid}/travel-stats")]
    public async Task<ActionResult<TravelStatsDto>> GetTravelStats(Guid userId)
    {
        var row = await _db.UserTravelStats.AsNoTracking()
            .FirstOrDefaultAsync(s => s.UserId == userId);
        if (row == null) return NotFound();

        return Ok(new TravelStatsDto
        {
            CountriesVisited = row.CountriesVisited,
            ContinentsReached = row.ContinentsReached,
            UpdatedAt = row.UpdatedAt,
        });
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

public class UpdateTravelStatsDto
{
    public int CountriesVisited { get; set; }
    public int ContinentsReached { get; set; }
}

public class TravelStatsDto
{
    public int CountriesVisited { get; set; }
    public int ContinentsReached { get; set; }
    public DateTime UpdatedAt { get; set; }
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
