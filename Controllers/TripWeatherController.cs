using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

// ── GET /api/trips/{tripId}/weather ───────────────────────────────────────
// Trip-scoped daily forecast. Location is resolved per day by
// TripDayLocationService (TripDayLocation anchors, carried forward, with
// the trip destination as the universal fallback) — this controller never
// does its own itinerary reasoning, it only fetches weather for whichever
// coordinates the resolver hands back. For a trip with zero TripDayLocation
// rows this produces exactly the same output as the single-destination
// version did. Weather can degrade (stale cache) or report unavailable,
// but it can never fail a request with a 5xx surprise — the trip itself
// always loads through other routes.
[ApiController]
[Route("api/trips/{tripId}/weather")]
[Authorize]
public class TripWeatherController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WeatherService _weather;
    private readonly TripDayLocationService _resolver;

    public TripWeatherController(AppDbContext db, WeatherService weather, TripDayLocationService resolver)
    {
        _db = db;
        _weather = weather;
        _resolver = resolver;
    }

    [HttpGet]
    public async Task<ActionResult<TripWeatherDto>> GetWeather(Guid tripId, CancellationToken ct)
    {
        var userId = Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

        var trip = await _db.Trips.FindAsync([tripId], ct);
        if (trip == null) return NotFound();
        if (!await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId, ct))
            return Forbid();

        var dto = new TripWeatherDto { DestinationName = trip.Destination };

        var dayLocations = await _db.TripDayLocations
            .Where(d => d.TripId == tripId)
            .ToListAsync(ct);

        // Cheap, date-range-independent check first — mirrors the original
        // no_coordinates precedence exactly for the zero-anchor case (this
        // reduces to "does the trip have a destination?", identical to
        // before) while correctly extending it to "or does at least one
        // day-location anchor exist?" for the new case.
        var hasAnyLocationSource = dayLocations.Count > 0
            || (trip.DestinationLatitude is double && trip.DestinationLongitude is double);
        if (!hasAnyLocationSource)
        {
            dto.Status = "no_coordinates";
            return Ok(dto);
        }

        // Degenerate dates (created without a range) or fully past trips:
        // there is nothing truthful to forecast. Checked before resolving
        // the timeline so the resolver never has to iterate a backwards or
        // meaningless date range.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        if (trip.EndDate < trip.StartDate || trip.StartDate.Year < 2000 || trip.EndDate < today)
        {
            dto.Status = "unavailable";
            return Ok(dto);
        }

        // Window math in UTC dates; each location's own provider response
        // (destination timezone) is what actually gets clipped below, so an
        // off-by-one at a timezone edge self-corrects on the next request.
        var lastForecastDay = today.AddDays(WeatherService.ForecastDays - 1);
        if (trip.StartDate > lastForecastDay)
        {
            dto.Status = "too_early";
            dto.ForecastAvailableFrom = trip.StartDate.AddDays(-(WeatherService.ForecastDays - 1));
            return Ok(dto);
        }

        var timeline = _resolver.ResolveTimeline(
            trip.StartDate, trip.EndDate, dayLocations,
            trip.Destination, trip.DestinationLatitude, trip.DestinationLongitude);

        // One provider call per unique coordinate actually visited by the
        // trip — not per day. Each still hits WeatherService's own
        // per-coordinate cache/dedupe/stale-fallback, unchanged.
        var forecastsByCoordinate = new Dictionary<(double Lat, double Lon), (WeatherForecast? Forecast, bool Stale)>();
        foreach (var location in timeline.Where(t => t != null).Select(t => t!))
        {
            var key = (location.Latitude, location.Longitude);
            if (forecastsByCoordinate.ContainsKey(key)) continue;
            var result = await _weather.GetForecastAsync(location.Latitude, location.Longitude, ct);
            forecastsByCoordinate[key] = (result.Forecast, result.Stale);
        }

        var days = new List<TripWeatherDayDto>();
        var anyStale = false;
        string? primaryTimezone = null;
        DateTime? latestUpdatedAt = null;

        foreach (var resolved in timeline)
        {
            if (resolved == null) continue;
            var (forecast, stale) = forecastsByCoordinate[(resolved.Latitude, resolved.Longitude)];
            var dayForecast = forecast?.Days.FirstOrDefault(d => d.Date == resolved.Date);

            if (forecast != null && dayForecast is { IsForecastAvailable: true })
            {
                days.Add(new TripWeatherDayDto
                {
                    Date = resolved.Date,
                    IsForecastAvailable = true,
                    Code = dayForecast.Code,
                    TempMinC = dayForecast.TempMinC,
                    TempMaxC = dayForecast.TempMaxC,
                    PrecipitationProbability = dayForecast.PrecipitationProbability,
                    UvIndexMax = dayForecast.UvIndexMax,
                    LocationLabel = resolved.LocationLabel,
                });

                primaryTimezone ??= forecast.Timezone;
                if (stale) anyStale = true;
                if (latestUpdatedAt == null || forecast.FetchedAt > latestUpdatedAt) latestUpdatedAt = forecast.FetchedAt;
            }
            else
            {
                // Outside this location's provider window, the provider
                // call failed entirely, or the provider returned this exact
                // date with no real values (e.g. the last day of its
                // window) — never fabricate a forecast. The date and its
                // resolved location still show; only the numbers are
                // withheld.
                days.Add(new TripWeatherDayDto
                {
                    Date = resolved.Date,
                    IsForecastAvailable = false,
                    LocationLabel = resolved.LocationLabel,
                });
            }
        }

        if (days.Count == 0)
        {
            // No resolved day had ANY location source to even attempt —
            // e.g. no destination coordinates and no anchor covers any
            // trip date. Distinguished from the per-day "unavailable"
            // entries above, which now always populate when a location is
            // known.
            var lastProviderDay = forecastsByCoordinate.Values
                .Where(v => v.Forecast != null)
                .SelectMany(v => v.Forecast!.Days)
                .Select(d => d.Date)
                .DefaultIfEmpty()
                .Max();

            if (lastProviderDay != default && trip.StartDate > lastProviderDay)
            {
                dto.Status = "too_early";
                dto.ForecastAvailableFrom = trip.StartDate.AddDays(-(WeatherService.ForecastDays - 1));
            }
            else
            {
                dto.Status = "unavailable";
            }
            return Ok(dto);
        }

        dto.Status = "available";
        dto.Timezone = primaryTimezone;
        dto.ForecastStart = days[0].Date;
        dto.ForecastEnd = days[^1].Date;
        dto.UpdatedAt = latestUpdatedAt;
        dto.Stale = anyStale;
        dto.Attribution = WeatherService.Attribution;
        dto.Days = days;
        return Ok(dto);
    }
}
