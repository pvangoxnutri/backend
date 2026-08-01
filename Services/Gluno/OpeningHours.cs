using System.Globalization;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One continuous span a place is open, on one weekday.
///
/// A day can have several: a restaurant open 12–14 and 18–23 is two intervals,
/// not one 12–23. Collapsing them is how a plan ends up putting someone at a
/// locked door at four in the afternoon.
/// </summary>
public sealed record OpeningInterval(DayOfWeek Day, TimeOnly Open, TimeOnly Close)
{
    /// A bar open 20:00–02:00 closes on the FOLLOWING day.
    public bool CrossesMidnight => Close <= Open;

    /// Minutes from local midnight on <see cref="Day"/>. Past 1440 when the
    /// span runs into the next day, which is what makes the comparisons below
    /// work without special cases everywhere.
    public int OpenMinutes => Open.Hour * 60 + Open.Minute;

    public int CloseMinutes
    {
        get
        {
            var minutes = Close.Hour * 60 + Close.Minute;
            return CrossesMidnight ? minutes + 24 * 60 : minutes;
        }
    }
}

public enum OpeningStatus
{
    /// No data at all, or data too old to trust. NOT the same as closed.
    Unknown,
    Open,
    Closed,
    /// Opens later than the proposed start, or closes before the proposed end.
    PartiallyOpen,
}

public sealed record OpeningHoursCheck(
    OpeningStatus Status,
    /// Local opening time on the day asked about, when known.
    TimeOnly? OpensAt,
    TimeOnly? ClosesAt,
    /// Machine code for the mobile copy: "closed_that_day", "opens_later",
    /// "closes_before_start", "closes_before_end", "unknown_hours".
    string? WarningCode);

/// <summary>
/// Verified opening hours, normalised away from any one provider's shape.
///
/// WHAT "VERIFIED" MEANS HERE. A provider told us these hours, and we recorded
/// when. It does NOT mean the place is open right now — public holidays,
/// seasonal closures and last-minute changes are invisible to every hours API
/// there is. That gap is why <see cref="Evaluate"/> can return Unknown with a
/// "possible_holiday" code rather than a confident Open, and why the prompt
/// forbids the phrase "open now" unless the data is both present and fresh.
///
/// MISSING IS MISSING. An absent day is Unknown, never Closed. "The museum is
/// closed on Mondays" when the truth is "the provider did not tell us about
/// Mondays" is a worse failure than saying nothing.
/// </summary>
public sealed class OpeningHours
{
    public required IReadOnlyList<OpeningInterval> Intervals { get; init; }

    /// Provider id — "tripadvisor". Never a URL, never a key.
    public required string Source { get; init; }

    public required DateTime FetchedAtUtc { get; init; }

    /// <summary>
    /// The place's own timezone, when the provider gives one.
    ///
    /// Opening hours are always local to the PLACE. A trip planned from Sweden
    /// to Tokyo that compares 09:00 Stockholm against Tokyo opening times is
    /// wrong by eight hours in a way nobody notices until they are standing
    /// outside.
    /// </summary>
    public string? TimeZoneId { get; init; }

    public bool IsKnown => Intervals.Count > 0;

    /// <summary>
    /// Past this, hours are treated as unknown rather than quoted.
    ///
    /// Deliberately conservative. Hours change with seasons, and an assistant
    /// confidently repeating last spring's timetable is worse than one that
    /// says it does not know.
    /// </summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    public bool IsFresh(DateTime nowUtc) => nowUtc - FetchedAtUtc <= MaxAge;

    /// <summary>
    /// Does a proposed visit fit inside the opening hours?
    /// </summary>
    /// <param name="date">The local date of the visit, at the place.</param>
    public OpeningHoursCheck Evaluate(DateOnly date, TimeOnly start, int durationMinutes, DateTime nowUtc)
    {
        if (!IsKnown || !IsFresh(nowUtc))
            return new OpeningHoursCheck(OpeningStatus.Unknown, null, null, "unknown_hours");

        var startMinutes = start.Hour * 60 + start.Minute;
        var endMinutes = startMinutes + Math.Max(0, durationMinutes);

        // Intervals from the PREVIOUS day that run past midnight still cover
        // this morning — a 20:00–02:00 bar is open at 00:30 today.
        var previousDay = date.AddDays(-1).DayOfWeek;
        var candidates = Intervals
            .Where(interval => interval.Day == date.DayOfWeek)
            .Select(interval => (Open: interval.OpenMinutes, Close: interval.CloseMinutes, Interval: interval))
            .Concat(Intervals
                .Where(interval => interval.Day == previousDay && interval.CrossesMidnight)
                .Select(interval => (Open: interval.OpenMinutes - 24 * 60, Close: interval.CloseMinutes - 24 * 60, Interval: interval)))
            .OrderBy(candidate => candidate.Open)
            .ToList();

        if (candidates.Count == 0)
        {
            // The provider described this place's week and did not include this
            // weekday. That is real evidence of a closing day — unlike an
            // entirely absent hours object, which lands in Unknown above.
            return new OpeningHoursCheck(OpeningStatus.Closed, null, null, "closed_that_day");
        }

        foreach (var candidate in candidates)
        {
            if (startMinutes >= candidate.Open && endMinutes <= candidate.Close)
            {
                return new OpeningHoursCheck(
                    OpeningStatus.Open, candidate.Interval.Open, candidate.Interval.Close, null);
            }
        }

        // Inside an interval at the start but running past its close.
        var straddling = candidates.FirstOrDefault(candidate =>
            startMinutes >= candidate.Open && startMinutes < candidate.Close);

        if (straddling.Interval != null)
        {
            return new OpeningHoursCheck(
                OpeningStatus.PartiallyOpen, straddling.Interval.Open, straddling.Interval.Close, "closes_before_end");
        }

        // Arriving before the first opening of the day is a fixable problem —
        // report when it opens so the planner can shift the stop.
        var next = candidates.FirstOrDefault(candidate => candidate.Open > startMinutes);
        if (next.Interval != null)
        {
            return new OpeningHoursCheck(
                OpeningStatus.Closed, next.Interval.Open, next.Interval.Close, "opens_later");
        }

        var last = candidates[^1];
        return new OpeningHoursCheck(OpeningStatus.Closed, last.Interval.Open, last.Interval.Close, "closes_before_start");
    }

    /// <summary>
    /// Tripadvisor's structured hours, normalised.
    ///
    /// Shape: <c>hours.periods[] = { open: { day, time }, close: { day, time } }</c>,
    /// day 0 = Sunday, time "HHmm". A period with no close is a 24-hour day.
    ///
    /// The weekday_text lines are NOT parsed. They are localised prose
    /// ("Mon 9:00 AM - 5:00 PM"), and parsing prose into times is exactly the
    /// kind of nearly-right that produces a plan built on a misread timetable.
    /// When there are no periods, hours stay unknown.
    /// </summary>
    public static OpeningHours? FromTripadvisor(JsonElement root, DateTime fetchedAtUtc, string? timeZoneId = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (!root.TryGetProperty("hours", out var hours) || hours.ValueKind != JsonValueKind.Object) return null;
        if (!hours.TryGetProperty("periods", out var periods) || periods.ValueKind != JsonValueKind.Array) return null;

        var intervals = new List<OpeningInterval>();

        foreach (var period in periods.EnumerateArray())
        {
            var open = ReadDayTime(period, "open");
            if (open == null) continue;

            var close = ReadDayTime(period, "close");

            if (close == null)
            {
                // Open with no close: the documented shape for a 24-hour day.
                intervals.Add(new OpeningInterval(open.Value.Day, new TimeOnly(0, 0), new TimeOnly(23, 59)));
                continue;
            }

            // A close on a later weekday than the open is the over-midnight
            // case; the interval belongs to the OPENING day and the
            // CrossesMidnight arithmetic carries it forward.
            intervals.Add(new OpeningInterval(open.Value.Day, open.Value.Time, close.Value.Time));
        }

        if (intervals.Count == 0) return null;

        return new OpeningHours
        {
            Intervals = intervals,
            Source = TripadvisorTravelProvider.ProviderId,
            FetchedAtUtc = fetchedAtUtc,
            TimeZoneId = timeZoneId,
        };
    }

    private static (DayOfWeek Day, TimeOnly Time)? ReadDayTime(JsonElement period, string name)
    {
        if (period.ValueKind != JsonValueKind.Object) return null;
        if (!period.TryGetProperty(name, out var node) || node.ValueKind != JsonValueKind.Object) return null;

        if (!node.TryGetProperty("day", out var dayValue) || dayValue.ValueKind != JsonValueKind.Number) return null;
        var day = dayValue.GetInt32();
        if (day is < 0 or > 6) return null;

        if (!node.TryGetProperty("time", out var timeValue)) return null;
        var raw = timeValue.ValueKind switch
        {
            JsonValueKind.String => timeValue.GetString(),
            JsonValueKind.Number => timeValue.GetInt32().ToString("0000", CultureInfo.InvariantCulture),
            _ => null,
        };

        if (raw == null || raw.Length != 4 || !raw.All(char.IsAsciiDigit)) return null;

        var hour = int.Parse(raw[..2], CultureInfo.InvariantCulture);
        var minute = int.Parse(raw[2..], CultureInfo.InvariantCulture);
        if (hour > 23 || minute > 59) return null;

        // Google/Tripadvisor day 0 is Sunday, which is also DayOfWeek 0.
        return ((DayOfWeek)day, new TimeOnly(hour, minute));
    }

    /// <summary>
    /// Short, honest, human-readable — for a proposal row, not for the model to
    /// re-derive. Returns null when there is nothing trustworthy to say.
    /// </summary>
    public string? Describe(DateOnly date, string language, DateTime nowUtc)
    {
        if (!IsKnown || !IsFresh(nowUtc)) return null;

        var today = Intervals.Where(interval => interval.Day == date.DayOfWeek).OrderBy(i => i.Open).ToList();
        if (today.Count == 0) return language == "sv" ? "Stängt den dagen" : "Closed that day";

        return string.Join(", ", today.Select(interval => $"{interval.Open:HH\\:mm}–{interval.Close:HH\\:mm}"));
    }
}
