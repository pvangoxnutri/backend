namespace sidequest.backend.Models;

// "From this day onwards, the travellers are here." One row per calendar
// date the user has explicitly set; TripDayLocationService expands these
// forward to cover every day of the trip. Deliberately has no ActivityId —
// activities remain completely independent of this table — and no
// timezone column: the weather provider's own per-location response
// remains the sole source of truth for that.
public class TripDayLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
