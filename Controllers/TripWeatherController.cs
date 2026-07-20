using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

// ── GET /api/trips/{tripId}/weather ───────────────────────────────────────
// Trip-scoped daily forecast for the destination. The status/window logic
// runs entirely on trip dates, so past trips and trips outside Open-Meteo's
// real 16-day window never cost a provider call. Weather can degrade
// (stale cache) or report unavailable, but it can never fail a request
// with a 5xx surprise — the trip itself always loads through other routes.
[ApiController]
[Route("api/trips/{tripId}/weather")]
[Authorize]
public class TripWeatherController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly WeatherService _weather;

    public TripWeatherController(AppDbContext db, WeatherService weather)
    {
        _db = db;
        _weather = weather;
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

        if (trip.DestinationLatitude is not double lat || trip.DestinationLongitude is not double lon)
        {
            dto.Status = "no_coordinates";
            return Ok(dto);
        }

        // Window math in UTC dates; the provider's own day list (destination
        // timezone) is what actually gets clipped below, so an off-by-one at
        // a timezone edge self-corrects on the next request.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var lastForecastDay = today.AddDays(WeatherService.ForecastDays - 1);

        // Degenerate dates (created without a range) or fully past trips:
        // there is nothing truthful to forecast.
        if (trip.EndDate < trip.StartDate || trip.StartDate.Year < 2000 || trip.EndDate < today)
        {
            dto.Status = "unavailable";
            return Ok(dto);
        }

        if (trip.StartDate > lastForecastDay)
        {
            dto.Status = "too_early";
            dto.ForecastAvailableFrom = trip.StartDate.AddDays(-(WeatherService.ForecastDays - 1));
            return Ok(dto);
        }

        var result = await _weather.GetForecastAsync(lat, lon, ct);
        if (result.Forecast == null)
        {
            dto.Status = "unavailable";
            return Ok(dto);
        }

        // Only days that belong to the trip — never unrelated forecast days.
        var days = result.Forecast.Days
            .Where(d => d.Date >= trip.StartDate && d.Date <= trip.EndDate)
            .OrderBy(d => d.Date)
            .Select(d => new TripWeatherDayDto
            {
                Date = d.Date,
                Code = d.Code,
                TempMinC = d.TempMinC,
                TempMaxC = d.TempMaxC,
                PrecipitationProbability = d.PrecipitationProbability,
                UvIndexMax = d.UvIndexMax,
            })
            .ToList();

        if (days.Count == 0)
        {
            // Timezone-edge case: the provider's window (destination time)
            // doesn't reach the trip yet even though UTC math said it might.
            var lastProviderDay = result.Forecast.Days.Max(d => d.Date);
            if (trip.StartDate > lastProviderDay)
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
        dto.Timezone = result.Forecast.Timezone;
        dto.ForecastStart = days[0].Date;
        dto.ForecastEnd = days[^1].Date;
        dto.UpdatedAt = result.Forecast.FetchedAt;
        dto.Stale = result.Stale;
        dto.Attribution = WeatherService.Attribution;
        dto.Days = days;
        return Ok(dto);
    }
}
