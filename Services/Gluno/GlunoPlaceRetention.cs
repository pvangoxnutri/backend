namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What survives a turn, out of the places it showed.
///
/// Exactly one of three outcomes, and never a blend:
///
///  • **The cards.** Every provider involved licences its content for storage.
///    Nothing changes; a reload renders what it always did.
///
///  • **References only.** Content may not be kept but the ids may. The answer
///    text stays, the cards do not come back, and "Add" still works because the
///    place can be fetched again from its id.
///
///  • **Nothing.** Neither is permitted, or the turn was mixed. A shortlist half
///    of which survives would render on reload as though the other half had
///    never been recommended, which is a worse lie than an empty history.
///
/// PURE, AND SEPARATE FROM THE TURN, so the rule can be read and tested without
/// a conversation, a provider or a database.
/// </summary>
public sealed class GlunoPlaceRetention
{
    /// Full cards to persist. Empty unless every place allows it.
    public required IReadOnlyList<GlunoPlaceCard> Places { get; init; }

    /// Identity-only handles. Empty whenever <see cref="Places"/> is not.
    public required IReadOnlyList<GlunoPlaceReference> References { get; init; }

    /// The request behind <see cref="References"/>. Null when there are none —
    /// a search context with nothing to look up is dead weight.
    public GlunoPlaceSearchContext? Search { get; init; }

    /// True when something the user was shown is not being stored in full.
    public bool Reduced { get; init; }

    private static readonly GlunoPlaceRetention Nothing = new()
    {
        Places = Array.Empty<GlunoPlaceCard>(),
        References = Array.Empty<GlunoPlaceReference>(),
        Reduced = true,
    };

    public static GlunoPlaceRetention Decide(
        IReadOnlyList<GlunoPlaceCard> shown, GlunoPlaceSearchContext? search)
    {
        if (shown.Count == 0)
        {
            return new GlunoPlaceRetention
            {
                Places = Array.Empty<GlunoPlaceCard>(),
                References = Array.Empty<GlunoPlaceReference>(),
                Reduced = false,
            };
        }

        // ── One rule for the whole list ───────────────────────────────────
        //
        // Whole-list rather than per-place. In practice a turn is single
        // provider, so a mixed list only happens if that ever stops being true
        // — and if it does, keeping the storable half as cards and the rest as
        // references would render on reload as two of six places, as though the
        // other four had never been recommended.
        //
        // So the strictest terms in the list govern all of it. Everything
        // becomes references, or nothing is kept.
        if (shown.All(place => place.AllowsContentPersistence))
        {
            return new GlunoPlaceRetention
            {
                Places = shown,
                References = Array.Empty<GlunoPlaceReference>(),
                Reduced = false,
            };
        }

        // Everything the user saw has to be re-fetchable, or none of it is:
        // a reference list covering four of six cards means two "Add" buttons
        // that cannot work, and no way for the user to tell which two.
        if (!shown.All(place => place.AllowsIdentityPersistence)) return Nothing;

        // Without the request that found them, an id is an id nobody can look
        // up — the endpoint that answers by id is allowlist-governed.
        if (search is not { IsUsable: true }) return Nothing;

        var references = new List<GlunoPlaceReference>(shown.Count);

        for (var index = 0; index < shown.Count; index++)
        {
            var place = shown[index];
            var locationId = place.ProviderPlaceId;

            // Fall back to the namespaced id, which every card carries. A
            // reference without an id is not a reference.
            if (string.IsNullOrWhiteSpace(locationId)
                && TravelPlaceIds.TrySplit(place.ExternalId, out _, out var parsed))
            {
                locationId = parsed;
            }

            if (string.IsNullOrWhiteSpace(locationId)) return Nothing;

            references.Add(new GlunoPlaceReference
            {
                // The same positional key the card was rendered with, so a tap
                // on the third card finds the third reference.
                OptionKey = GlunoPlaceOptions.KeyFor(index),
                ProviderId = place.Provider,
                LocationId = locationId,
            });
        }

        return new GlunoPlaceRetention
        {
            Places = Array.Empty<GlunoPlaceCard>(),
            References = references,
            Search = search,
            Reduced = true,
        };
    }
}
