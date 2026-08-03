namespace sidequest.backend.Services.Gluno;

/// <summary>
/// The provider-agnostic category vocabulary. Each provider maps its own
/// taxonomy onto this; nothing above the provider layer ever sees a
/// vendor-specific category name.
/// </summary>
public enum TravelPlaceCategory
{
    General,
    Restaurant,
    Attraction,
    Hotel,
}

public static class TravelPlaceCategories
{
    public static TravelPlaceCategory Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "restaurant" or "restaurants" or "food" or "eat" => TravelPlaceCategory.Restaurant,
        "attraction" or "attractions" or "sight" or "sights" or "museum" => TravelPlaceCategory.Attraction,
        "hotel" or "hotels" or "accommodation" or "stay" => TravelPlaceCategory.Hotel,
        _ => TravelPlaceCategory.General,
    };

    public static string ToWireValue(TravelPlaceCategory category) => category switch
    {
        TravelPlaceCategory.Restaurant => "restaurant",
        TravelPlaceCategory.Attraction => "attraction",
        TravelPlaceCategory.Hotel => "hotel",
        _ => "general",
    };
}

/// <summary>
/// A place from an external travel-data provider, normalised.
///
/// Three rules shape every field below.
///
///  • **Missing means null.** Not zero, not an empty string, not a placeholder
///    rating. A caller must be able to tell "the provider didn't return this"
///    apart from "the provider says it is zero", because Gluno is explicitly
///    forbidden from inventing ratings, prices, reviews or opening hours.
///
///  • **The provider stays attached.** <see cref="Provider"/> and
///    <see cref="SourceAttribution"/> travel with each individual result, not
///    with the batch — a future second provider must not be able to inherit
///    Tripadvisor's attribution by sitting in the same list.
///
///  • **Ids keep their namespace.** <see cref="ExternalId"/> is always
///    "provider:id", so an id can never be handed to the wrong provider, and a
///    future propose_activity can store it without ambiguity.
/// </summary>
public sealed class TravelPlace
{
    /// Stable provider id, e.g. "tripadvisor". Never a URL, never a key.
    public required string Provider { get; init; }

    /// Namespaced id: "tripadvisor:12345". This is what leaves the backend.
    public required string ExternalId { get; init; }

    /// The provider's own bare id. Internal — used to build follow-up calls;
    /// never sent to the model or the app on its own.
    public required string ProviderPlaceId { get; init; }

    public required string Name { get; init; }

    /// Normalised category ("restaurant" | "attraction" | "hotel" | "general").
    public required string Category { get; init; }
    /// The provider's own human label for the category, when it gave one.
    public string? CategoryLabel { get; init; }

    public string? Address { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// The provider's rating on ITS OWN scale — never rescaled or normalised
    /// to a percentage, because a rescaled rating stops matching what the
    /// user would see on the provider's own page.
    public double? Rating { get; init; }
    /// The top of that scale (5 for Tripadvisor). Explicit so a future
    /// provider using a 10-point scale cannot be silently misread.
    public double? RatingScaleMax { get; init; }
    public int? ReviewCount { get; init; }

    /// The provider's own price band, verbatim (e.g. "$$ - $$$"). Not parsed
    /// into a number: the bands are not comparable across providers.
    public string? PriceLevel { get; init; }

    /// A single representative image, already validated as https. Null when
    /// the provider returned none or photos are not enabled.
    public string? ImageUrl { get; init; }

    /// The provider's page for this place, already validated against the
    /// provider's own host allowlist.
    public string? ProviderUrl { get; init; }

    /// Attribution text that must be shown wherever this result appears.
    /// Carried with the data so a caller cannot forget it.
    public required string SourceAttribution { get; init; }

    /// <summary>
    /// Whether this result's CONTENT may be written into a stored payload —
    /// the name, the address, the rating, the hours, the snippet.
    ///
    /// CARRIED ON THE PLACE, not looked up by provider name, and the reason is
    /// concrete: Terra and the Content API are both "tripadvisor" and issue the
    /// same location ids, so a name comparison could not tell them apart even
    /// if it were an acceptable way to decide. The provider that produced the
    /// result is the only thing that knows its own terms, so it stamps them on.
    ///
    /// Defaults to false. A provider that says nothing about its rights gets
    /// the careful reading rather than the convenient one.
    /// </summary>
    public bool AllowsContentPersistence { get; init; }

    /// <summary>
    /// Whether this result's IDENTITY may be kept — the location id alone,
    /// with no presentation text attached to it.
    ///
    /// SEPARATE FROM CONTENT ON PURPOSE, because the terms are separate.
    /// Tripadvisor Terra forbids storing content and permits keeping the
    /// Location ID; those are two different permissions and collapsing them
    /// into one flag would force a choice between storing what is not allowed
    /// and discarding what is.
    ///
    /// An id on its own renders nothing. What it buys is the ability to ask the
    /// provider for the same place again — see <see cref="GlunoPlaceReference"/>.
    /// </summary>
    public bool AllowsIdentityPersistence { get; init; }

    /// Opening hours exactly as the provider stated them, one line per day.
    /// EMPTY unless the provider actually returned them — never inferred.
    public IReadOnlyList<string> OpeningHours { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The same hours, parsed into intervals the schedule engine can compare
    /// clock times against. Null when the provider gave no STRUCTURED hours —
    /// the display lines above are localised prose and are deliberately not
    /// parsed, because a misread timetable is worse than an unknown one.
    /// </summary>
    public OpeningHours? Hours { get; init; }

    /// A short, permitted review snippet or summary. Null unless the provider
    /// returned review text and snippets are enabled.
    public string? ReviewSummary { get; init; }

    /// Straight-line distance from the search origin, when one was given.
    /// Computed by SideQuest, not the provider.
    public double? DistanceKm { get; init; }
}

/// <summary>
/// A place plus SideQuest's own ranking of it.
///
/// <see cref="Signals"/> is the transparency requirement made concrete: the
/// short machine-readable reasons this result placed where it did, so Gluno
/// can say "highly rated with thousands of reviews, and a five-minute walk
/// from your hotel" instead of asserting an authority it doesn't have.
///
/// This ordering is SideQuest's, never the provider's — see
/// <see cref="TravelPlaceRanker"/>.
/// </summary>
public sealed class RankedTravelPlace
{
    public required TravelPlace Place { get; init; }
    public required double Score { get; init; }
    public required IReadOnlyList<string> Signals { get; init; }
}

public sealed class TravelPlaceQuery
{
    /// Free text: "seafood restaurant", "modern art museum".
    public string Query { get; init; } = string.Empty;
    /// Town or area, when the caller has no coordinates.
    public string? Near { get; init; }
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// Only meaningful alongside coordinates.
    public double? RadiusKm { get; init; }
    public TravelPlaceCategory Category { get; init; } = TravelPlaceCategory.General;
    public int Limit { get; init; } = 5;
    /// ISO language for provider-localised text.
    public string Language { get; init; } = "en";
    /// Budget hint from the user, used for ranking — never sent upstream as a
    /// filter, so a cheap-but-perfect match is still surfaced.
    public string? PriceLevel { get; init; }
    /// Free-form interests ("art", "kid friendly"), used for ranking only.
    public IReadOnlyList<string> Interests { get; init; } = Array.Empty<string>();

    /// <summary>
    /// Bare provider location ids the caller has already shown, so "more"
    /// can mean MORE. Sent upstream when the provider supports exclusion
    /// (Terra documents exclude_location_ids), and re-filtered locally either
    /// way — a provider that ignores the field must not undo the promise.
    /// </summary>
    public IReadOnlyList<string> ExcludedLocationIds { get; init; } = Array.Empty<string>();

    /// <summary>
    /// The Gluno request id this search runs for — LOG CORRELATION ONLY. It
    /// appears in the provider's own log lines so they can be joined to the
    /// request summary by eye. Never sent upstream, never persisted.
    /// </summary>
    public string? RequestId { get; init; }
}

/// <summary>
/// Why a search returned what it returned.
///
/// Only ever used to decide what to SAY and whether a retry is worth
/// suggesting. Never shown, never returned to the app as-is.
/// </summary>
public enum TravelSearchStatus
{
    /// The provider answered. An empty list here is a real answer.
    Ok,
    /// Over a rate limit or a quota. Transient — the same request may work in a
    /// minute, so this must not be reported as "the place is gone".
    RateLimited,
    /// Rejected, timed out, unreachable, or a contract change. Not the user's
    /// problem and not something a retry loop should hammer.
    Failed,
    /// The provider does not report why. Treated as "no information".
    Unknown,
}

public sealed class TravelSearchResult
{
    public required IReadOnlyList<TravelPlace> Places { get; init; }
    public required TravelSearchStatus Status { get; init; }
}

/// <summary>
/// One source of external travel data.
///
/// Everything vendor-specific lives behind this: endpoints, auth, taxonomy,
/// response shapes. A provider with no key reports <see cref="IsConfigured"/>
/// false and is skipped entirely, which is what lets Gluno run with no
/// provider at all and say so honestly rather than failing.
///
/// Provider credentials are server-side only and never appear in a response,
/// a log line, or anything the mobile app can reach.
/// </summary>
public interface ITravelDataProvider
{
    /// Stable id used as <see cref="TravelPlace.Provider"/>.
    string Provider { get; }

    /// False when the provider has no credentials or is switched off.
    bool IsConfigured { get; }

    /// <summary>
    /// Whether this provider's content may be cached or written into a
    /// conversation's stored payload.
    ///
    /// WHY THIS IS A PROVIDER DECISION AND NOT A GLOBAL ONE. Terms differ per
    /// provider and per contract. Tripadvisor's Terra caching policy says
    /// "caching, copying, downloading, storing or indexing content is not
    /// permitted for any content" and carves out only the Location ID — while
    /// the older Content API integration was built around a search and detail
    /// cache. One rule for both would either break the terms of one or throw
    /// away the performance of the other.
    ///
    /// FALSE MEANS: no cache entry, and nothing but the identity persisted
    /// into a message payload. The consequence is real and is not hidden — see
    /// the note in TerraTravelProvider.
    /// </summary>
    bool AllowsContentPersistence { get; }

    /// <summary>
    /// Whether the provider's own place id may be kept.
    ///
    /// Almost always true, and true for both Tripadvisor products — Terra names
    /// the Location ID as the one thing that may be stored. It is a separate
    /// question from content because the answers are separate, and because an
    /// id with no content is what makes a place re-fetchable without keeping
    /// anything the provider licensed only for the answer.
    /// </summary>
    bool AllowsLocationIdPersistence { get; }

    Task<IReadOnlyList<TravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct);

    /// <summary>
    /// The same search, plus the reason an empty result was empty.
    ///
    /// The plain overload throws that reason away, which is right for a chat
    /// turn: the answer degrades to prose and carries on. Re-fetching a place
    /// the user is trying to add needs it — "we are over the rate limit" and
    /// "that place is no longer in the results" are the same empty list and
    /// must not become the same sentence.
    ///
    /// Defaulted so a provider that cannot say why simply reports
    /// <see cref="TravelSearchStatus.Unknown"/>, which callers must treat as
    /// "no information", never as "fine".
    /// </summary>
    async Task<TravelSearchResult> SearchPlacesWithStatusAsync(TravelPlaceQuery query, CancellationToken ct)
        => new()
        {
            Places = await SearchPlacesAsync(query, ct),
            Status = TravelSearchStatus.Unknown,
        };

    /// <param name="providerPlaceId">
    /// The provider's BARE id (namespace already stripped by the registry).
    /// </param>
    Task<TravelPlace?> GetPlaceDetailsAsync(string providerPlaceId, string language, CancellationToken ct);
}

public interface ITravelDataRegistry
{
    /// False when nothing is configured. Surfaced to Gluno so it can say it
    /// has no source, rather than implying an empty result means "nothing
    /// exists".
    bool HasConfiguredProvider { get; }

    /// Ranked by SideQuest's own scoring — see <see cref="TravelPlaceRanker"/>.
    Task<IReadOnlyList<RankedTravelPlace>> SearchPlacesAsync(TravelPlaceQuery query, CancellationToken ct);

    /// <summary>
    /// Everything the providers returned, UNRANKED and UNTRIMMED, with the
    /// reason behind an empty result.
    ///
    /// WHY UNRANKED. The ranked overload sorts by SideQuest's score and then
    /// takes the requested number, which is exactly right for an answer and
    /// exactly wrong for finding one known id again: the place could come back
    /// from the provider and still be trimmed off the end before anyone looked
    /// for it. This is for lookups by id, where relevance is not the question.
    /// </summary>
    Task<TravelSearchResult> SearchAllAsync(TravelPlaceQuery query, CancellationToken ct);

    /// <param name="externalId">Namespaced id, e.g. "tripadvisor:12345".</param>
    Task<TravelPlace?> GetPlaceDetailsAsync(string externalId, string language, CancellationToken ct);
}

public static class TravelPlaceIds
{
    public static string Namespaced(string provider, string providerPlaceId)
        => $"{provider}:{providerPlaceId}";

    /// <summary>
    /// Splits "provider:id". Returns false for anything unnamespaced — an id
    /// without a provider is never guessed at, because guessing would mean
    /// handing one provider's id to another.
    /// </summary>
    public static bool TrySplit(string? externalId, out string provider, out string providerPlaceId)
    {
        provider = string.Empty;
        providerPlaceId = string.Empty;
        if (string.IsNullOrWhiteSpace(externalId)) return false;

        var separator = externalId.IndexOf(':');
        if (separator <= 0 || separator == externalId.Length - 1) return false;

        provider = externalId[..separator].Trim();
        providerPlaceId = externalId[(separator + 1)..].Trim();
        return provider.Length > 0 && providerPlaceId.Length > 0;
    }
}
