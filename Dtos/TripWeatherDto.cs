namespace sidequest.backend.Dtos;

// SideQuest-owned weather contract — never provider JSON. Status values:
//   available      — Days holds the trip∩forecast-window daily forecast
//   too_early      — trip starts beyond the provider's real window;
//                    ForecastAvailableFrom says when forecasts open
//   no_coordinates — the trip's destination has no picked coordinates
//   unavailable    — past trip, missing dates, or provider failure with no
//                    usable stale cache
public class TripWeatherDto
{
    public string Status { get; set; } = "unavailable";
    public string? DestinationName { get; set; }
    public string? Timezone { get; set; }
    public DateOnly? ForecastStart { get; set; }
    public DateOnly? ForecastEnd { get; set; }
    public DateOnly? ForecastAvailableFrom { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public bool Stale { get; set; }
    public string? Attribution { get; set; }
    // The per-DAY forecast, one entry per trip day, from that day's MAIN
    // location. Unchanged shape and meaning — an older client that only reads
    // this keeps working exactly as before.
    public List<TripWeatherDayDto> Days { get; set; } = new();
    // One forecast per STORED day-location row, including the additional stops
    // a day may have. Empty for a trip whose days are all carried forward or
    // fall back to the destination, which is precisely when there is nothing
    // extra to say. Additive: nothing above depends on it.
    public List<TripWeatherLocationDto> LocationForecasts { get; set; } = new();
}

// A forecast tied to one specific stored place, so a client can pick the right
// weather when a single day holds several. Carries the coordinates and label
// alongside the id, which is what lets a slide match by placeId, coordinates or
// label when it has no id of its own yet.
public class TripWeatherLocationDto
{
    public Guid DayLocationId { get; set; }
    public DateOnly Date { get; set; }
    public int SortIndex { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    // Same availability contract as TripWeatherDayDto: false means the provider
    // had no real forecast, and every value below is null rather than a
    // fabricated zero.
    public bool IsForecastAvailable { get; set; }
    public string? Code { get; set; }
    public double? TempMinC { get; set; }
    public double? TempMaxC { get; set; }
    public int? PrecipitationProbability { get; set; }
    public double? UvIndexMax { get; set; }
}

public class TripWeatherDayDto
{
    public DateOnly Date { get; set; }
    // Explicit availability flag — never inferred from whether the numeric
    // fields happen to be zero. False means the provider had no real
    // forecast for this date yet (e.g. the last day of its window); every
    // field below is null in that case, not a fabricated zero.
    public bool IsForecastAvailable { get; set; }
    public string? Code { get; set; }
    public double? TempMinC { get; set; }
    public double? TempMaxC { get; set; }
    public int? PrecipitationProbability { get; set; }
    public double? UvIndexMax { get; set; }
    // Which resolved location this day's forecast came from — a
    // TripDayLocation anchor (explicit or carried forward), or the trip
    // destination fallback. Always set, even when IsForecastAvailable is
    // false, so the day still shows where the travellers are.
    public string LocationLabel { get; set; } = string.Empty;
}
