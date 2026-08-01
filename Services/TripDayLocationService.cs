using sidequest.backend.Models;

namespace sidequest.backend.Services;

public class ResolvedDayLocation
{
    public DateOnly Date { get; set; }
    public string LocationLabel { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public string? PlaceId { get; set; }
    // True only on the exact date a TripDayLocation row was set for;
    // false when this date's value is carried forward from an earlier
    // anchor, or is the trip-destination fallback.
    public bool IsExplicit { get; set; }
}

// Owns "where are the travellers on each day" — pure domain logic over
// TripDayLocation rows. No I/O: callers load the rows and trip fields,
// this just expands them. Weather is a consumer of this, not the owner —
// this class knows nothing about forecasts, providers, or caching.
public class TripDayLocationService
{
    // Returns one entry per calendar day in [startDate, endDate] — never
    // fewer, so callers never have to guess whether a day was silently
    // skipped. A null entry means no anchor and no trip destination exist
    // for that date; everything else always resolves to something.
    public List<ResolvedDayLocation?> ResolveTimeline(
        DateOnly startDate,
        DateOnly endDate,
        IReadOnlyList<TripDayLocation> dayLocations,
        string? tripDestinationName,
        double? tripDestinationLatitude,
        double? tripDestinationLongitude)
    {
        // ONLY the main location of each day (SortIndex 0) is an anchor. A day's
        // additional stops apply to that day alone and must never roll forward,
        // so they are filtered out before the scan rather than skipped inside
        // it — that way "the most recent anchor" cannot accidentally land on an
        // extra stop.
        var sorted = dayLocations
            .Where(d => d.SortIndex == 0)
            .OrderBy(d => d.StartDate)
            .ToList();
        var result = new List<ResolvedDayLocation?>();
        var pointer = -1;

        for (var date = startDate; date <= endDate; date = date.AddDays(1))
        {
            // Advance to the most recent anchor at-or-before this date —
            // carry-forward, expressed as a single forward scan.
            while (pointer + 1 < sorted.Count && sorted[pointer + 1].StartDate <= date)
                pointer++;

            if (pointer >= 0)
            {
                var anchor = sorted[pointer];
                result.Add(new ResolvedDayLocation
                {
                    Date = date,
                    LocationLabel = anchor.LocationLabel,
                    Latitude = anchor.Latitude,
                    Longitude = anchor.Longitude,
                    PlaceId = anchor.PlaceId,
                    IsExplicit = anchor.StartDate == date,
                });
            }
            else if (tripDestinationLatitude is double lat && tripDestinationLongitude is double lon)
            {
                // Before the first-ever anchor (or no anchors at all): the
                // trip destination is the universal fallback, read live
                // every time — never copied into a row, so editing the
                // trip destination later is reflected immediately without
                // touching any TripDayLocation.
                result.Add(new ResolvedDayLocation
                {
                    Date = date,
                    LocationLabel = tripDestinationName ?? string.Empty,
                    Latitude = lat,
                    Longitude = lon,
                    PlaceId = null,
                    IsExplicit = false,
                });
            }
            else
            {
                result.Add(null);
            }
        }

        return result;
    }
}
