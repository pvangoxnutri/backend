using System.Globalization;
using System.Text.Json;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Turns extracted bookings into ordinary Gluno proposals.
///
/// WHY IT REUSES THE EXISTING PROPOSAL PATH RATHER THAN WRITING DIRECTLY. A
/// document is the least trustworthy input in the product — a machine's reading
/// of a photograph of somebody's booking. It gets the SAME treatment as a
/// suggestion Gluno invented: a preview card, an editable review screen, a
/// staleness check, and an explicit tap. Nothing about "it came from a
/// confirmation email" makes it safe to apply automatically; if anything the
/// opposite, because a wrong date read off a ticket looks more authoritative
/// than one Gluno guessed.
///
/// WHAT DELIBERATELY DOES NOT GET PROPOSED. A flight document supports ONE
/// certain thing: the flight. Generating "travel to the airport" and "arrival"
/// Activities alongside it invents two commitments the document never made — a
/// departure time for a taxi nobody booked. Those are offered only when the
/// document itself states them.
/// </summary>
public static class GlunoDocumentProposalMapper
{
    /// <summary>
    /// Builds proposal payloads for the items the user selected.
    /// </summary>
    /// <param name="selectedIds">
    /// Explicitly chosen by the user. An empty selection produces nothing —
    /// there is no "all" shortcut, because accepting everything should be a
    /// deliberate act rather than a default.
    /// </param>
    public static IReadOnlyList<GlunoProposal> Build(
        GlunoDocumentExtraction extraction,
        IReadOnlySet<string> selectedIds,
        Guid tripId,
        Guid documentId,
        Guid analysisId,
        string language)
    {
        var proposals = new List<GlunoProposal>();

        foreach (var item in extraction.Items)
        {
            if (!selectedIds.Contains(item.Id)) continue;

            // Too uncertain to offer, whatever the user selected. The review
            // screen already hides these; this is the server-side half of the
            // same rule, because the screen is not the boundary.
            if (item.Confidence < GlunoDocumentConfidence.TooLow) continue;

            var start = item.Start ?? item.CheckIn;
            if (start?.NormalisedDate is not { } date) continue;

            // An ambiguous date must be resolved by the user before it can
            // become an Activity. Picking one reading here would put someone
            // on the wrong day with total confidence.
            if (start.IsAmbiguous) continue;

            var payload = BuildPayload(item, date, extraction.Version, documentId, analysisId);

            proposals.Add(new GlunoProposal
            {
                ActionName = GlunoActions.ProposeActivity,
                Kind = "activity",
                TripId = tripId,
                Summary = Summary(item, date, language),
                Payload = payload,
            });
        }

        return proposals;
    }

    private static JsonElement BuildPayload(
        GlunoExtractedItem item,
        string date,
        int extractionVersion,
        Guid documentId,
        Guid analysisId)
    {
        var end = item.End ?? item.CheckOut;

        return JsonSerializer.SerializeToElement(new
        {
            date,
            title = item.Title,
            // The document's own detail, and nothing invented. Note what is
            // absent: the confirmation number. SideQuest has a dedicated
            // BookingReference field on documents; an Activity description is
            // rendered in the feed, the slideshow and share pages, and a
            // booking reference does not belong in any of them.
            description = BuildDescription(item),
            time = item.Start?.NormalisedTime,
            endDate = end?.NormalisedDate != date ? end?.NormalisedDate : null,
            endTime = end?.NormalisedTime,
            category = GlunoBookingTypes.ToActivityCategory(item.Type),
            locationLabel = item.Address ?? item.DepartureLocation ?? item.PickupLocation,

            // ── Grounding ────────────────────────────────────────────────
            //
            // Which document, which reading of it, which item. Enough for apply
            // to re-check that the document has not been replaced since — and
            // enough for a person to trace a saved Activity back to the ticket
            // it came from.
            grounding = new
            {
                source = "document",
                analysisId,
                documentId,
                extractionVersion,
                itemId = item.Id,
                confidence = GlunoDocumentConfidence.Bucket(item.Confidence),
            },
        });
    }

    /// <summary>
    /// The Activity description.
    ///
    /// Only fields a traveller would want to see at a glance, and only when the
    /// document stated them. Never the confirmation number, never a price,
    /// never a passenger name — all three end up on a shared trip feed.
    /// </summary>
    private static string? BuildDescription(GlunoExtractedItem item)
    {
        var parts = new List<string>();

        if (item.Provider != null) parts.Add(item.Provider);

        if (item.DepartureLocation != null && item.ArrivalLocation != null)
        {
            parts.Add($"{item.DepartureLocation} → {item.ArrivalLocation}");
        }

        if (item.Terminal != null) parts.Add($"Terminal {item.Terminal}");
        if (item.Gate != null) parts.Add($"Gate {item.Gate}");
        if (item.Seat != null) parts.Add($"Seat {item.Seat}");

        return parts.Count > 0 ? string.Join(" · ", parts) : null;
    }

    private static string Summary(GlunoExtractedItem item, string date, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);
        var readable = DateOnly.TryParse(date, CultureInfo.InvariantCulture, out var parsed)
            ? parsed.ToString("d MMM", swedish
                ? CultureInfo.GetCultureInfo("sv-SE")
                : CultureInfo.GetCultureInfo("en-GB"))
            : date;

        return swedish
            ? $"Lägg till \"{item.Title}\" den {readable}"
            : $"Add \"{item.Title}\" on {readable}";
    }
}
