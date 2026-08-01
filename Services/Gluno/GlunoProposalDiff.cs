using System.Globalization;
using System.Text.Json;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One thing the user changed before applying.
///
/// <see cref="Value"/> is a CATEGORY, never the actual content: "later" rather
/// than "10:00", "removed" rather than the name of the stop. That is what makes
/// the diff safe to log and safe to count, and it is all the candidate
/// arithmetic needs — the pattern worth learning is "they keep pushing the
/// start later", not which morning or which museum.
/// </summary>
public sealed record GlunoProposalChange(string Field, string Value)
{
    /// <summary>
    /// True when this looks like the user's own decision rather than the
    /// server tidying up.
    ///
    /// Load-bearing: the schedule engine rounds durations to quarter hours and
    /// re-lays travel times on every edit. Counting those as choices would
    /// teach Gluno preferences nobody expressed.
    /// </summary>
    public bool IsUserIntent { get; init; } = true;
}

public sealed record GlunoProposalDiffResult(IReadOnlyList<GlunoProposalChange> Changes)
{
    public bool HasUserEdits => Changes.Any(change => change.IsUserIntent);

    /// Categories only, for telemetry. Never the values.
    public IReadOnlyList<string> Categories =>
        Changes.Where(change => change.IsUserIntent)
            .Select(change => change.Field)
            .Distinct(StringComparer.Ordinal)
            .ToList();
}

/// <summary>
/// Works out what the user changed about a proposal before applying it.
///
/// WHY STRUCTURED FIELDS AND NOT TEXT. Comparing rendered summaries would treat
/// a re-worded title as a change and a moved stop as none. The fields are what
/// the user actually manipulated, and they are what the schedule engine
/// re-derives — so the diff can also tell the two apart.
///
/// THE NORMALISATION PROBLEM, which is the interesting part. Editing one start
/// time causes the engine to recompute every subsequent start, every travel
/// leg, and possibly the day's order. A naive diff sees eight changes and
/// concludes the user rewrote the day. Only the field they touched is intent;
/// everything downstream is arithmetic, and marking it as intent would build
/// preference candidates out of the server's own behaviour.
/// </summary>
public static class GlunoProposalDiff
{
    // ── Change categories ────────────────────────────────────────────────
    public const string StartTime = "start_time";
    public const string Duration = "duration";
    public const string Day = "day";
    public const string Order = "order";
    public const string TransportMode = "transport_mode";
    public const string RemovedActivity = "removed_activity";
    public const string AddedActivity = "added_activity";
    public const string Location = "location";
    public const string Title = "title";
    public const string Pace = "pace";
    public const string BudgetLevel = "budget_level";
    public const string MealTime = "meal_time";

    /// <summary>
    /// Compares the proposal as Gluno built it against what the user applied.
    /// </summary>
    public static GlunoProposalDiffResult Compare(JsonElement original, JsonElement edited)
    {
        var changes = new List<GlunoProposalChange>();

        CompareScalar(original, edited, "date", Day, changes);
        CompareScalar(original, edited, "transportMode", TransportMode, changes);
        CompareScalar(original, edited, "pace", Pace, changes);

        var originalRows = ReadRows(original);
        var editedRows = ReadRows(edited);

        // ── Removals ──────────────────────────────────────────────────────
        //
        // Matched by TITLE rather than index: removing the second of four stops
        // shifts every later index, and an index comparison would report three
        // changes where there was one.
        var editedTitles = editedRows.Select(row => Simplify(row.Title)).ToHashSet(StringComparer.Ordinal);

        foreach (var row in originalRows)
        {
            if (editedTitles.Contains(Simplify(row.Title))) continue;
            changes.Add(new GlunoProposalChange(RemovedActivity, "removed"));
        }

        // ── Additions ─────────────────────────────────────────────────────
        //
        // Recorded so that "they changed it" is true, but deliberately mapped
        // to no preference: adding a stop says what somebody wanted THAT day,
        // not how they travel.
        var originalTitles = originalRows.Select(row => Simplify(row.Title)).ToHashSet(StringComparer.Ordinal);

        foreach (var row in editedRows)
        {
            if (originalTitles.Contains(Simplify(row.Title))) continue;
            changes.Add(new GlunoProposalChange(AddedActivity, "added"));
        }

        // ── Reordering ────────────────────────────────────────────────────
        var survivingOriginal = originalRows
            .Where(row => editedTitles.Contains(Simplify(row.Title)))
            .Select(row => Simplify(row.Title))
            .ToList();

        var survivingEdited = editedRows
            .Select(row => Simplify(row.Title))
            .Where(title => survivingOriginal.Contains(title, StringComparer.Ordinal))
            .ToList();

        if (!survivingOriginal.SequenceEqual(survivingEdited, StringComparer.Ordinal))
        {
            changes.Add(new GlunoProposalChange(Order, "reordered"));
        }

        // ── Per-row edits ─────────────────────────────────────────────────
        //
        // Only the FIRST changed start time counts as intent. The engine
        // cascades every later start from it, and counting the cascade would
        // read one decision as five.
        var startTimeCounted = false;

        foreach (var row in editedRows)
        {
            var before = originalRows.FirstOrDefault(
                candidate => Simplify(candidate.Title) == Simplify(row.Title));

            if (before == null) continue;

            if (before.Time != row.Time && row.Time != null)
            {
                changes.Add(new GlunoProposalChange(
                    IsMeal(row.Category) ? MealTime : StartTime,
                    Direction(before.Time, row.Time))
                {
                    IsUserIntent = !startTimeCounted,
                });

                startTimeCounted = true;
            }

            if (before.DurationMinutes != row.DurationMinutes && row.DurationMinutes.HasValue)
            {
                changes.Add(new GlunoProposalChange(
                    Duration,
                    row.DurationMinutes > before.DurationMinutes ? "longer" : "shorter"));
            }

            if (!string.Equals(before.Title, row.Title, StringComparison.Ordinal)
                && Simplify(before.Title) == Simplify(row.Title))
            {
                // Same stop, different wording. Cosmetic, and not a preference
                // about anything.
                changes.Add(new GlunoProposalChange(Title, "reworded") { IsUserIntent = false });
            }

            if (before.LocationLabel != row.LocationLabel && row.LocationLabel != null)
            {
                changes.Add(new GlunoProposalChange(Location, "changed"));
            }
        }

        return new GlunoProposalDiffResult(changes);
    }

    /// <summary>
    /// Which preference key a change could eventually support, if it keeps
    /// happening.
    ///
    /// Returns null for most of them. A reordered day, a renamed stop and a
    /// changed location say something about that PLAN, not about how the person
    /// travels — and manufacturing a preference from them is exactly the
    /// overreach the candidate system exists to prevent.
    /// </summary>
    public static (string Key, string Value)? ToCandidateSignal(GlunoProposalChange change)
        => (change.Field, change.Value) switch
        {
            (StartTime, "later") => (GlunoPreferenceKeys.StartTime, "later start"),
            (StartTime, "earlier") => (GlunoPreferenceKeys.StartTime, "earlier start"),
            (Duration, "longer") => (GlunoPreferenceKeys.Pace, "relaxed"),
            (RemovedActivity, "removed") => (GlunoPreferenceKeys.Pace, "relaxed"),
            (TransportMode, _) => (GlunoPreferenceKeys.Transport, change.Value),
            (BudgetLevel, _) => (GlunoPreferenceKeys.Budget, change.Value),
            _ => null,
        };

    private sealed record Row(string Title, string? Time, int? DurationMinutes, string? Category, string? LocationLabel);

    private static IReadOnlyList<Row> ReadRows(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return [];

        // A single-activity proposal is one row; a day plan is a list. Reading
        // both the same way keeps the diff logic in one place.
        if (!payload.TryGetProperty("activities", out var activities)
            || activities.ValueKind != JsonValueKind.Array)
        {
            var title = ReadString(payload, "title");
            return title == null
                ? []
                : [new Row(
                    title,
                    ReadString(payload, "time"),
                    ReadInt(payload, "durationMinutes"),
                    ReadString(payload, "category"),
                    ReadString(payload, "locationLabel"))];
        }

        return activities.EnumerateArray()
            .Where(row => row.ValueKind == JsonValueKind.Object)
            .Select(row => new Row(
                ReadString(row, "title") ?? "",
                ReadString(row, "time"),
                ReadInt(row, "durationMinutes"),
                ReadString(row, "category"),
                ReadString(row, "locationLabel")))
            .Where(row => row.Title.Length > 0)
            .ToList();
    }

    private static void CompareScalar(
        JsonElement original, JsonElement edited, string property, string field, List<GlunoProposalChange> changes)
    {
        var before = ReadString(original, property);
        var after = ReadString(edited, property);

        if (before == after || after == null) return;

        changes.Add(new GlunoProposalChange(field, field == Day ? "moved" : after));
    }

    /// "later" / "earlier" rather than the clock time. The direction is the
    /// pattern; the time is somebody's morning.
    private static string Direction(string? before, string? after)
    {
        if (before == null || after == null) return "changed";

        var parsedBefore = ParseMinutes(before);
        var parsedAfter = ParseMinutes(after);

        if (parsedBefore == null || parsedAfter == null) return "changed";

        return parsedAfter > parsedBefore ? "later" : "earlier";
    }

    private static int? ParseMinutes(string value)
        => TimeOnly.TryParseExact(value, "HH\\:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed.Hour * 60 + parsed.Minute
            : null;

    private static bool IsMeal(string? category)
        => ActivityRoles.FromCategory(category, null) == "meal";

    private static string Simplify(string? value) => GlunoIntentRouter.Normalise(value);

    private static string? ReadString(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        if (value.ValueKind != JsonValueKind.String) return null;
        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value)) return null;
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var parsed) ? parsed : null;
    }
}
