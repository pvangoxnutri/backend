namespace sidequest.backend.Dtos;

// GET response: the resolved timeline (one entry per trip day that has a
// location — explicit, carried forward, or the trip-destination
// fallback), never only the raw stored rows. The backend is the single
// source of truth for carry-forward; mobile never recomputes it.
public class TripDayLocationDto
{
    public DateOnly Date { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    public bool IsExplicit { get; set; }
}

// PUT request body — StartDate comes from the route, not the body.
public class SetTripDayLocationDto
{
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
}
