using Microsoft.AspNetCore.Mvc;
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
    public async Task<ActionResult<TripResponseDto>> CreateTrip([FromBody] CreateTripDto dto)
    {
        var trip = new Trip
        {
            Title = dto.Title,
            Description = dto.Description,
            Destination = dto.Destination,
            StartDate = dto.StartDate,
            EndDate = dto.EndDate
        };

        _db.Trips.Add(trip);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(CreateTrip), new { id = trip.Id }, new TripResponseDto
        {
            Id = trip.Id,
            Title = trip.Title,
            Description = trip.Description,
            Destination = trip.Destination,
            StartDate = trip.StartDate,
            EndDate = trip.EndDate,
            CreatedAt = trip.CreatedAt
        });
    }
}
