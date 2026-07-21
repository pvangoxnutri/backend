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
    public List<TripWeatherDayDto> Days { get; set; } = new();
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
