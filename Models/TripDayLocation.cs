namespace sidequest.backend.Models;

// One place the travellers are on a given day. A date can now hold SEVERAL
// ordered rows: SortIndex 0 is the day's MAIN location, anything above it is
// an additional stop that day.
//
// Carry-forward is deliberately asymmetric. Only SortIndex 0 rolls onward to
// later days that have no rows of their own — an extra stop belongs to the day
// it was added to and nothing else. "Nice, then Monaco" on Tuesday must not
// make Wednesday claim Monaco too.
//
// Deliberately has no ActivityId — activities remain completely independent of
// this table — and no timezone column: the weather provider's own per-location
// response remains the sole source of truth for that.
public class TripDayLocation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public DateOnly StartDate { get; set; }
    // Position within the day, always contiguous from 0. The API reindexes on
    // every insert, move and delete, so a gap can never appear — which is what
    // lets "SortIndex 0" mean "the main location" without a second flag.
    public int SortIndex { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
