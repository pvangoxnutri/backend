using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

public enum GlunoQualitySeverity
{
    /// Worth saying, not worth stopping for.
    Warning,
    /// Never leaves the backend as something the user can apply.
    Blocker,
}

public sealed record GlunoQualityIssue(
    GlunoQualitySeverity Severity,
    /// Stable machine code — "time_overlap", "fabricated_travel_time".
    string Code,
    /// One line, in the user's language, safe to show.
    string Message)
{
    /// The proposal it belongs to, when it came from one.
    public int? ActivityIndex { get; init; }
}

public sealed class GlunoQualityResult
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<GlunoQualityIssue> Blockers { get; init; }
    public required IReadOnlyList<GlunoQualityIssue> Warnings { get; init; }

    /// <summary>
    /// A repaired payload, when the gate could fix the problem without making a
    /// decision that is the user's to make.
    ///
    /// Null means "nothing safe to change". Dropping an optional extra stop is
    /// a correction; moving a booked dinner is not, and the gate will report
    /// that rather than fix it.
    /// </summary>
    public JsonElement? CorrectedPlan { get; init; }

    public required bool RequiresClarification { get; init; }

    /// One short line for the model to include in its answer, already in the
    /// user's language. Null when there is nothing to say.
    public string? UserFacingNote { get; init; }

    public static GlunoQualityResult Clean => new()
    {
        Passed = true,
        Blockers = Array.Empty<GlunoQualityIssue>(),
        Warnings = Array.Empty<GlunoQualityIssue>(),
        RequiresClarification = false,
    };
}

/// <summary>
/// The last deterministic check before anything reaches the user.
///
/// WHY A SEPARATE GATE. The schedule engine already refuses to build an
/// impossible day, and the action executor already validates every parameter.
/// This catches the third category: answers that are individually valid and
/// collectively wrong. A proposal that is well-formed but duplicates an
/// Activity already in the plan. A recommendation the user rejected two turns
/// ago. Text that says "I've added it" when nothing has been saved.
///
/// None of those are catchable by validating one object at a time, and all of
/// them are things a language model does under pressure to be helpful.
///
/// TWO SEVERITIES, AND THE LINE BETWEEN THEM. A blocker means the user could
/// tap apply and get something broken or untrue — those never leave as
/// applicable proposals. A warning means the plan is legitimate but worth a
/// sentence. When in doubt it is a warning: a gate that blocks too eagerly
/// turns into an assistant that refuses to do anything.
///
/// AUTOMATIC CORRECTION IS DELIBERATELY TIMID. It may drop an optional stop it
/// added itself. It may never move a fixed booking, change a date, or touch
/// anything the user created — those are decisions, and decisions belong to the
/// person whose trip it is.
/// </summary>
public sealed class GlunoQualityGate
{
    /// <summary>
    /// Phrases that claim something is already saved.
    ///
    /// The single most damaging thing Gluno can say. Nothing is written until
    /// the user taps apply, so "I've added it to Friday" is false at the moment
    /// it is said — and the user has no way to know that without going to look.
    /// </summary>
    /// <remarks>
    /// Matched against the ACCENT-FOLDED text, not the raw answer. "är nu
    /// inlagd" and "ar nu inlagd" have to be the same sentence to this check —
    /// a pattern that only catches one of them catches nothing in practice,
    /// since Gluno writes proper Swedish.
    /// </remarks>
    private static readonly Regex SavedClaimPattern = new(
        @"\b(" +
        // English
        @"i(?:'ve| have)? (?:now )?(?:added|created|saved|updated|moved|scheduled|booked|put)\b" +
        @"|(?:has|have) been (?:added|created|saved|updated|moved)\b" +
        @"|(?:it|that|this|they)(?:'s| is| are) (?:now )?(?:added|saved|in your|on your)\b" +
        // Swedish. Folded forms only: "är" arrives here as "ar".
        @"|jag har (?:nu )?(?:lagt|skapat|sparat|uppdaterat|flyttat|bokat)\b" +
        @"|(?:har|ar|blev) (?:nu )?(?:tillagd|tillagt|sparad|sparat|skapad|skapat|flyttad|flyttat|inlagd|inlagt)\b" +
        @"|nu (?:ligger|finns) (?:den|det|de)\b" +
        @")",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Phrases that state a travel time. Only legitimate when a verified leg
    /// backs them up.
    ///
    /// The gap between the number and the mode is bounded to word characters
    /// so the match cannot span two sentences — "20 minutes. Then walk" is not
    /// a claim that the walk takes 20 minutes.
    /// </summary>
    private static readonly Regex TravelTimePattern = new(
        @"\b\d{1,3}\s*(?:-\s*\d{1,3}\s*)?(?:min(?:ut(?:er|es?)?)?s?|timm(?:e|ar)|hours?|h)\b[a-z0-9 ]{0,25}\b" +
        @"(?:walk|walking|drive|driving|bus|train|metro|transit|promenad|gang|bil|buss|tag|tunnelbana)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Phrases that assert opening hours.
    /// </summary>
    private static readonly Regex OpeningHoursPattern = new(
        @"\b(?:open(?:s|ing)?|closed?|stang(?:er|t)|oppn(?:ar|et)|oppet|oppnar|stanger)\b[a-z0-9 ]{0,20}\b\d{1,2}[:.]\d{2}\b" +
        @"|\boppet nu\b|\bopen now\b|\bstangt nu\b|\bclosed now\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public GlunoQualityResult Check(GlunoQualityInput input)
    {
        var issues = new List<GlunoQualityIssue>();
        var swedish = string.Equals(input.Language, "sv", StringComparison.OrdinalIgnoreCase);
        JsonElement? corrected = null;
        var requiresClarification = false;

        // ── 1. Text-level honesty ─────────────────────────────────────────
        //
        // Checked FIRST because it is the only category that is wrong even when
        // everything else is right, and the only one the user cannot verify.
        if (!string.IsNullOrWhiteSpace(input.AnswerText))
        {
            // Folded once, matched three times. Accents must not decide
            // whether a false claim is caught.
            var answer = FoldForMatching(input.AnswerText);

            if (SavedClaimPattern.IsMatch(answer) && !input.SomethingWasApplied)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "claims_already_saved",
                    swedish
                        ? "Svaret påstår att något redan sparats. Inget sparas förrän användaren godkänner förslaget."
                        : "The answer claims something was saved. Nothing is saved until the user accepts the suggestion."));
            }

            if (TravelTimePattern.IsMatch(answer) && !input.HasVerifiedTravelTimes)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "fabricated_travel_time",
                    swedish
                        ? "Svaret anger en restid utan verifierad ruttdata bakom sig."
                        : "The answer states a travel time with no verified routing behind it."));
            }

            if (OpeningHoursPattern.IsMatch(answer) && !input.HasVerifiedOpeningHours)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "fabricated_opening_hours",
                    swedish
                        ? "Svaret anger öppettider som ingen provider har bekräftat."
                        : "The answer states opening hours no provider confirmed."));
            }
        }

        // ── 2. Was a proposal appropriate at all? ─────────────────────────
        if (input.ProducedProposal && !input.ExpectsProposal)
        {
            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "unrequested_proposal",
                swedish
                    ? "Användaren ställde en fråga men fick ett ändringsförslag."
                    : "The user asked a question and got a proposed change."));
        }

        // ── 3. The plan itself ────────────────────────────────────────────
        if (input.DayPlan is { } plan)
        {
            var (planIssues, repaired, needsQuestion) = CheckDayPlan(plan, input, swedish);
            issues.AddRange(planIssues);
            corrected = repaired;
            requiresClarification |= needsQuestion;
        }

        // ── 4. Findings the analyzer already produced ─────────────────────
        //
        // Reused rather than recomputed: TripAnalyzer is the one place that
        // knows what a bad plan looks like, and a second implementation here
        // would eventually disagree with it.
        foreach (var finding in input.Findings)
        {
            var severity = BlockingFindingTypes.Contains(finding.Type) && input.ProducedProposal
                ? GlunoQualitySeverity.Blocker
                : GlunoQualitySeverity.Warning;

            issues.Add(new GlunoQualityIssue(severity, finding.Type, finding.Explanation));
        }

        // ── 5. Things already said no to ──────────────────────────────────
        foreach (var suggested in input.SuggestedPlaceIds)
        {
            var rejected = input.RejectedOptions.FirstOrDefault(option =>
                string.Equals(option.Id, suggested, StringComparison.OrdinalIgnoreCase));

            if (rejected == null) continue;

            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "previously_rejected",
                swedish
                    ? $"{rejected.Label} har redan valts bort i den här konversationen."
                    : $"{rejected.Label} was already turned down in this conversation."));
        }

        // ── 6. Things already in the plan ─────────────────────────────────
        foreach (var suggested in input.SuggestedTitles)
        {
            var existing = input.ExistingTitles.FirstOrDefault(title =>
                string.Equals(Simplify(title), Simplify(suggested), StringComparison.Ordinal));

            if (existing == null) continue;

            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Warning, "already_in_plan",
                swedish
                    ? $"\"{existing}\" finns redan i planen."
                    : $"\"{existing}\" is already in the plan."));
        }

        var blockers = issues.Where(issue => issue.Severity == GlunoQualitySeverity.Blocker).ToList();
        var warnings = issues.Where(issue => issue.Severity == GlunoQualitySeverity.Warning).ToList();

        return new GlunoQualityResult
        {
            Passed = blockers.Count == 0,
            Blockers = blockers,
            Warnings = warnings,
            CorrectedPlan = corrected,
            RequiresClarification = requiresClarification,
            UserFacingNote = BuildNote(blockers, warnings, swedish),
        };
    }

    /// <summary>
    /// Findings that stop being advisory the moment they are inside something
    /// the user could tap "apply" on.
    ///
    /// A zigzag day the user built themselves is their business. A zigzag day
    /// Gluno is offering to create is Gluno's mistake.
    /// </summary>
    private static readonly HashSet<string> BlockingFindingTypes = new(StringComparer.Ordinal)
    {
        "time_overlap",
        "activity_outside_trip_dates",
        "invalid_stay",
        "activity_before_checkin",
        "activity_after_checkout",
    };

    private (List<GlunoQualityIssue> Issues, JsonElement? Corrected, bool NeedsQuestion) CheckDayPlan(
        JsonElement plan, GlunoQualityInput input, bool swedish)
    {
        var issues = new List<GlunoQualityIssue>();

        if (!plan.TryGetProperty("activities", out var activities) || activities.ValueKind != JsonValueKind.Array)
        {
            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "missing_proposal_fields",
                swedish ? "Förslaget saknar aktiviteter." : "The suggestion has no activities."));
            return (issues, null, false);
        }

        // The schedule engine already decided this. Trusting its verdict rather
        // than re-deriving one keeps a single source of truth about whether a
        // day works.
        if (plan.TryGetProperty("feasible", out var feasible) && feasible.ValueKind == JsonValueKind.False)
        {
            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "schedule_not_feasible",
                swedish
                    ? "Dagen går inte ihop som den ser ut nu."
                    : "The day does not work as it currently stands."));
        }

        var rows = activities.EnumerateArray().ToList();
        var titles = new List<string>();
        var droppable = new List<int>();

        var previousEnd = -1;
        var previousTitle = string.Empty;

        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            var title = ReadString(row, "title") ?? string.Empty;
            var isFixed = row.TryGetProperty("isFixed", out var fixedFlag) && fixedFlag.ValueKind == JsonValueKind.True;
            var isExisting = !string.IsNullOrWhiteSpace(ReadString(row, "existingActivityId"));

            // ── Required fields ───────────────────────────────────────────
            if (string.IsNullOrWhiteSpace(title))
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "missing_proposal_fields",
                    swedish ? "En rad saknar titel." : "One row has no title.") { ActivityIndex = index });
            }

            // ── Duplicates within the plan ────────────────────────────────
            if (titles.Any(existing => string.Equals(Simplify(existing), Simplify(title), StringComparison.Ordinal)))
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Warning, "duplicate_stop",
                    swedish ? $"\"{title}\" finns med två gånger." : $"\"{title}\" appears twice.")
                { ActivityIndex = index });

                if (!isFixed && !isExisting) droppable.Add(index);
            }

            titles.Add(title);

            // ── Overlaps ──────────────────────────────────────────────────
            var start = ReadMinutes(row, "time");
            var end = ReadMinutes(row, "endTime") ?? start;

            if (start is { } startMinutes && previousEnd >= 0 && startMinutes < previousEnd)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "time_overlap",
                    swedish
                        ? $"\"{title}\" börjar innan \"{previousTitle}\" är slut."
                        : $"\"{title}\" starts before \"{previousTitle}\" ends.")
                { ActivityIndex = index });

                // Correctable ONLY when the colliding row is something Gluno
                // itself added. A booking the user made stays exactly where it
                // is and becomes a question instead.
                if (!isFixed && !isExisting) droppable.Add(index);
            }

            if (end is { } endMinutes) previousEnd = endMinutes;
            if (!string.IsNullOrWhiteSpace(title)) previousTitle = title;

            // ── Travel that does not fit ──────────────────────────────────
            if (row.TryGetProperty("warnings", out var rowWarnings) && rowWarnings.ValueKind == JsonValueKind.Array)
            {
                foreach (var warning in rowWarnings.EnumerateArray())
                {
                    var code = warning.ValueKind == JsonValueKind.String ? warning.GetString() : null;
                    if (code == null) continue;

                    if (code == "not_enough_travel_time")
                    {
                        issues.Add(new GlunoQualityIssue(
                            GlunoQualitySeverity.Blocker, "travel_time_does_not_fit",
                            swedish
                                ? $"Det hinns inte med att ta sig till \"{title}\" i tid."
                                : $"There isn't time to reach \"{title}\".")
                        { ActivityIndex = index });
                    }
                }
            }

            // ── Closed places ─────────────────────────────────────────────
            var openingWarning = row.TryGetProperty("openingHours", out var hours)
                && hours.ValueKind == JsonValueKind.Object
                    ? ReadString(hours, "warning")
                    : null;

            if (openingWarning is "closed_that_day" or "closes_before_start")
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Blocker, "place_closed",
                    swedish
                        ? $"\"{title}\" är stängt då."
                        : $"\"{title}\" is closed then.")
                { ActivityIndex = index });

                if (!isFixed && !isExisting) droppable.Add(index);
            }
            else if (openingWarning == "closes_before_end")
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Warning, "closes_early",
                    swedish
                        ? $"\"{title}\" stänger innan besöket är slut."
                        : $"\"{title}\" closes before that visit ends.")
                { ActivityIndex = index });
            }

            // ── Unreasonably early or late ────────────────────────────────
            if (start is { } early && early < 6 * 60 && !isFixed)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Warning, "unreasonably_early",
                    swedish
                        ? $"\"{title}\" börjar väldigt tidigt."
                        : $"\"{title}\" starts very early.")
                { ActivityIndex = index });
            }

            if (start is { } late && late > 23 * 60 && !isFixed)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Warning, "unreasonably_late",
                    swedish
                        ? $"\"{title}\" börjar väldigt sent."
                        : $"\"{title}\" starts very late.")
                { ActivityIndex = index });
            }
        }

        // ── Day-level checks ──────────────────────────────────────────────
        var dayStart = rows.Select(row => ReadMinutes(row, "time")).Where(value => value != null).Min();
        var dayEnd = rows.Select(row => ReadMinutes(row, "endTime")).Where(value => value != null).Max();

        // A long day with no meal in it reads as written by someone who does
        // not eat.
        if (dayStart is { } from && dayEnd is { } to && to - from >= 7 * 60)
        {
            var hasMeal = rows.Any(row =>
                ActivityRoles.FromCategory(ReadString(row, "category"), null) == "meal");

            if (!hasMeal)
            {
                issues.Add(new GlunoQualityIssue(
                    GlunoQualitySeverity.Warning, "missing_meal",
                    swedish
                        ? "Dagen är lång men innehåller ingen måltid."
                        : "It's a long day with no meal in it."));
            }
        }

        // Pace. The schedule engine already caps this, so a breach here means
        // something bypassed it.
        var stopCount = rows.Count(row =>
            ActivityRoles.FromCategory(ReadString(row, "category"), null) is "activity" or "meal");
        var (_, maxStops) = TripPaces.DayStopRange(input.Pace);

        if (stopCount > maxStops)
        {
            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Warning, "too_many_stops_for_pace",
                swedish
                    ? $"{stopCount} stopp är mer än ett {PaceWord(input.Pace, true)} tempo brukar rymma."
                    : $"{stopCount} stops is more than a {PaceWord(input.Pace, false)} pace usually holds."));
        }

        // Zigzag: the analyzer's own rule, applied to the proposed order.
        var zigzag = CountLongHops(rows);
        if (zigzag >= 2)
        {
            issues.Add(new GlunoQualityIssue(
                GlunoQualitySeverity.Warning, "geographic_zigzag",
                swedish
                    ? "Dagen korsar staden fram och tillbaka."
                    : "The day crosses back and forth across town."));
        }

        // ── Correction ────────────────────────────────────────────────────
        //
        // Only ever REMOVES optional stops Gluno itself proposed. It never
        // moves anything, never changes a time, and never touches a row the
        // user owns.
        JsonElement? corrected = null;
        var needsQuestion = false;

        var removable = droppable.Distinct().OrderByDescending(index => index).ToList();
        if (removable.Count > 0)
        {
            var kept = rows
                .Where((_, index) => !removable.Contains(index))
                .Select(row => JsonSerializer.Deserialize<JsonElement>(row.GetRawText()))
                .ToList();

            if (kept.Count > 0)
            {
                corrected = Rebuild(plan, kept);
            }
            else
            {
                // Everything would have to go. That is a conversation, not a
                // correction.
                needsQuestion = true;
            }
        }
        else if (issues.Any(issue => issue.Severity == GlunoQualitySeverity.Blocker))
        {
            // Blocked, and nothing safe to remove — the clash is between things
            // the user owns.
            needsQuestion = true;
        }

        return (issues, corrected, needsQuestion);
    }

    /// Rebuilds the payload with a new activities array, everything else intact.
    private static JsonElement Rebuild(JsonElement plan, IReadOnlyList<JsonElement> activities)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();

            foreach (var property in plan.EnumerateObject())
            {
                if (property.NameEquals("activities")) continue;
                property.WriteTo(writer);
            }

            // Flagged so the model knows it must SAY something was dropped
            // rather than quietly presenting a shorter day.
            writer.WriteBoolean("autoCorrected", true);

            writer.WritePropertyName("activities");
            writer.WriteStartArray();
            foreach (var activity in activities) activity.WriteTo(writer);
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize<JsonElement>(stream.ToArray());
    }

    /// <summary>
    /// How many consecutive hops cross more than the analyzer's long-hop
    /// threshold. Two or more means the order is fighting the map.
    /// </summary>
    private static int CountLongHops(IReadOnlyList<JsonElement> rows)
    {
        var hops = 0;
        double? previousLat = null, previousLon = null;

        foreach (var row in rows)
        {
            var latitude = ReadNumber(row, "latitude");
            var longitude = ReadNumber(row, "longitude");

            if (latitude == null || longitude == null) continue;

            if (previousLat != null)
            {
                var distance = GeoDistance.KilometresBetween(previousLat, previousLon, latitude, longitude);
                if (distance is > 6) hops++;
            }

            previousLat = latitude;
            previousLon = longitude;
        }

        return hops;
    }

    /// <summary>
    /// One line the model is told to work into its answer.
    ///
    /// Deliberately short and deliberately not a list: the user gets the
    /// headline, and the proposal card carries the detail.
    /// </summary>
    private static string? BuildNote(
        IReadOnlyList<GlunoQualityIssue> blockers,
        IReadOnlyList<GlunoQualityIssue> warnings,
        bool swedish)
    {
        if (blockers.Count > 0) return blockers[0].Message;
        if (warnings.Count == 0) return null;
        if (warnings.Count == 1) return warnings[0].Message;

        return swedish
            ? $"{warnings[0].Message} Det finns {warnings.Count - 1} till att titta på."
            : $"{warnings[0].Message} There are {warnings.Count - 1} more things worth a look.";
    }

    private static string PaceWord(TripPace pace, bool swedish) => pace switch
    {
        TripPace.Relaxed => swedish ? "lugnt" : "relaxed",
        TripPace.Packed => swedish ? "intensivt" : "packed",
        _ => swedish ? "balanserat" : "balanced",
    };

    /// Lowercase, punctuation-free — so "Lunch" and "lunch," collide as they
    /// should when looking for duplicates.
    private static string Simplify(string? value)
        => GlunoIntentRouter.Normalise(value);

    /// <summary>
    /// Accent-folded and lowercased, but colons and periods SURVIVE.
    ///
    /// The router's normaliser strips them, which would turn "opens at 09:00"
    /// into "opens at 09 00" and make the opening-hours pattern match nothing.
    /// A check that silently stops firing is worse than no check.
    /// </summary>
    private static string FoldForMatching(string value)
    {
        var decomposed = value.ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var builder = new System.Text.StringBuilder(decomposed.Length);

        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static double? ReadNumber(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number ? value.GetDouble() : null;
    }

    private static int? ReadMinutes(JsonElement element, string name)
    {
        var text = ReadString(element, name);
        if (!TimeOnly.TryParseExact(text, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

        return parsed.Hour * 60 + parsed.Minute;
    }
}

public sealed class GlunoQualityInput
{
    /// The answer the model wrote. Null when checking a proposal on its own.
    public string? AnswerText { get; init; }

    /// The day-plan payload, when this turn produced one.
    public JsonElement? DayPlan { get; init; }

    public IReadOnlyList<TripFinding> Findings { get; init; } = Array.Empty<TripFinding>();

    public bool ProducedProposal { get; init; }
    public bool ExpectsProposal { get; init; }

    /// True only when an apply actually ran this turn. Almost always false —
    /// applying is a separate endpoint behind a user tap.
    public bool SomethingWasApplied { get; init; }

    public bool HasVerifiedTravelTimes { get; init; }
    public bool HasVerifiedOpeningHours { get; init; }

    public TripPace Pace { get; init; } = TripPace.Balanced;
    public string Language { get; init; } = "en";

    /// Namespaced provider ids this answer suggested.
    public IReadOnlyList<string> SuggestedPlaceIds { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> SuggestedTitles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> ExistingTitles { get; init; } = Array.Empty<string>();
    public IReadOnlyList<RejectedOption> RejectedOptions { get; init; } = Array.Empty<RejectedOption>();
}
