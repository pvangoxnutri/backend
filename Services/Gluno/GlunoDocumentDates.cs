using System.Globalization;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Reads dates out of document text without guessing.
///
/// THE CENTRAL PROBLEM. "05/08/2026" is the fifth of August to a Swede and the
/// eighth of May to an American, and the string carries no evidence either way.
/// A system that picks one is right about half the time and confident every
/// time — and the failure surfaces as somebody at an airport on the wrong day.
///
/// So the rule here is: when two readings both fit, BOTH are returned and the
/// user chooses. Not the more likely one, not the one matching the phone's
/// locale. Both. The only ambiguity that gets resolved automatically is the
/// kind that is not really ambiguous — a day above twelve can only be a day.
///
/// TIMEZONES ARE NEVER INVENTED. A local time with no zone stays a local time
/// with no zone. Airport codes are the one exception, because IATA codes map to
/// exactly one place; a city name does not, and "Springfield" is a dozen
/// timezones.
/// </summary>
public static class GlunoDocumentDates
{
    /// <summary>
    /// Numeric dates: 05/08/2026, 5-8-2026, 2026-08-05.
    /// </summary>
    private static readonly Regex NumericDate = new(
        @"\b(\d{1,4})[/\-.](\d{1,2})[/\-.](\d{2,4})\b", RegexOptions.Compiled);

    /// Textual dates: "5 Aug 2026", "5 augusti 2026", "August 5, 2026".
    private static readonly Regex TextualDayFirst = new(
        @"\b(\d{1,2})\s+([A-Za-zÅÄÖåäö]{3,12})\.?\s+(\d{4})\b", RegexOptions.Compiled);

    private static readonly Regex TextualMonthFirst = new(
        @"\b([A-Za-zÅÄÖåäö]{3,12})\.?\s+(\d{1,2}),?\s+(\d{4})\b", RegexOptions.Compiled);

    private static readonly Regex TimePattern = new(
        @"\b([01]?\d|2[0-3])[:.]([0-5]\d)\b(?:\s*(am|pm|AM|PM))?", RegexOptions.Compiled);

    /// <summary>
    /// IATA airport codes we can resolve to a timezone.
    ///
    /// Deliberately a small, hand-maintained list rather than a lookup service.
    /// A code that is not here yields NO timezone, which is the correct
    /// outcome — inventing one is worse than leaving it null, and a null is
    /// something the validator can warn about.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> AirportTimeZones =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["ARN"] = "Europe/Stockholm", ["GOT"] = "Europe/Stockholm", ["MMX"] = "Europe/Stockholm",
            ["BMA"] = "Europe/Stockholm", ["NYO"] = "Europe/Stockholm",
            ["CPH"] = "Europe/Copenhagen", ["OSL"] = "Europe/Oslo", ["HEL"] = "Europe/Helsinki",
            ["LHR"] = "Europe/London", ["LGW"] = "Europe/London", ["STN"] = "Europe/London",
            ["CDG"] = "Europe/Paris", ["ORY"] = "Europe/Paris", ["NCE"] = "Europe/Paris",
            ["AMS"] = "Europe/Amsterdam", ["BRU"] = "Europe/Brussels",
            ["FRA"] = "Europe/Berlin", ["MUC"] = "Europe/Berlin", ["BER"] = "Europe/Berlin",
            ["MAD"] = "Europe/Madrid", ["BCN"] = "Europe/Madrid", ["AGP"] = "Europe/Madrid",
            ["FCO"] = "Europe/Rome", ["MXP"] = "Europe/Rome", ["VCE"] = "Europe/Rome",
            ["LIS"] = "Europe/Lisbon", ["OPO"] = "Europe/Lisbon",
            ["ATH"] = "Europe/Athens", ["IST"] = "Europe/Istanbul", ["VIE"] = "Europe/Vienna",
            ["ZRH"] = "Europe/Zurich", ["PRG"] = "Europe/Prague", ["WAW"] = "Europe/Warsaw",
            ["KEF"] = "Atlantic/Reykjavik", ["DUB"] = "Europe/Dublin",
            ["JFK"] = "America/New_York", ["EWR"] = "America/New_York", ["LGA"] = "America/New_York",
            ["LAX"] = "America/Los_Angeles", ["SFO"] = "America/Los_Angeles",
            ["ORD"] = "America/Chicago", ["MIA"] = "America/New_York",
            ["DXB"] = "Asia/Dubai", ["SIN"] = "Asia/Singapore", ["BKK"] = "Asia/Bangkok",
            ["HND"] = "Asia/Tokyo", ["NRT"] = "Asia/Tokyo",
            ["SYD"] = "Australia/Sydney", ["MEL"] = "Australia/Melbourne",
        };

    private static readonly IReadOnlyDictionary<string, int> Months =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            ["jan"] = 1, ["januari"] = 1, ["january"] = 1,
            ["feb"] = 2, ["februari"] = 2, ["february"] = 2,
            ["mar"] = 3, ["mars"] = 3, ["march"] = 3,
            ["apr"] = 4, ["april"] = 4,
            ["may"] = 5, ["maj"] = 5,
            ["jun"] = 6, ["juni"] = 6, ["june"] = 6,
            ["jul"] = 7, ["juli"] = 7, ["july"] = 7,
            ["aug"] = 8, ["augusti"] = 8, ["august"] = 8,
            ["sep"] = 9, ["sept"] = 9, ["september"] = 9,
            ["okt"] = 10, ["oct"] = 10, ["oktober"] = 10, ["october"] = 10,
            ["nov"] = 11, ["november"] = 11,
            ["dec"] = 12, ["december"] = 12,
        };

    /// <summary>
    /// Reads one date expression.
    /// </summary>
    /// <param name="text">The raw text from the document.</param>
    /// <param name="airportCode">
    /// An IATA code identified alongside it, when there is one. The ONLY thing
    /// that may produce a timezone.
    /// </param>
    public static GlunoExtractedDate Read(string text, string? airportCode = null)
    {
        var raw = (text ?? string.Empty).Trim();
        var timeZone = ResolveTimeZone(airportCode);
        var time = ReadTime(raw);

        // ── ISO first. Unambiguous by definition. ─────────────────────────
        var isoMatch = Regex.Match(raw, @"\b(\d{4})-(\d{2})-(\d{2})\b");
        if (isoMatch.Success && TryBuild(
                int.Parse(isoMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups[2].Value, CultureInfo.InvariantCulture),
                int.Parse(isoMatch.Groups[3].Value, CultureInfo.InvariantCulture),
                out var iso))
        {
            return new GlunoExtractedDate
            {
                OriginalText = raw,
                NormalisedDate = iso,
                NormalisedTime = time,
                TimeZoneId = timeZone,
                Confidence = 0.98,
            };
        }

        // ── Textual months. Also unambiguous — "5 Aug" cannot be read two
        // ways, which is exactly why booking systems print them.
        foreach (var (match, dayGroup, monthGroup) in new[]
        {
            (TextualDayFirst.Match(raw), 1, 2),
            (TextualMonthFirst.Match(raw), 2, 1),
        })
        {
            if (!match.Success) continue;
            if (!Months.TryGetValue(match.Groups[monthGroup].Value.TrimEnd('.'), out var month)) continue;

            if (TryBuild(
                    int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture),
                    month,
                    int.Parse(match.Groups[dayGroup].Value, CultureInfo.InvariantCulture),
                    out var textual))
            {
                return new GlunoExtractedDate
                {
                    OriginalText = raw,
                    NormalisedDate = textual,
                    NormalisedTime = time,
                    TimeZoneId = timeZone,
                    Confidence = 0.95,
                };
            }
        }

        // ── Numeric. The hard case. ───────────────────────────────────────
        var numeric = NumericDate.Match(raw);
        if (numeric.Success)
        {
            var first = int.Parse(numeric.Groups[1].Value, CultureInfo.InvariantCulture);
            var second = int.Parse(numeric.Groups[2].Value, CultureInfo.InvariantCulture);
            var year = NormaliseYear(int.Parse(numeric.Groups[3].Value, CultureInfo.InvariantCulture));

            var dayFirstWorks = TryBuild(year, second, first, out var dayFirst);
            var monthFirstWorks = TryBuild(year, first, second, out var monthFirst);

            // Both readings valid AND different. This is the case that must
            // NOT be resolved by us — the user is the only one who knows.
            if (dayFirstWorks && monthFirstWorks && dayFirst != monthFirst)
            {
                return new GlunoExtractedDate
                {
                    OriginalText = raw,
                    NormalisedDate = null,
                    NormalisedTime = time,
                    TimeZoneId = timeZone,
                    Confidence = 0.4,
                    AlternativeReadings = [dayFirst!, monthFirst!],
                };
            }

            // Only one reading is a real date — a "month" above twelve settles
            // it, and that is not a guess.
            var resolved = dayFirstWorks ? dayFirst : monthFirstWorks ? monthFirst : null;
            if (resolved != null)
            {
                return new GlunoExtractedDate
                {
                    OriginalText = raw,
                    NormalisedDate = resolved,
                    NormalisedTime = time,
                    TimeZoneId = timeZone,
                    Confidence = 0.9,
                };
            }
        }

        // Nothing readable. Zero confidence and the original text preserved —
        // the review screen shows what the document said and asks.
        return new GlunoExtractedDate
        {
            OriginalText = raw,
            NormalisedTime = time,
            TimeZoneId = timeZone,
            Confidence = 0,
        };
    }

    /// <summary>
    /// A timezone, ONLY from an airport code.
    ///
    /// Not from a city name, not from a country, not from the document's
    /// language. Each of those maps to many zones, and a wrong timezone on a
    /// flight is a wrong departure time.
    /// </summary>
    public static string? ResolveTimeZone(string? airportCode)
    {
        if (string.IsNullOrWhiteSpace(airportCode)) return null;
        return AirportTimeZones.GetValueOrDefault(airportCode.Trim());
    }

    /// <summary>
    /// Whether a journey crosses midnight, given both ends.
    ///
    /// An arrival "before" a departure is normal for a flight — an overnight,
    /// or a westward crossing. It is only an ERROR when both ends share a
    /// timezone and the arrival is still earlier, which is what the validator
    /// checks.
    /// </summary>
    public static bool CrossesMidnight(GlunoExtractedDate? start, GlunoExtractedDate? end)
    {
        if (start?.NormalisedDate == null || end?.NormalisedDate == null) return false;
        if (start.NormalisedTime == null || end.NormalisedTime == null) return false;

        return start.NormalisedDate == end.NormalisedDate
            && string.CompareOrdinal(end.NormalisedTime, start.NormalisedTime) < 0;
    }

    private static string? ReadTime(string text)
    {
        var match = TimePattern.Match(text);
        if (!match.Success) return null;

        var hour = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var meridiem = match.Groups[3].Value.ToLowerInvariant();

        if (meridiem == "pm" && hour < 12) hour += 12;
        if (meridiem == "am" && hour == 12) hour = 0;

        return hour > 23 ? null : $"{hour:00}:{minute:00}";
    }

    private static bool TryBuild(int year, int month, int day, out string? iso)
    {
        iso = null;
        if (month is < 1 or > 12 || day < 1) return false;
        if (year is < 1900 or > 2200) return false;
        if (day > DateTime.DaysInMonth(year, month)) return false;

        iso = new DateOnly(year, month, day).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return true;
    }

    /// Two-digit years. Booking documents are about the near future, so a
    /// two-digit year is this century.
    private static int NormaliseYear(int year) => year < 100 ? 2000 + year : year;
}
