namespace sidequest.backend.Dtos;

// GET response: the resolved timeline (one entry per trip day that has a
// location — explicit, carried forward, or the trip-destination
// fallback), never only the raw stored rows. The backend is the single
// source of truth for carry-forward; mobile never recomputes it.
//
// This shape is UNCHANGED so every existing client keeps working: it still
// describes the day's MAIN location, one entry per day. Clients that need the
// additional stops read the stored rows from the /entries endpoint instead.
public class TripDayLocationDto
{
    public DateOnly Date { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    public bool IsExplicit { get; set; }
}

// A single STORED row, not a resolved day. Carries its identity and position
// so a client can edit, delete and reorder one specific place — the resolved
// timeline above cannot, since a carried-forward day has no row of its own.
public class TripDayLocationEntryDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    // Named to match the column and the mobile type. The date this place
    // belongs to; for SortIndex 0 it is also the day carry-forward starts on.
    public DateOnly StartDate { get; set; }
    public int SortIndex { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
}

// PUT body for the main location of a date — StartDate comes from the route.
// Kept exactly as it was: this is the endpoint older clients already call.
public class SetTripDayLocationDto
{
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
}

// POST body for appending an ADDITIONAL place to a date. The server assigns
// SortIndex (always the next free position), so a client can never create a
// gap or a duplicate position.
public class AddTripDayLocationDto
{
    public DateOnly StartDate { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
}

// PATCH body for one specific stored row, addressed by id.
public class UpdateTripDayLocationEntryDto
{
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
}

// PATCH body for reordering a date's places. The full id list in the desired
// order — the same payload shape the activity reorder endpoint uses, so the
// server rewrites every SortIndex rather than trusting per-row numbers.
public class ReorderTripDayLocationsDto
{
    public DateOnly StartDate { get; set; }
    public List<Guid> LocationIds { get; set; } = [];
}
