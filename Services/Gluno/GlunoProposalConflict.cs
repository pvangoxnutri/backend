namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The ways a suggested change can fail against the plan that already exists.
///
/// These are not new detections. Every one maps from a blocker the quality
/// gate already produces — the deterministic validation that runs on every
/// proposal — so there is exactly one implementation of "is this schedule
/// workable" and this file only decides what can be OFFERED about it.
/// </summary>
public static class GlunoConflictTypes
{
    /// Two things at once.
    public const string TimeOverlap = "time_overlap";
    /// The gap between them is shorter than the journey.
    public const string InsufficientTravelTime = "insufficient_travel_time";
    /// A day the Adventure does not cover.
    public const string OutsideTripDates = "outside_trip_dates";
    /// The right day for a different town.
    public const string WrongDestinationDay = "wrong_destination_day";
    /// A booking with a reference number. Never movable from here.
    public const string LockedBooking = "locked_booking";
    public const string CheckInConflict = "check_in_conflict";
    public const string CheckOutConflict = "check_out_conflict";
    public const string OutsideOpeningHours = "outside_opening_hours";
    /// More in the day than the pace allows.
    public const string DayCapacityExceeded = "day_capacity_exceeded";
    public const string DuplicateActivity = "duplicate_activity";

    public static readonly IReadOnlyList<string> All =
    [
        TimeOverlap, InsufficientTravelTime, OutsideTripDates, WrongDestinationDay,
        LockedBooking, CheckInConflict, CheckOutConflict, OutsideOpeningHours,
        DayCapacityExceeded, DuplicateActivity,
    ];

    public static bool IsKnown(string? type) => type != null && All.Contains(type);

    /// <summary>
    /// How much of the plan a conflict breaks, for ordering.
    ///
    /// Lower is worse. When several conflicts land at once the most blocking
    /// is asked about first: resolving it often removes the others, and asking
    /// about a capacity warning while the day is also outside the trip's dates
    /// is asking the wrong question.
    /// </summary>
    public static int Severity(string type) => type switch
    {
        OutsideTripDates or LockedBooking or CheckInConflict or CheckOutConflict => 0,
        TimeOverlap or InsufficientTravelTime => 1,
        WrongDestinationDay or OutsideOpeningHours => 2,
        DuplicateActivity => 3,
        _ => 4,
    };
}

/// <summary>
/// What the user may be offered about a conflict.
///
/// A closed list, and the point of this file: the model cannot add to it, and
/// nothing here is offered unless the validator agrees it could actually work.
/// </summary>
public static class GlunoConflictStrategies
{
    /// Move the thing being suggested.
    public const string MoveNew = "move_new";
    /// Move what is already planned.
    public const string MoveExisting = "move_existing";
    public const string ChooseAnotherDay = "choose_another_day";
    public const string ChooseAnotherTime = "choose_another_time";
    /// Fit it by making it shorter.
    public const string Shorten = "shorten";
    /// Leave the clash in place, when the day still works.
    public const string KeepBoth = "keep_both";
    public const string RemoveNew = "remove_new";
    public const string ReplaceExisting = "replace_existing";
    public const string Cancel = "cancel";

    public static readonly IReadOnlyList<string> All =
    [
        MoveNew, MoveExisting, ChooseAnotherDay, ChooseAnotherTime,
        Shorten, KeepBoth, RemoveNew, ReplaceExisting, Cancel,
    ];

    public static bool IsKnown(string? strategy) => strategy != null && All.Contains(strategy);

    /// <summary>
    /// The strategies the continuation can actually carry out today.
    ///
    /// WHY THIS EXISTS AND IS NOT JUST A COMMENT. <see cref="For"/> answers what
    /// a conflict PERMITS — a question about the plan. This answers what the
    /// server can DO — a question about the build. They are different, and
    /// conflating them is how an option becomes a dead end: the user taps
    /// "Pick another day", waits, and gets an error, which reads as the product
    /// being broken rather than unfinished.
    ///
    /// Offering only what can be honoured means a card is always answerable.
    /// The unbuilt strategies stay in <see cref="For"/> so the permission rules
    /// remain complete and their tests keep passing; they simply are not shown.
    /// </summary>
    public static bool IsSupported(string strategy) => strategy
        is RemoveNew            // Drop the suggested row.
        or KeepBoth             // Accept the clash, record it so it is not re-asked.
        or Cancel               // Always. Backing out is never unavailable.
        or ChooseAnotherDay     // Day chooser, built from days that actually work.
        or ChooseAnotherTime    // Time chooser, built from the schedule engine's rules.
        or MoveNew              // The time chooser without the tap: first slot that fits.
        or Shorten              // Trimmed as little as possible, never below the minimum.
        or MoveExisting         // Recorded as a draft operation. No write until apply.
        or ReplaceExisting;     // Same, and never for anything locked.

    /// <summary>
    /// Which strategies a conflict can actually offer.
    ///
    /// THE RULE: never offer something the validator already knows is
    /// impossible. A strategy that cannot work is worse than no strategy — the
    /// user taps it, waits, and gets the same card back, which reads as the
    /// product not listening.
    ///
    /// `Cancel` is always last and always present. Backing out of a suggestion
    /// must never be unavailable.
    /// </summary>
    public static IReadOnlyList<string> For(GlunoProposalConflict conflict)
    {
        var strategies = new List<string>();

        switch (conflict.ConflictType)
        {
            // A booking with a reference number belongs to somebody else's
            // system. Offering to move it would be offering something Gluno
            // cannot do, and doing it would desynchronise the plan from a real
            // reservation.
            case GlunoConflictTypes.LockedBooking:
            case GlunoConflictTypes.CheckInConflict:
            case GlunoConflictTypes.CheckOutConflict:
                strategies.Add(MoveNew);
                strategies.Add(ChooseAnotherDay);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.TimeOverlap:
                strategies.Add(MoveNew);
                // Only when the thing already there is ours to move.
                if (conflict.ExistingIsMovable) strategies.Add(MoveExisting);
                strategies.Add(ChooseAnotherTime);
                strategies.Add(ChooseAnotherDay);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.InsufficientTravelTime:
                strategies.Add(MoveNew);
                if (conflict.ExistingIsMovable) strategies.Add(MoveExisting);
                // Shortening only helps when the stop has a length to shorten.
                if (conflict.AvailableMinutes > 0) strategies.Add(Shorten);
                strategies.Add(ChooseAnotherDay);
                strategies.Add(RemoveNew);
                break;

            // The day is not part of the Adventure. Nothing about the day's
            // shape can fix that, and the trip is never extended silently.
            case GlunoConflictTypes.OutsideTripDates:
                strategies.Add(ChooseAnotherDay);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.WrongDestinationDay:
                strategies.Add(ChooseAnotherDay);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.OutsideOpeningHours:
                strategies.Add(ChooseAnotherTime);
                strategies.Add(ChooseAnotherDay);
                // Only when the hours are genuinely uncertain. Definitely shut
                // is not something to plan around by ignoring it.
                if (conflict.HoursAreUncertain) strategies.Add(KeepBoth);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.DayCapacityExceeded:
                strategies.Add(ChooseAnotherDay);
                strategies.Add(Shorten);
                // A full day is a judgement, not an impossibility.
                strategies.Add(KeepBoth);
                strategies.Add(RemoveNew);
                break;

            case GlunoConflictTypes.DuplicateActivity:
                strategies.Add(RemoveNew);
                if (conflict.ExistingIsMovable) strategies.Add(ReplaceExisting);
                strategies.Add(KeepBoth);
                break;

            default:
                strategies.Add(RemoveNew);
                break;
        }

        // Belt and braces: a conflict the validator says is unresolvable
        // offers nothing but backing out, whatever the switch produced.
        if (!conflict.IsResolvable) strategies.Clear();

        strategies.Add(Cancel);

        return strategies.Distinct().ToList();
    }

    /// The user-facing label, in their language.
    public static string Label(string strategy, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return strategy switch
        {
            MoveNew => swedish ? "Flytta den nya" : "Move the new one",
            MoveExisting => swedish ? "Flytta den befintliga" : "Move the existing one",
            ChooseAnotherDay => swedish ? "Välj en annan dag" : "Pick another day",
            ChooseAnotherTime => swedish ? "Välj en annan tid" : "Pick another time",
            Shorten => swedish ? "Gör den kortare" : "Make it shorter",
            KeepBoth => swedish ? "Behåll båda" : "Keep both",
            RemoveNew => swedish ? "Hoppa över den" : "Skip it",
            ReplaceExisting => swedish ? "Ersätt den befintliga" : "Replace the existing one",
            Cancel => swedish ? "Avbryt" : "Cancel",
            _ => swedish ? "Avbryt" : "Cancel",
        };
    }

    /// <summary>
    /// One sentence naming what does not fit.
    ///
    /// No times, no titles, no places — the card shows those separately, and
    /// this string also reaches the log line.
    /// </summary>
    public static string Explain(string conflictType, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return conflictType switch
        {
            GlunoConflictTypes.TimeOverlap => swedish
                ? "Det krockar med något som redan är inplanerat."
                : "That clashes with something already planned.",
            GlunoConflictTypes.InsufficientTravelTime => swedish
                ? "Det hinns inte med — resan emellan tar för lång tid."
                : "There isn't time to get between them.",
            GlunoConflictTypes.OutsideTripDates => swedish
                ? "Den dagen ligger utanför resan."
                : "That day is outside the Adventure.",
            GlunoConflictTypes.WrongDestinationDay => swedish
                ? "Ni är på en annan ort den dagen."
                : "You're somewhere else that day.",
            GlunoConflictTypes.LockedBooking => swedish
                ? "Det krockar med en bokning som inte går att flytta."
                : "That clashes with a booking that can't be moved.",
            GlunoConflictTypes.CheckInConflict => swedish
                ? "Det krockar med incheckningen."
                : "That clashes with check-in.",
            GlunoConflictTypes.CheckOutConflict => swedish
                ? "Det krockar med utcheckningen."
                : "That clashes with check-out.",
            GlunoConflictTypes.OutsideOpeningHours => swedish
                ? "Det är stängt då."
                : "It's closed then.",
            GlunoConflictTypes.DayCapacityExceeded => swedish
                ? "Dagen blir väldigt full."
                : "That makes the day very full.",
            GlunoConflictTypes.DuplicateActivity => swedish
                ? "Något liknande finns redan i planen."
                : "Something like that is already planned.",
            _ => swedish
                ? "Det går inte ihop med planen."
                : "That doesn't fit the plan.",
        };
    }
}

/// <summary>
/// One reason a draft proposal cannot become a plan as it stands.
///
/// Built from the quality gate's own blockers, never from the model. The model
/// may describe a conflict; it cannot declare one, cannot decide what may be
/// offered about it, and cannot mark it resolved.
/// </summary>
public sealed record GlunoProposalConflict
{
    public required string ConflictType { get; init; }

    /// 0 is most blocking. See <see cref="GlunoConflictTypes.Severity"/>.
    public int Severity => GlunoConflictTypes.Severity(ConflictType);

    /// Rows of the draft this is about, by index.
    public IReadOnlyList<int> AffectedDraftItemIndexes { get; init; } = Array.Empty<int>();

    /// Activities already in the plan that this clashes with.
    public IReadOnlyList<Guid> AffectedExistingActivityIds { get; init; } = Array.Empty<Guid>();

    /// ISO date the conflict is on, when it is about one day.
    public string? Date { get; init; }
    public string? StartTime { get; init; }
    public string? EndTime { get; init; }

    /// For a travel-time conflict: what the journey needs, and what there is.
    public int RequiredTravelMinutes { get; init; }
    public int AvailableMinutes { get; init; }

    /// <summary>
    /// False when nothing on offer can fix it — a locked booking that cannot
    /// move and a new item that cannot go anywhere else. Then the only honest
    /// option is backing out.
    /// </summary>
    public bool IsResolvable { get; init; } = true;

    /// <summary>
    /// Whether the existing Activity may be moved at all.
    ///
    /// False for a booking with a reference, a check-in and a check-out. This
    /// is what stops "move the existing one" being offered for something Gluno
    /// has no business moving.
    /// </summary>
    public bool ExistingIsMovable { get; init; }

    /// <summary>
    /// True when the opening hours are not actually known.
    ///
    /// Definitely shut and possibly shut are different conflicts: the second
    /// can honestly be planned around with a caveat, the first cannot.
    /// </summary>
    public bool HoursAreUncertain { get; init; }

    /// <summary>
    /// Bumped every time the draft is rebuilt.
    ///
    /// A tap carrying an old version is answering a question about a plan that
    /// no longer exists, and applying it would resolve the wrong conflict.
    /// </summary>
    public int ConflictVersion { get; init; }

    public IReadOnlyList<string> AllowedStrategies => GlunoConflictStrategies.For(this);
}
