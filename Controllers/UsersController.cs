using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

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

        return Ok(new UserProfileDto
        {
            Id = user.Id,
            Name = user.Name,
            Bio = user.Bio,
            AvatarUrl = user.AvatarUrl,
            TripsJoined = tripsJoined,
            SidequestsCreated = sidequestsCreated,
            CountriesVisited = 0
        });
    }
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
}
