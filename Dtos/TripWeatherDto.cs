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
    public string Code { get; set; } = "cloudy";
    public double TempMinC { get; set; }
    public double TempMaxC { get; set; }
    public int PrecipitationProbability { get; set; }
    public double UvIndexMax { get; set; }
}
