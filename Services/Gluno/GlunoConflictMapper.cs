using System.Text.Json;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Turns the quality gate's blockers into typed, answerable conflicts.
///
/// WHY A MAPPER AND NOT A SECOND DETECTOR. The gate already decides whether a
/// schedule works — overlaps, travel time, opening hours, trip dates, check-in
/// windows. Writing that again for clarifications would give two answers to
/// one question, and they would drift: a plan the gate blocks and the
/// conflict layer thinks is fine is a plan that can never be applied and never
/// be fixed.
///
/// So this only translates. It adds no judgement about feasibility — only
/// about what may be OFFERED, which is the part the gate has no opinion on.
///
/// Deterministic and pure. No model, no provider, no database.
/// </summary>
public static class GlunoConflictMapper
{
    /// <summary>
    /// Which gate blocker means which conflict.
    ///
    /// Anything absent is a blocker with no safe strategy — a fabricated
    /// travel time, a claim the plan was already saved. Those are Gluno's own
    /// mistakes and are never offered to the user as a choice; the turn falls
    /// back rather than asking somebody to pick a fix for a bug.
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ByCode =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["time_overlap"] = GlunoConflictTypes.TimeOverlap,
            ["not_enough_travel_time"] = GlunoConflictTypes.InsufficientTravelTime,
            ["travel_time_does_not_fit"] = GlunoConflictTypes.InsufficientTravelTime,
            ["activity_outside_trip_dates"] = GlunoConflictTypes.OutsideTripDates,
            ["activity_before_checkin"] = GlunoConflictTypes.CheckInConflict,
            ["activity_after_checkout"] = GlunoConflictTypes.CheckOutConflict,
            ["place_closed"] = GlunoConflictTypes.OutsideOpeningHours,
            ["closes_early"] = GlunoConflictTypes.OutsideOpeningHours,
            ["too_many_stops_for_pace"] = GlunoConflictTypes.DayCapacityExceeded,
            ["duplicate_stop"] = GlunoConflictTypes.DuplicateActivity,
            ["already_in_plan"] = GlunoConflictTypes.DuplicateActivity,
        };

    /// True when this blocker is something the user can sensibly choose about.
    public static bool IsAnswerable(string code) => ByCode.ContainsKey(code);

    /// <summary>
    /// The conflicts a validation produced, most blocking first.
    ///
    /// One at a time is the rule the caller follows: resolving the worst often
    /// removes the rest, and asking three questions about one suggestion is
    /// how a chat becomes a wizard.
    /// </summary>
    public static IReadOnlyList<GlunoProposalConflict> From(
        GlunoQualityResult validation,
        int conflictVersion,
        Func<int, bool>? existingIsMovable = null,
        JsonElement? dayPlan = null,
        IReadOnlyList<int>? destinationMismatches = null)
    {
        var rows = DayPlanRows.Read(dayPlan);
        var conflicts = new List<GlunoProposalConflict>();

        foreach (var blocker in validation.Blockers)
        {
            if (!ByCode.TryGetValue(blocker.Code, out var type)) continue;

            var index = blocker.ActivityIndex ?? -1;
            var row = rows.At(index);
            // What a time clash actually collides WITH: the nearest earlier row
            // that has a time. The gate compares against exactly that row, so
            // this is the same neighbour, not a second guess at one.
            var collidesWith = rows.PreviousTimed(index);

            // ── Is this really a locked booking? ──────────────────────────
            //
            // Not a new lock concept — the gate already marks a row `isFixed`
            // when it is a booking with a time, and `existingActivityId` when
            // it is already in the Adventure. A clash with one of those is a
            // different conflict from a clash between two suggestions, because
            // the answers differ: nothing may be offered that moves, replaces
            // or removes it.
            if (type == GlunoConflictTypes.TimeOverlap && collidesWith is { IsLocked: true })
            {
                type = GlunoConflictTypes.LockedBooking;
            }

            // ── How short the gap actually is ─────────────────────────────
            //
            // "There isn't time to get between them" is true and unhelpful.
            // "25 minutes short" tells somebody whether to move one thing or
            // give up on the day — and both figures come off the plan the
            // schedule engine produced, so neither is an estimate made here.
            var required = row?.TravelFromPreviousMinutes ?? 0;
            var available = collidesWith?.End is { } previousEnd && row?.Start is { } rowStart
                ? Math.Max(0, (int)(rowStart - previousEnd).TotalMinutes)
                : 0;

            conflicts.Add(new GlunoProposalConflict
            {
                ConflictType = type,
                AffectedDraftItemIndexes = index >= 0 ? [index] : [],
                RequiredTravelMinutes = type == GlunoConflictTypes.InsufficientTravelTime ? required : 0,
                AvailableMinutes = type == GlunoConflictTypes.InsufficientTravelTime ? available : 0,
                // The real Activity behind the clash, so the card can name what
                // it collides with instead of saying "something".
                AffectedExistingActivityIds = collidesWith?.ExistingActivityId is { } activityId
                    ? [activityId]
                    : [],
                Date = rows.Date,
                StartTime = row?.Time,
                EndTime = row?.EndTime,
                ConflictVersion = conflictVersion,
                // A check-in, a check-out and a booking with a reference are
                // never ours to move. Read from the plan itself where it says
                // so; absent an answer the safe assumption is immovable.
                ExistingIsMovable = type is not (
                    GlunoConflictTypes.CheckInConflict
                    or GlunoConflictTypes.CheckOutConflict
                    or GlunoConflictTypes.LockedBooking)
                    && (collidesWith?.IsLocked != true)
                    && (existingIsMovable?.Invoke(index) ?? false),
                // "closes_early" is a warning about hours we DO know. Only an
                // unknown-hours blocker is genuinely uncertain.
                HoursAreUncertain = blocker.Code == "unknown_hours",
                IsResolvable = true,
            });
        }

        // ── The one conflict the gate does not produce ────────────────────
        //
        // The quality gate checks whether a day WORKS — times, travel, hours,
        // capacity. It has no opinion on where the trip is, because that lives
        // in the destination timeline rather than in the day plan.
        //
        // So this is added here, from stored coordinates only. It is the same
        // kind of statement as the rest — a verified fact about the plan — and
        // it is deliberately hard to trigger: see GlunoDestinationCheck.
        if (destinationMismatches is { Count: > 0 })
        {
            conflicts.Add(new GlunoProposalConflict
            {
                ConflictType = GlunoConflictTypes.WrongDestinationDay,
                AffectedDraftItemIndexes = destinationMismatches,
                Date = rows.Date,
                StartTime = rows.At(destinationMismatches[0])?.Time,
                ConflictVersion = conflictVersion,
                // The trip's own location is not something a suggestion may
                // move. The day changes, or the suggestion does.
                ExistingIsMovable = false,
                IsResolvable = true,
            });
        }

        return conflicts
            .OrderBy(conflict => conflict.Severity)
            .ThenBy(conflict => conflict.ConflictType, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>
    /// The day plan's rows, read once, for the two questions the mapper needs
    /// answering: which row is affected, and what it collides with.
    ///
    /// Read defensively. This runs on a live turn against a payload the model
    /// produced, so an absent property is a missing answer, never an exception.
    /// </summary>
    private sealed record DayPlanRow(
        int Index, string? Time, string? EndTime, bool IsFixed, Guid? ExistingActivityId,
        int? TravelFromPreviousMinutes)
    {
        public TimeOnly? Start => GlunoDraftPlan.ParseTime(Time);
        public TimeOnly? End => GlunoDraftPlan.ParseTime(EndTime);


        /// <summary>
        /// True when this row is not Gluno's to move.
        ///
        /// Both halves matter. `isFixed` is a booking with a time — a table, a
        /// train. `existingActivityId` is something already in the Adventure,
        /// which a suggestion has no business rearranging without being asked.
        /// </summary>
        public bool IsLocked => IsFixed || ExistingActivityId.HasValue;
    }

    private sealed class DayPlanRows
    {
        private static readonly DayPlanRows Empty = new(new List<DayPlanRow>(), null);

        private readonly IReadOnlyList<DayPlanRow> _rows;

        private DayPlanRows(IReadOnlyList<DayPlanRow> rows, string? date)
        {
            _rows = rows;
            Date = date;
        }

        public string? Date { get; }

        public DayPlanRow? At(int index)
            => index >= 0 && index < _rows.Count ? _rows[index] : null;

        /// The nearest earlier row with a time — what the gate compared against.
        public DayPlanRow? PreviousTimed(int index)
        {
            for (var candidate = index - 1; candidate >= 0; candidate--)
            {
                if (_rows[candidate].Time != null) return _rows[candidate];
            }

            return null;
        }

        public static DayPlanRows Read(JsonElement? dayPlan)
        {
            if (dayPlan is not { ValueKind: JsonValueKind.Object } plan) return Empty;

            var date = plan.TryGetProperty("date", out var dateElement)
                && dateElement.ValueKind == JsonValueKind.String
                    ? dateElement.GetString()
                    : null;

            if (!plan.TryGetProperty("activities", out var activities)
                || activities.ValueKind != JsonValueKind.Array)
            {
                return new DayPlanRows(new List<DayPlanRow>(), date);
            }

            var rows = new List<DayPlanRow>();
            var index = 0;

            foreach (var entry in activities.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object)
                {
                    index++;
                    continue;
                }

                rows.Add(new DayPlanRow(
                    index++,
                    Text(entry, "time"),
                    Text(entry, "endTime"),
                    entry.TryGetProperty("isFixed", out var fixedFlag)
                        && fixedFlag.ValueKind == JsonValueKind.True,
                    Guid.TryParse(Text(entry, "existingActivityId"), out var activityId)
                        ? activityId
                        : null,
                    entry.TryGetProperty("travelFromPrevious", out var travel)
                        && travel.ValueKind == JsonValueKind.Object
                        && travel.TryGetProperty("minutes", out var minutes)
                        && minutes.ValueKind == JsonValueKind.Number
                            ? minutes.GetInt32()
                            : null));
            }

            return new DayPlanRows(rows, date);
        }

        private static string? Text(JsonElement element, string name)
            => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
    }

    /// <summary>
    /// The one conflict to ask about now, or null when there is nothing to ask.
    ///
    /// Returns null when the validation blocked for a reason the user cannot
    /// choose about — the caller then falls back rather than offering a card
    /// whose only option is Cancel.
    /// </summary>
    public static GlunoProposalConflict? MostBlocking(
        IReadOnlyList<GlunoProposalConflict> conflicts)
        => conflicts.Count == 0 ? null : conflicts[0];

    /// <summary>
    /// True when exactly one strategy could work, so it can be applied without
    /// asking.
    ///
    /// A card whose only real option is "skip it" is not a choice — it is a
    /// notification with extra steps.
    /// </summary>
    public static string? OnlySafeStrategy(GlunoProposalConflict conflict)
    {
        var real = conflict.AllowedStrategies
            .Where(strategy => strategy != GlunoConflictStrategies.Cancel)
            .ToList();

        return real.Count == 1 ? real[0] : null;
    }

    /// <summary>
    /// The options a conflict becomes, in the user's language.
    ///
    /// Filtered to what the continuation can carry out. A strategy the conflict
    /// permits but the server cannot yet perform is not shown: an option that
    /// errors on tap is worse than one that was never offered.
    /// </summary>
    public static IReadOnlyList<GlunoOptionDraft> Options(
        GlunoProposalConflict conflict, string language)
        => conflict.AllowedStrategies
            .Where(GlunoConflictStrategies.IsSupported)
            .Select((strategy, index) => new GlunoOptionDraft(
                strategy, GlunoConflictStrategies.Label(strategy, language))
            {
                EntityType = GlunoClarificationEntityTypes.Enum,
                Value = strategy,
            })
            .Take(GlunoClarificationBuilder.MaxOptions)
            .ToList();
}
