using System.Globalization;

namespace sidequest.backend.Services.Gluno;

public sealed record GlunoDocumentIssue(
    /// "blocker" | "warning"
    string Severity,
    /// Stable machine code — "checkout_before_checkin", "ambiguous_date".
    string Code,
    /// The extracted item it belongs to, when it belongs to one.
    string? ItemId,
    /// One line, in the user's language, safe to show.
    string Message);

public sealed record GlunoPossibleDuplicate(
    string ItemId,
    /// "activity" | "extraction" | "confirmation_number"
    string Against,
    /// The Activity id, or the other extraction's id.
    string ExistingId,
    string Label);

/// <summary>
/// What an extracted item would become if the user accepts it.
/// </summary>
public sealed record GlunoSuggestedMapping(
    string ItemId,
    /// The proposal action name — "propose_activity".
    string Action,
    /// The Activity category the proposal would use.
    string Category,
    string Title);

public sealed class GlunoDocumentValidationResult
{
    public required bool Valid { get; init; }
    public required IReadOnlyList<GlunoDocumentIssue> Blockers { get; init; }
    public required IReadOnlyList<GlunoDocumentIssue> Warnings { get; init; }
    public required IReadOnlyList<GlunoPossibleDuplicate> PossibleDuplicates { get; init; }

    /// <summary>
    /// True when something needs a human decision before anything is proposed —
    /// an ambiguous date, a missing timezone that changes the plan, a probable
    /// duplicate.
    /// </summary>
    public required bool RequiresUserReview { get; init; }

    public required IReadOnlyList<GlunoSuggestedMapping> SuggestedMappings { get; init; }
}

/// <summary>
/// Deterministic checks on what was read out of a document.
///
/// WHY A SEPARATE PASS. The extraction says what the document appears to
/// contain. This says whether that makes SENSE — against the Adventure, against
/// what is already planned, and against itself. A check-out before a check-in
/// is not an extraction failure, it is a reading that cannot be true, and only
/// something holding both dates at once can notice.
///
/// IT NEVER REPAIRS A CRITICAL DATE. Not by inference, not by swapping day and
/// month until the range works, not by assuming the year. A wrong date that
/// looks deliberate is far worse than a flagged one the user fixes in five
/// seconds — they can see their own booking; we are reading a photograph of it.
/// </summary>
public sealed class GlunoDocumentValidator
{
    public GlunoDocumentValidationResult Validate(GlunoDocumentValidationInput input)
    {
        var swedish = string.Equals(input.Language, "sv", StringComparison.OrdinalIgnoreCase);
        var issues = new List<GlunoDocumentIssue>();
        var duplicates = new List<GlunoPossibleDuplicate>();
        var mappings = new List<GlunoSuggestedMapping>();

        var seenConfirmations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in input.Items)
        {
            var start = item.Start ?? item.CheckIn;
            var end = item.End ?? item.CheckOut;

            // ── Dates that cannot be true ─────────────────────────────────
            if (item.CheckIn?.NormalisedDate is { } checkIn
                && item.CheckOut?.NormalisedDate is { } checkOut
                && string.CompareOrdinal(checkOut, checkIn) < 0)
            {
                issues.Add(Blocker("checkout_before_checkin", item.Id, swedish
                    ? "Utcheckningen ligger före incheckningen."
                    : "Check-out is before check-in."));
            }

            if (item.Type == GlunoBookingTypes.CarRental
                && item.Start?.NormalisedDate is { } pickup
                && item.End?.NormalisedDate is { } dropoff
                && string.CompareOrdinal(dropoff, pickup) < 0)
            {
                issues.Add(Blocker("dropoff_before_pickup", item.Id, swedish
                    ? "Återlämningen ligger före upphämtningen."
                    : "Drop-off is before pick-up."));
            }

            if (start?.NormalisedDate is { } from && end?.NormalisedDate is { } to
                && string.CompareOrdinal(to, from) < 0)
            {
                // For a journey this is only an error when both ends share a
                // timezone — an overnight flight or a westward crossing arrives
                // "before" it left, and that is entirely normal.
                var sameZone = start.TimeZoneId != null && start.TimeZoneId == end.TimeZoneId;
                var journey = GlunoBookingTypes.IsJourney(item.Type);

                if (!journey || sameZone)
                {
                    issues.Add(Blocker("end_before_start", item.Id, swedish
                        ? "Slutdatumet ligger före startdatumet."
                        : "The end date is before the start date."));
                }
                else
                {
                    issues.Add(Warning("arrival_before_departure_unexplained", item.Id, swedish
                        ? "Ankomsten ligger före avgången — kontrollera tidszonerna."
                        : "Arrival is before departure — check the time zones."));
                }
            }

            // ── Ambiguity the user has to settle ──────────────────────────
            foreach (var (label, date) in new[]
            {
                ("start", item.Start), ("end", item.End),
                ("check-in", item.CheckIn), ("check-out", item.CheckOut),
            })
            {
                if (date == null) continue;

                if (date.IsAmbiguous)
                {
                    issues.Add(Warning("ambiguous_date", item.Id, swedish
                        ? $"Datumet \"{date.OriginalText}\" kan läsas på två sätt — välj vilket som stämmer."
                        : $"The date \"{date.OriginalText}\" can be read two ways — pick the right one."));
                }
                else if (date.NormalisedDate != null && date.Confidence < GlunoDocumentConfidence.NeedsReview)
                {
                    issues.Add(Warning("low_confidence_date", item.Id, swedish
                        ? $"Jag är inte säker på datumet \"{date.OriginalText}\"."
                        : $"I'm not confident about the date \"{date.OriginalText}\"."));
                }

                // A missing timezone only matters where it changes the plan.
                // A restaurant booking is local by definition; a flight is not.
                if (GlunoBookingTypes.IsJourney(item.Type)
                    && date.NormalisedTime != null
                    && date.TimeZoneId == null
                    && label is "start" or "end")
                {
                    issues.Add(Warning("missing_timezone", item.Id, swedish
                        ? "Tidszonen framgår inte — tiden visas som den stod i dokumentet."
                        : "The time zone isn't stated — the time is shown as the document had it."));
                }
            }

            // ── Critical dates that are simply absent ─────────────────────
            if (start?.NormalisedDate == null && !start.IsAmbiguousOrNull())
            {
                issues.Add(Blocker("missing_start_date", item.Id, swedish
                    ? "Inget startdatum kunde läsas."
                    : "No start date could be read."));
            }

            // ── Outside the Adventure ─────────────────────────────────────
            if (start?.NormalisedDate is { } within
                && DateOnly.TryParse(within, CultureInfo.InvariantCulture, out var parsed)
                && !TripDateRange.Contains(input.TripStart, input.TripEnd, parsed))
            {
                issues.Add(Warning("outside_trip_dates", item.Id, swedish
                    ? "Datumet ligger utanför Äventyrets datum."
                    : "That date falls outside the Adventure's dates."));
            }

            // ── Booking status that was never stated ──────────────────────
            if (item.BookingStatus != null
                && item.BookingStatus is not ("confirmed" or "pending" or "cancelled"))
            {
                issues.Add(Warning("unclear_booking_status", item.Id, swedish
                    ? "Bokningsstatusen är oklar."
                    : "The booking status isn't clear."));
            }

            // ── Duplicates ────────────────────────────────────────────────
            if (item.ConfirmationNumber is { } confirmation && confirmation.Trim().Length > 3)
            {
                var key = confirmation.Trim();

                if (seenConfirmations.TryGetValue(key, out var previousItem))
                {
                    duplicates.Add(new GlunoPossibleDuplicate(
                        item.Id, "confirmation_number", previousItem, item.Title));
                }
                else
                {
                    seenConfirmations[key] = item.Id;
                }

                if (input.KnownConfirmationNumbers.Contains(key))
                {
                    duplicates.Add(new GlunoPossibleDuplicate(
                        item.Id, "extraction", key, item.Title));
                }
            }

            foreach (var existing in input.ExistingActivities)
            {
                if (!LooksLikeSameBooking(item, existing)) continue;

                duplicates.Add(new GlunoPossibleDuplicate(
                    item.Id, "activity", existing.Id.ToString(), existing.Title));
            }

            // ── Clashes with what is already fixed ────────────────────────
            if (GlunoBookingTypes.IsJourney(item.Type)
                && start?.NormalisedDate is { } journeyDate
                && start.NormalisedTime is { } journeyTime)
            {
                var clash = input.ExistingActivities.FirstOrDefault(activity =>
                    activity.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) == journeyDate
                    && activity.Time == journeyTime);

                if (clash != null)
                {
                    issues.Add(Warning("clashes_with_activity", item.Id, swedish
                        ? $"Krockar med \"{clash.Title}\" som redan ligger i planen."
                        : $"Clashes with \"{clash.Title}\", already in the plan."));
                }
            }

            // ── A hotel that does not cover the nights ────────────────────
            if (item.Type == GlunoBookingTypes.Hotel
                && item.CheckIn?.NormalisedDate is { } stayFrom
                && item.CheckOut?.NormalisedDate is { } stayTo
                && DateOnly.TryParse(stayFrom, CultureInfo.InvariantCulture, out var fromDate)
                && DateOnly.TryParse(stayTo, CultureInfo.InvariantCulture, out var toDate)
                && input.TripEnd is { } tripEnd
                && (fromDate > input.TripStart || toDate < tripEnd))
            {
                issues.Add(Warning("stay_does_not_cover_trip", item.Id, swedish
                    ? "Vistelsen täcker inte hela resan."
                    : "The stay doesn't cover the whole trip."));
            }

            // ── Too uncertain to offer at all ─────────────────────────────
            if (item.Confidence < GlunoDocumentConfidence.TooLow)
            {
                issues.Add(Warning("very_low_confidence", item.Id, swedish
                    ? "Jag är för osäker på den här posten för att föreslå den."
                    : "I'm too unsure about this one to suggest it."));
                continue;
            }

            mappings.Add(new GlunoSuggestedMapping(
                item.Id,
                GlunoActions.ProposeActivity,
                GlunoBookingTypes.ToActivityCategory(item.Type),
                item.Title));
        }

        var blockers = issues.Where(issue => issue.Severity == "blocker").ToList();
        var warnings = issues.Where(issue => issue.Severity == "warning").ToList();

        return new GlunoDocumentValidationResult
        {
            Valid = blockers.Count == 0,
            Blockers = blockers,
            Warnings = warnings,
            PossibleDuplicates = duplicates,
            // Anything ambiguous, duplicated or blocked gets a human before it
            // becomes a proposal. Everything from a document does anyway — this
            // flags the ones that need MORE than a glance.
            RequiresUserReview = blockers.Count > 0
                || duplicates.Count > 0
                || warnings.Any(warning => warning.Code is "ambiguous_date" or "low_confidence_date"),
            SuggestedMappings = mappings,
        };
    }

    /// <summary>
    /// Whether an extracted item is probably the Activity the user already has.
    ///
    /// Same day plus a recognisable title. Deliberately loose: showing a
    /// possible duplicate the user dismisses costs a glance, while missing one
    /// costs them a duplicated flight in their itinerary.
    /// </summary>
    private static bool LooksLikeSameBooking(GlunoExtractedItem item, GlunoActivityContext existing)
    {
        var date = (item.Start ?? item.CheckIn)?.NormalisedDate;
        if (date == null) return false;
        if (existing.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != date) return false;

        var itemTitle = GlunoIntentRouter.Normalise(item.Title);
        var existingTitle = GlunoIntentRouter.Normalise(existing.Title);

        if (itemTitle.Length < 3 || existingTitle.Length < 3) return false;

        return existingTitle.Contains(itemTitle, StringComparison.Ordinal)
            || itemTitle.Contains(existingTitle, StringComparison.Ordinal)
            // Same day, same role — two hotels on one night is far more likely
            // to be a duplicate than a plan.
            || (item.Type == GlunoBookingTypes.Hotel
                && ActivityRoles.FromCategory(existing.Category, existing.EndDate) == "stay");
    }

    private static GlunoDocumentIssue Blocker(string code, string? itemId, string message)
        => new("blocker", code, itemId, message);

    private static GlunoDocumentIssue Warning(string code, string? itemId, string message)
        => new("warning", code, itemId, message);
}

internal static class GlunoExtractedDateExtensions
{
    /// A null date is "not stated", which is different from "stated and
    /// unreadable". Only the second is worth a blocker.
    public static bool IsAmbiguousOrNull(this GlunoExtractedDate? date)
        => date == null || date.IsAmbiguous;
}

public sealed class GlunoDocumentValidationInput
{
    public required IReadOnlyList<GlunoExtractedItem> Items { get; init; }
    public required DateOnly TripStart { get; init; }
    public DateOnly? TripEnd { get; init; }
    public IReadOnlyList<GlunoActivityContext> ExistingActivities { get; init; }
        = Array.Empty<GlunoActivityContext>();
    /// Confirmation numbers already seen in OTHER analyses of this Adventure.
    public IReadOnlySet<string> KnownConfirmationNumbers { get; init; }
        = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    public string Language { get; init; } = "en";
}
