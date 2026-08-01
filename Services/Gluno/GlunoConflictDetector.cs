using System.Globalization;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Two things the user wants that cannot both happen.
///
/// <see cref="Alternatives"/> is what separates this from a complaint. Telling
/// someone their plan does not work and stopping there is not help; the point
/// is to name the trade-off and hand them a choice they can actually make.
/// </summary>
public sealed record GlunoConflict(
    /// Stable code — "pace_vs_stops", "no_car_vs_distance".
    string Code,
    /// One sentence, in the user's language, naming both sides.
    string Explanation,
    /// One or two concrete ways forward. Never more: a list of five options is
    /// a decision handed back, not a recommendation.
    IReadOnlyList<string> Alternatives);

/// <summary>
/// Finds wishes that fight each other.
///
/// WHY THIS IS NOT LEFT TO THE MODEL. Agreeing is the path of least resistance
/// for a language model, and the failure is invisible: asked for a relaxed day
/// with eight stops, it produces a relaxed-sounding day with eight stops, and
/// the person finds out at 16:00 on a Tuesday. Detecting the contradiction
/// deterministically means Gluno cannot fail to notice — it only has to decide
/// how to say it.
///
/// Every conflict here is measured from data SideQuest already holds:
/// preferences the user stated, coordinates in the plan, the forecast, the
/// clock. Nothing is inferred about the person.
/// </summary>
public static class GlunoConflictDetector
{
    /// A day whose last stop ends after this, before a flight, is a short night.
    private const int LateEveningMinutes = 22 * 60;

    /// Departures before this need a genuinely early start.
    private const int EarlyDepartureMinutes = 8 * 60;

    /// <summary>
    /// Past this chance of rain, an outdoor day is a gamble rather than a plan.
    ///
    /// Deliberately high. SideQuest's forecasts reach several days out, where a
    /// 40% chance means very little, and an assistant that rewrites every
    /// slightly damp day is one people stop listening to.
    /// </summary>
    private const int LikelyRainPercent = 70;

    /// Conditions that make an outdoor day genuinely not work.
    private static readonly string[] WetConditions = ["rain", "heavy_rain", "thunderstorm", "snow"];

    public static IReadOnlyList<GlunoConflict> Detect(GlunoConflictInput input)
    {
        var swedish = string.Equals(input.Language, "sv", StringComparison.OrdinalIgnoreCase);
        var conflicts = new List<GlunoConflict>();

        // ── Relaxed pace against a packed request ─────────────────────────
        var (_, maxStops) = TripPaces.DayStopRange(input.Pace);
        if (input.RequestedStopCount > maxStops && input.Pace == TripPace.Relaxed)
        {
            conflicts.Add(new GlunoConflict(
                "pace_vs_stops",
                swedish
                    ? $"Du har sagt att ni vill ha ett lugnt tempo, men {input.RequestedStopCount} stopp på en dag blir inte lugnt."
                    : $"You've said you want a relaxed pace, but {input.RequestedStopCount} stops in a day won't feel relaxed.",
                swedish
                    ? [
                        $"Behåll de {maxStops} viktigaste den här dagen och lägg resten på en annan dag.",
                        "Eller kör den fulla listan och acceptera att dagen blir intensiv.",
                      ]
                    : [
                        $"Keep the {maxStops} that matter most today and move the rest to another day.",
                        "Or run the full list and accept that this day will be a busy one.",
                      ]));
        }

        // ── Budget against what is being suggested ────────────────────────
        if (input.BudgetIsLow && input.ExpensivePlaceCount > 0)
        {
            conflicts.Add(new GlunoConflict(
                "budget_vs_places",
                swedish
                    ? "Du har sagt att ni håller nere kostnaderna, men flera av de här ställena ligger i den dyrare änden."
                    : "You've said you're keeping costs down, but several of these places are at the pricier end.",
                swedish
                    ? [
                        "Jag kan leta billigare alternativ i samma område.",
                        "Eller behålla ett av dem som en kväll ni unnar er och hålla resten enkelt.",
                      ]
                    : [
                        "I can look for cheaper options in the same area.",
                        "Or keep one of them as the night you splash out and keep the rest simple.",
                      ]));
        }

        // ── No car against real distances ─────────────────────────────────
        if (input.Transport is { AvoidCar: true } or { CarAvailable: false }
            && input.LongestLegKm is { } longest
            && longest > Math.Max(input.Transport.EffectiveMaxWalkKm * 4, 8))
        {
            var reachable = input.Transport.AvoidTransit
                ? swedish ? "och du har sagt att ni helst slipper kollektivtrafik också" : "and you'd rather avoid public transport too"
                : swedish ? "men det går att lösa med kollektivtrafik" : "but public transport can cover it";

            conflicts.Add(new GlunoConflict(
                "no_car_vs_distance",
                swedish
                    ? $"Det längsta hoppet är runt {longest:0.#} km utan bil, {reachable}."
                    : $"The longest hop is around {longest:0.#} km without a car, {reachable}.",
                swedish
                    ? [
                        "Jag kan planera dagen närmare där ni bor istället.",
                        "Eller lägga det längre stoppet på en egen dag med kollektivtrafik dit.",
                      ]
                    : [
                        "I can plan the day closer to where you're staying instead.",
                        "Or give the far stop its own day and get there by public transport.",
                      ]));
        }

        // ── A late night before an early departure ────────────────────────
        if (input.DayEndsAtMinutes is { } endsAt
            && input.NextDayDepartureMinutes is { } departure
            && endsAt >= LateEveningMinutes
            && departure <= EarlyDepartureMinutes)
        {
            conflicts.Add(new GlunoConflict(
                "late_night_vs_early_departure",
                swedish
                    ? $"Kvällen slutar {Format(endsAt)} och ni ska iväg {Format(departure)} dagen efter — det blir en kort natt."
                    : $"The evening ends at {Format(endsAt)} and you leave at {Format(departure)} the next day — that's a short night.",
                swedish
                    ? [
                        "Jag kan flytta kvällens sista stopp tidigare.",
                        "Eller lägga den sena kvällen på en dag utan tidig avresa.",
                      ]
                    : [
                        "I can move the evening's last stop earlier.",
                        "Or put the late night on a day without an early departure.",
                      ]));
        }

        // ── An outdoor day in real rain ───────────────────────────────────
        var wet = input.WeatherCondition != null
            && WetConditions.Contains(input.WeatherCondition, StringComparer.OrdinalIgnoreCase);

        if (input.OutdoorStopCount > 0
            && wet
            && (input.PrecipitationProbability ?? 100) >= LikelyRainPercent)
        {
            var chance = input.PrecipitationProbability is { } probability
                ? swedish ? $"{probability}% chans för nederbörd" : $"a {probability}% chance of rain"
                : swedish ? "nederbörd i prognosen" : "rain in the forecast";

            conflicts.Add(new GlunoConflict(
                "outdoor_vs_weather",
                swedish
                    ? $"Prognosen visar {chance} den dagen, och planen är mest utomhus."
                    : $"The forecast shows {chance} that day, and this plan is mostly outdoors.",
                swedish
                    ? [
                        "Jag kan byta dag med en av de andra som passar bättre inomhus.",
                        "Eller hålla dagen men lägga in ett par inomhusalternativ som reserv.",
                      ]
                    : [
                        "I can swap this day with one that suits indoor plans better.",
                        "Or keep the day and add a couple of indoor fallbacks.",
                      ]));
        }

        return conflicts;
    }

    private static string Format(int minutes)
        => new TimeOnly(minutes / 60 % 24, minutes % 60).ToString("HH\\:mm", CultureInfo.InvariantCulture);
}

public sealed class GlunoConflictInput
{
    public TripPace Pace { get; init; } = TripPace.Balanced;
    public int RequestedStopCount { get; init; }

    /// From the stored budget preference, read as a constraint rather than a
    /// number — SideQuest has no prices, only the user's own words.
    public bool BudgetIsLow { get; init; }

    /// Suggested places the provider marked as expensive.
    public int ExpensivePlaceCount { get; init; }

    public TransportPreferences? Transport { get; init; }

    /// The longest straight-line hop in the proposed day.
    public double? LongestLegKm { get; init; }

    /// Minutes from midnight the day's last stop ends.
    public int? DayEndsAtMinutes { get; init; }

    /// Minutes from midnight of a transport Activity the following morning.
    public int? NextDayDepartureMinutes { get; init; }

    public int OutdoorStopCount { get; init; }

    /// SideQuest's own condition vocabulary — clear, rain, heavy_rain, snow,
    /// thunderstorm. Null when there is no forecast for that day, which is a
    /// different thing from good weather and is treated as such.
    public string? WeatherCondition { get; init; }
    public int? PrecipitationProbability { get; init; }

    public string Language { get; init; } = "en";
}
