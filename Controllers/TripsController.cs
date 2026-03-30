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
public class TripsController : ControllerBase
{
    private readonly AppDbContext _db;

    public TripsController(AppDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    [Authorize]
    public async Task<ActionResult<TripResponseDto>> CreateTrip([FromBody] CreateTripDto dto)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = new Trip
        {
            Title = dto.Title,
            Description = dto.Description,
            Destination = dto.Destination,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate,
            OwnerId = userId
        };

        _db.Trips.Add(trip);

        _db.TripMembers.Add(new TripMember
        {
            TripId = trip.Id,
            UserId = userId
        });

        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetTrip), new { id = trip.Id }, new TripResponseDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Description = trip.Description,
            Destination = trip.Destination,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            CreatedAt = trip.CreatedAt,
            OwnerId = trip.OwnerId
        });
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<TripResponseDto>> GetTrip(Guid id)
    {
        var trip = await _db.Trips.FindAsync(id);

        if (trip == null)
            return NotFound();

        return Ok(new TripResponseDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Description = trip.Description,
            Destination = trip.Destination,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            CreatedAt = trip.CreatedAt,
            OwnerId = trip.OwnerId
        });
    }

    [HttpGet("{id}/members")]
    [Authorize]
    public async Task<ActionResult<List<TripMemberDto>>> GetTripMembers(Guid id)
    {
        if (!await _db.Trips.AnyAsync(t => t.Id == id))
            return NotFound();

        var members = await _db.TripMembers
            .Where(tm => tm.TripId == id)
            .Select(tm => new TripMemberDto
            {
                Id = tm.User.Id,
                Name = tm.User.Name,
                AvatarUrl = tm.User.AvatarUrl
            })
            .ToListAsync();

        return Ok(members);
    }
}
