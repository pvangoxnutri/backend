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
    private readonly ITripResolvedLocationTimelineService _timeline;

    public TripWeatherController(
        AppDbContext db, WeatherService weather, ITripResolvedLocationTimelineService timeline)
    {
        _db = db;
        _weather = weather;
        _timeline = timeline;
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

        // The shared loader. Gluno calls the same one, so the cities on this
        // screen and the stops Gluno describes cannot come from different rows.
        var loaded = await _timeline.BuildAsync(tripId, endOverride: null, ct);
        if (loaded == null) return NotFound();

        var dayLocations = loaded.DayLocations;

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
        if (trip.EndDate < trip.StartDate || trip.StartDate.Year < 2000
            || (trip.EndDate.HasValue && trip.EndDate.Value < today))
        {
            // An open-ended adventure is never "fully past" — it stays
            // forecastable until the traveller gives it an end date or
            // completes it.
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

        // The forecast horizon is the hard ceiling on how far the timeline is
        // walked. A trip WITH an end date past the horizon was already clipped
        // by the provider returning nothing for those days; doing it here means
        // an OPEN-ENDED trip cannot ask for an unbounded number of days in the
        // first place, rather than relying on a downstream filter.
        var rangeEnd = TripDateRange.EffectiveEnd(trip.StartDate, trip.EndDate, today);
        if (rangeEnd > lastForecastDay) rangeEnd = lastForecastDay;

        // Re-resolved to the FORECAST horizon rather than the trip's own end:
        // past it there are no numbers to show, and walking further would only
        // produce rows with a place and no weather. Same service, same rows —
        // only the ceiling differs, and it differs for a stated reason.
        var horizon = await _timeline.BuildAsync(tripId, endOverride: rangeEnd, ct);
        var timeline = horizon?.Days ?? loaded.Days;

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

        // ── Per-location forecasts ────────────────────────────────────────
        // One entry per STORED row, additional stops included. Days[] above
        // stays the main-location-only view, so nothing here can change what an
        // older client reads.
        //
        // Reuses forecastsByCoordinate, so a stop that shares coordinates with
        // the day's main location (or with any other day) costs no extra
        // provider call. Only genuinely new coordinates are fetched.
        var locationForecasts = new List<TripWeatherLocationDto>();
        foreach (var row in dayLocations.OrderBy(d => d.StartDate).ThenBy(d => d.SortIndex))
        {
            // A stop outside the forecastable range would only produce an entry
            // with no numbers; skip it rather than pad the list. rangeEnd is
            // already clipped to the provider's horizon, so an open-ended trip
            // adds no extra provider calls here either.
            if (row.StartDate < trip.StartDate || row.StartDate > rangeEnd) continue;

            var key = (row.Latitude, row.Longitude);
            if (!forecastsByCoordinate.ContainsKey(key))
            {
                try
                {
                    var extra = await _weather.GetForecastAsync(row.Latitude, row.Longitude, ct);
                    forecastsByCoordinate[key] = (extra.Forecast, extra.Stale);
                }
                catch
                {
                    // One stop's provider call failing must never take the whole
                    // trip's weather down — record it as unavailable and move on.
                    forecastsByCoordinate[key] = (null, false);
                }
            }

            var (locForecast, locStale) = forecastsByCoordinate[key];
            var locDay = locForecast?.Days.FirstOrDefault(d => d.Date == row.StartDate);
            var available = locForecast != null && locDay is { IsForecastAvailable: true };

            locationForecasts.Add(new TripWeatherLocationDto
            {
                DayLocationId = row.Id,
                Date = row.StartDate,
                SortIndex = row.SortIndex,
                LocationLabel = row.LocationLabel,
                Latitude = row.Latitude,
                Longitude = row.Longitude,
                PlaceId = row.PlaceId,
                IsForecastAvailable = available,
                Code = available ? locDay!.Code : null,
                TempMinC = available ? locDay!.TempMinC : null,
                TempMaxC = available ? locDay!.TempMaxC : null,
                PrecipitationProbability = available ? locDay!.PrecipitationProbability : null,
                UvIndexMax = available ? locDay!.UvIndexMax : null,
            });

            if (available && locStale) anyStale = true;
        }

        dto.LocationForecasts = locationForecasts;

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
