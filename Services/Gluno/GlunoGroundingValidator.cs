using System.Globalization;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// A statement in the answer that nothing in the ledger backs.
/// </summary>
public sealed record UnsupportedClaim(
    /// <see cref="GlunoClaimTypes"/>.
    string ClaimType,
    /// The exact substring that made the claim. Used to redact it — never
    /// logged.
    string Text,
    /// Short machine reason: "no_evidence", "wrong_source", "stale",
    /// "distance_stated_as_time".
    string Reason);

public sealed record GroundingContradiction(string ClaimType, string Text, string Detail);

public sealed record AttributionError(string Claimed, string Actual, string Detail);

public sealed class GlunoGroundingResult
{
    public required bool Passed { get; init; }
    public required IReadOnlyList<UnsupportedClaim> UnsupportedClaims { get; init; }
    public required IReadOnlyList<GroundingContradiction> Contradictions { get; init; }
    /// Claims resting on evidence that is past its freshness window.
    public required IReadOnlyList<GlunoEvidence> StaleClaims { get; init; }
    public required IReadOnlyList<AttributionError> AttributionErrors { get; init; }

    /// <summary>
    /// The answer with unsupported statements repaired, when repair was
    /// possible without inventing anything.
    ///
    /// Null when the answer is beyond safe repair — the core of what was said
    /// is unsupported, and editing around it would leave a sentence that reads
    /// fine and means something different.
    /// </summary>
    public string? SafeCorrections { get; init; }

    /// The answer's substance is unsupported. Worth one more model round.
    public required bool MustRegenerate { get; init; }

    /// A deterministic, honest answer for when regeneration is not available or
    /// failed twice.
    public string? FallbackResponse { get; init; }

    public static GlunoGroundingResult Clean => new()
    {
        Passed = true,
        UnsupportedClaims = Array.Empty<UnsupportedClaim>(),
        Contradictions = Array.Empty<GroundingContradiction>(),
        StaleClaims = Array.Empty<GlunoEvidence>(),
        AttributionErrors = Array.Empty<AttributionError>(),
        MustRegenerate = false,
    };
}

/// <summary>
/// Checks the model's answer against the evidence ledger, deterministically,
/// before anything is stored or shown.
///
/// WHY TEXT MATCHING AND NOT A MODEL. Using a second model round to grade the
/// first is slower, costs another call, and fails in a correlated way — the
/// same tendency that produced the invented rating will happily approve it. A
/// regex that asks "is there a number shaped like a rating in this sentence,
/// and does the ledger contain one?" is crude, but it is crude in a direction
/// that fails SAFE: the worst case is a redacted number that was actually fine,
/// which costs the user a slightly weaker sentence.
///
/// THE ASYMMETRY THAT SHAPES EVERYTHING HERE. A missing fact is a small
/// disappointment. An invented fact is a betrayal — the user has no way to
/// check it, acts on it, and finds out when it fails. So every judgement call
/// below is resolved toward removing the claim.
///
/// WHAT IT WILL NOT DO: invent a replacement. A correction may delete a number,
/// downgrade a travel time to the distance we genuinely measured, or say the
/// information is missing. It may never substitute a different number, because
/// then the validator itself becomes a source of unverified facts.
/// </summary>
public sealed class GlunoGroundingValidator
{
    // ── Claim detectors ──────────────────────────────────────────────────
    //
    // Each finds a SHAPE of statement. Matched against accent-folded text so
    // Swedish is not silently exempt from every rule.

    /// "4.5", "4,5 av 5", "rated 4.2", "betyg 4,6".
    private static readonly Regex RatingPattern = new(
        @"\b(?:rated|rating|betyg(?:et)?|betygsatt)\b[^.]{0,15}?\b\d[.,]\d\b" +
        @"|\b\d[.,]\d\s*(?:/|av|out of|of)\s*5\b" +
        @"|\b\d[.,]\d\s*(?:stars|stjarnor)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "1 200 recensioner", "over 3000 reviews".
    private static readonly Regex ReviewCountPattern = new(
        @"\b\d[\d\s.,]{0,8}\s*(?:reviews?|recensioner|omdomen|ratings)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "$$", "runt 400 kr", "€30 per person", "about 25 euros".
    private static readonly Regex PricePattern = new(
        @"(?<![\w$])\${1,4}(?![\w])" +
        @"|\b\d{1,5}\s*(?:kr|sek|eur|euro|euros|usd|dollar|dollars|pund|gbp)\b" +
        @"|[€£$]\s?\d{1,5}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "opens at 09:00", "oppet 10-18", "stanger 17:00".
    private static readonly Regex OpeningHoursPattern = new(
        @"\b(?:open(?:s|ing)?|closes?|closed|oppn(?:ar|et)|oppet|stang(?:er|t|d))\b[a-z0-9 ]{0,20}\b\d{1,2}(?:[:.]\d{2})?\b" +
        @"|\b\d{1,2}[:.]\d{2}\s*(?:-|–|till|to)\s*\d{1,2}[:.]\d{2}\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "open now", "oppet nu", "de har oppet just nu".
    private static readonly Regex OpenNowPattern = new(
        @"\b(?:open|closed)\s+(?:right\s+)?now\b|\b(?:oppet|stangt)\s+(?:just\s+)?nu\b" +
        @"|\bhar oppet nu\b|\bis currently open\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// A duration attached to a mode of travel.
    private static readonly Regex TravelTimePattern = new(
        @"\b(\d{1,3})\s*(?:-\s*\d{1,3}\s*)?(?:min(?:ut(?:er|es?)?)?s?|timm(?:e|ar)|hours?)\b[a-z0-9 ]{0,25}\b" +
        @"(?:walk|walking|drive|driving|bus|train|metro|transit|promenad|gang|bil|buss|tag|tunnelbana|cykel|bike)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// The reverse order: "en 12-minuters promenad", "a 20 minute drive".
    private static readonly Regex TravelTimeReversePattern = new(
        @"\b(?:walk|drive|ride|promenad|bilresa|bussresa|tagresa)\b[a-z0-9 ]{0,10}\b(\d{1,3})\s*(?:min|minut)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "det regnar på fredag", "sunny on Saturday", "22 degrees".
    private static readonly Regex WeatherPattern = new(
        @"\b(?:sunny|rain(?:y|ing)?|snow(?:y|ing)?|cloudy|storm|thunder|soligt|regn(?:ar|igt)?|snoar|molnigt|asks)\b" +
        @"|\b\d{1,2}\s*(?:degrees|grader|°c)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// "you can book", "det finns lediga bord", "bookable".
    private static readonly Regex BookabilityPattern = new(
        @"\b(?:you can book|bookable|available tables?|has availability|free tables?|slots? available)\b" +
        @"|\b(?:g[ao]r att boka|finns lediga|lediga bord|bokningsbar|det finns plats)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims about disruptions, closures and events.
    ///
    /// These are the most dangerous sentences Gluno can produce without
    /// evidence. "There's a rail strike that day" reshapes somebody's whole
    /// trip, and an invented one is indistinguishable from a real one until
    /// they are standing on a platform.
    /// </summary>
    private static readonly Regex DisruptionPattern = new(
        @"\b(?:strike|strikes|striking|cancelled|canceled|closed|closure|disrupt(?:ed|ion)?|" +
        @"suspended|not running|no service|blocked|evacuat)\w*\b" +
        @"|\b(?:strejk|installd|installt|stangt|stangd|instald|avstangd|stord|storning)\w*\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Claims that an event is happening.
    private static readonly Regex EventClaimPattern = new(
        @"\b(?:festival|concert|carnival|parade|marathon|exhibition|match|game)\b[a-z0-9 ]{0,25}\b" +
        @"(?:on|during|from|is|takes place|runs)\b" +
        @"|\b(?:festival|konsert|karneval|marknad|utstallning|match)\b[a-z0-9 ]{0,25}\b" +
        @"(?:den|under|fran|pagar|halls|ager rum)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// A public-holiday claim.
    private static readonly Regex HolidayPattern = new(
        @"\b(?:public|bank|national)\s+holiday\b|\b(?:helgdag|rod dag|nationaldag)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Promises about safety.
    ///
    /// Gluno is a trip planner, not a risk assessor, and nobody should act on
    /// its reassurance. It may report what an authority published; it may never
    /// tell somebody a place is safe.
    /// </summary>
    private static readonly Regex SafetyGuaranteePattern = new(
        @"\b(?:it(?:'s| is) (?:completely |perfectly |totally )?safe|no (?:danger|risk)|" +
        @"you(?:'ll| will) be fine|definitely safe|nothing to worry about)\b" +
        @"|\b(?:helt sakert|inga risker|det ar sakert att|ingen fara|inget att oroa)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Departure-status and ticket claims.
    ///
    /// SideQuest has no operator feed and no ticketing data. "The 08:40 is
    /// running" and "there are tickets left" are unsupported by construction,
    /// however plausible they sound.
    /// </summary>
    private static readonly Regex DepartureStatusPattern = new(
        @"\b(?:is|are|will be) (?:running|departing|operating|on time|sailing)\b" +
        @"|\btickets? (?:are |is )?(?:available|still available|left|on sale)\b" +
        @"|\b(?:gar som vanligt|avgar enligt|biljetter finns|det finns biljetter)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims that the group has agreed something.
    ///
    /// The most damaging thing Gluno can invent in a group context. "You've all
    /// agreed on Monaco" ends a discussion that was still happening, and the
    /// members who had not answered find out their view was assumed.
    /// </summary>
    private static readonly Regex ConsensusPattern = new(
        @"\b(?:everyone (?:agreed|agrees|wants|voted)|you(?:'ve| have) all (?:agreed|chosen|decided)|" +
        @"the group (?:has )?(?:agreed|decided|chosen)|unanimous(?:ly)?|all of you (?:agreed|want))\b" +
        @"|\b(?:alla (?:ar overens|tycker|vill|har rostat)|ni har alla (?:kommit overens|valt|bestamt)|" +
        @"gruppen har (?:bestamt|valt|kommit overens)|enhalligt|alla vill)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Language that assigns a group problem to a person.
    ///
    /// A planner that names whose constraint is blocking a plan turns a
    /// scheduling problem into an argument — and reveals something the person
    /// shared with the PLANNER, not with the group.
    /// </summary>
    private static readonly Regex BlamePattern = new(
        @"\b(?:because of (?:one|a) (?:member|person)|one of you is|" +
        @"someone(?:'s| is) (?:blocking|holding|preventing|stopping)|" +
        @"the majority (?:wants|has decided) so|is ruining|is the problem)\b" +
        @"|\bthe majority (?:wants?|has decided|decided|prefers?)\b" +
        @"|\b(?:pa grund av (?:en|nagon) (?:medlem|person)|nagon (?:blockerar|hindrar|stoppar)|" +
        @"majoriteten (?:vill|bestamde|tycker)|forstor planen)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims that this is the objectively right answer.
    ///
    /// The ranking is a heuristic with weights somebody chose. Calling it fair
    /// dresses a judgement as arithmetic and forecloses the discussion the group
    /// is entitled to have.
    /// </summary>
    private static readonly Regex FairnessClaimPattern = new(
        @"\b(?:the (?:only )?fair(?:est)? (?:solution|answer|option|way)|objectively fair|" +
        @"this is fair to everyone)\b" +
        @"|\b(?:den (?:enda )?rattvisa (?:losningen|losning)|objektivt rattvis|rattvist for alla)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims about what the user ALWAYS does, or what they are like.
    ///
    /// The overreach that turns a helpful assistant into an unsettling one.
    /// Somebody who moved three start times has a pattern on one trip; they do
    /// not "always prefer late starts", and they certainly do not have a
    /// personality Gluno has come to know. A confirmed preference supports "you
    /// asked me to keep walks short here" and nothing wider.
    /// </summary>
    private static readonly Regex OverGeneralisationPattern = new(
        @"\byou (?:always|never|usually|tend to|generally|typically) \w+" +
        @"|\b(?:i know|i've learned|i have learned|i've noticed you're|" +
        @"i understand your (?:personality|style|taste))\b" +
        @"|\byou(?:'re| are) (?:the kind of|a) (?:person|traveller|traveler) who\b" +
        @"|\byou (?:hate|love) \w+" +
        // Claiming a profile exists at all. There isn't one, by design — the
        // system stores confirmed preferences and open questions, and nothing
        // that could be described as a picture of somebody.
        @"|\b(?:your (?:travel |traveller |user )?profile|" +
        @"i(?:'ve| have) built (?:up )?a picture|based on (?:what i know about|your profile))\b" +
        @"|\b(?:du (?:brukar|alltid|aldrig)|jag vet att du|jag har lart kanna|" +
        @"du ar en sadan som|du (?:hatar|alskar)|din (?:rese)?profil)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Claims that a preference is settled when it is only a candidate.
    ///
    /// A candidate influences nothing until the user confirms it, and saying
    /// "you've told me you prefer X" when they have not is both false and the
    /// kind of false that is awkward to correct.
    /// </summary>
    private static readonly Regex StatedPreferencePattern = new(
        @"\byou(?:'ve| have) (?:told me|asked me|said)\b" +
        @"|\byour (?:stated |confirmed )?preference (?:is|for)\b" +
        @"|\b(?:du har (?:sagt|bett mig|talat om)|din (?:angivna |bekraftade )?preferens)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// Provider names Gluno might attribute to. Only Tripadvisor is real.
    private static readonly string[] KnownProviderNames =
    [
        "tripadvisor", "google", "yelp", "foursquare", "michelin", "booking.com",
        "trustpilot", "openstreetmap",
    ];

    /// A citation marker the model emitted: "[E3]".
    private static readonly Regex EvidenceMarkerPattern = new(
        @"\[E(\d{1,3})\]", RegexOptions.Compiled);

    public GlunoGroundingResult Validate(GlunoGroundingInput input)
    {
        var answer = input.AnswerText ?? string.Empty;
        if (answer.Trim().Length == 0) return GlunoGroundingResult.Clean;

        var now = input.NowUtc;
        var ledger = input.Ledger;
        var folded = Fold(answer);

        var unsupported = new List<UnsupportedClaim>();
        var contradictions = new List<GroundingContradiction>();
        var attributionErrors = new List<AttributionError>();
        var stale = new List<GlunoEvidence>();
        var corrected = answer;

        // ── Evidence markers ──────────────────────────────────────────────
        //
        // Checked first because an unknown id means the model invented a
        // citation, and a fabricated citation is worse than none: it makes an
        // unsupported claim look sourced.
        foreach (Match marker in EvidenceMarkerPattern.Matches(answer))
        {
            var id = "E" + marker.Groups[1].Value;
            if (ledger.Find(id) == null)
            {
                unsupported.Add(new UnsupportedClaim(
                    GlunoClaimTypes.Unknown, marker.Value, "unknown_evidence_id"));
            }
        }

        // Markers are internal plumbing. They come out of the user-facing text
        // whether they resolved or not.
        corrected = EvidenceMarkerPattern.Replace(corrected, string.Empty);
        corrected = Regex.Replace(corrected, @"[ \t]{2,}", " ");
        corrected = Regex.Replace(corrected, @"\s+([.,;:!?])", "$1");

        // ── Provider facts: rating, review count, price ───────────────────
        corrected = CheckAndRedact(
            corrected, folded, RatingPattern, GlunoClaimTypes.ProviderFact,
            "place_rating", ledger, now, unsupported, stale, input.Language);

        corrected = CheckAndRedact(
            corrected, folded, ReviewCountPattern, GlunoClaimTypes.ProviderFact,
            "place_review_count", ledger, now, unsupported, stale, input.Language);

        corrected = CheckAndRedact(
            corrected, folded, PricePattern, GlunoClaimTypes.ProviderFact,
            "place_price_level", ledger, now, unsupported, stale, input.Language);

        // ── Opening hours ─────────────────────────────────────────────────
        corrected = CheckAndRedact(
            corrected, folded, OpeningHoursPattern, GlunoClaimTypes.VerifiedOpeningHours,
            "opening_hours", ledger, now, unsupported, stale, input.Language);

        // "Open now" is its own rule: it needs CURRENT data, which the opening
        // hours window almost never provides. Stated hours for a day are not
        // evidence that a door is unlocked at this minute.
        if (OpenNowPattern.IsMatch(folded))
        {
            var match = OpenNowPattern.Match(corrected);
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.CurrentWeather, match.Success ? match.Value : "open now", "no_live_status"));

            if (match.Success)
            {
                corrected = corrected.Remove(match.Index, match.Length)
                    .Insert(match.Index, MissingPhrase(input.Language, "hours"));
            }
        }

        // ── Travel times ──────────────────────────────────────────────────
        //
        // The one case with a genuinely better correction than deletion: when
        // the ledger holds the straight-line distance, the sentence can be
        // demoted from a time to a distance. That is honest, still useful, and
        // uses only a number we actually measured.
        corrected = CheckTravelTimes(corrected, ledger, now, unsupported, stale, input.Language);

        // ── Weather ───────────────────────────────────────────────────────
        if (WeatherPattern.IsMatch(folded))
        {
            var forecasts = ledger.Supporting(GlunoClaimTypes.Forecast, now);

            if (forecasts.Count == 0)
            {
                unsupported.Add(new UnsupportedClaim(GlunoClaimTypes.Forecast, "weather", "no_evidence"));
            }
            else if (input.ReferencedDate != null)
            {
                // A forecast for the wrong day, or the wrong town, is not
                // evidence for this sentence. Both halves have to match.
                var matching = forecasts.Any(entry =>
                    entry.SourceReference != null
                    && entry.SourceReference.StartsWith(input.ReferencedDate, StringComparison.Ordinal));

                if (!matching)
                {
                    unsupported.Add(new UnsupportedClaim(
                        GlunoClaimTypes.Forecast, "weather", "wrong_date_or_location"));
                }
            }

            stale.AddRange(ledger.Stale(now).Where(entry => entry.Type == "day_forecast"));
        }

        // ── Live travel claims ────────────────────────────────────────────
        //
        // A disruption, closure or event needs a CURRENT live fact behind it.
        // The freshness filter does the real work: an expired or undated fact
        // enters the ledger already past its window, so it cannot support a
        // present-tense claim even though it is technically present.
        foreach (var (pattern, label) in new[]
        {
            (DisruptionPattern, "disruption"),
            (EventClaimPattern, "event"),
            (HolidayPattern, "public_holiday"),
        })
        {
            if (!pattern.IsMatch(folded)) continue;

            var live = ledger.Supporting(GlunoClaimTypes.LiveTravelFact, now);
            if (live.Count > 0) continue;

            var anyStale = ledger.Entries.Any(entry => entry.ClaimCategory == GlunoClaimTypes.LiveTravelFact);

            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.LiveTravelFact,
                label,
                // "stale" rather than "no_evidence" when we DID find something
                // and it turned out to describe another time. That distinction
                // decides whether a retry could help.
                anyStale ? "stale" : "no_evidence"));

            if (anyStale) stale.AddRange(ledger.Stale(now)
                .Where(entry => entry.ClaimCategory == GlunoClaimTypes.LiveTravelFact));
        }

        // ── Claims about the user themselves ──────────────────────────────
        //
        // Never supportable, whatever the ledger holds. A confirmed preference
        // backs "you asked me to keep walks short HERE" — it does not back
        // "you always prefer short walks", and nothing ever backs a claim about
        // somebody's personality.
        var generalisation = OverGeneralisationPattern.Match(corrected);
        if (generalisation.Success)
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.UserPreference, generalisation.Value, "over_generalised"));

            corrected = corrected.Remove(generalisation.Index, generalisation.Length)
                .Insert(generalisation.Index, MissingPhrase(input.Language, "generalisation"));
        }

        // Saying "you've told me" when they have not. A candidate is something
        // Gluno noticed, not something the user said.
        if (StatedPreferencePattern.IsMatch(folded)
            && !ledger.HasAny(GlunoClaimTypes.UserPreference, now))
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.UserPreference, "stated_preference", "candidate_presented_as_confirmed"));
        }

        // ── Group claims ──────────────────────────────────────────────────
        //
        // "You've all agreed on Monaco" ends a discussion that was still
        // happening, and the members who never answered discover their view was
        // assumed. Only a decision that actually reached ACCEPTED supports it —
        // and the freshness filter means a superseded one cannot.
        if (ConsensusPattern.IsMatch(folded))
        {
            var settled = ledger.Supporting(GlunoClaimTypes.ConfirmedGroupDecision, now);
            var polls = ledger.Supporting(GlunoClaimTypes.PollResult, now);

            if (settled.Count == 0 && polls.Count == 0)
            {
                unsupported.Add(new UnsupportedClaim(
                    GlunoClaimTypes.ConfirmedGroupDecision, "consensus", "no_group_decision"));
            }
        }

        // Naming whose constraint is blocking the plan reveals something shared
        // with the PLANNER, not with the group — and turns a scheduling problem
        // into an argument.
        var blame = BlamePattern.Match(corrected);
        if (blame.Success)
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.GroupConflict, blame.Value, "attributes_constraint_to_member"));

            corrected = corrected.Remove(blame.Index, blame.Length)
                .Insert(blame.Index, MissingPhrase(input.Language, "group_blame"));
        }

        // The ranking is a heuristic with weights somebody chose. Calling it
        // fair dresses a judgement as arithmetic and forecloses a discussion the
        // group is entitled to have.
        var fairness = FairnessClaimPattern.Match(corrected);
        if (fairness.Success)
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.PlanningCompromise, fairness.Value, "claims_objective_fairness"));

            corrected = corrected.Remove(fairness.Index, fairness.Length)
                .Insert(fairness.Index, MissingPhrase(input.Language, "compromise"));
        }

        // ── Safety guarantees ─────────────────────────────────────────────
        //
        // Never supportable, whatever the ledger holds. Reporting what an
        // authority published is legitimate; telling somebody a place is safe
        // is not something a trip planner gets to do.
        var safety = SafetyGuaranteePattern.Match(corrected);
        if (safety.Success)
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.Unknown, safety.Value, "safety_guarantee"));

            corrected = corrected.Remove(safety.Index, safety.Length)
                .Insert(safety.Index, MissingPhrase(input.Language, "safety"));
        }

        // ── Departure status and tickets ──────────────────────────────────
        var departure = DepartureStatusPattern.Match(corrected);
        if (departure.Success && !ledger.HasAny(GlunoClaimTypes.LiveTravelFact, now))
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.LiveTravelFact, departure.Value, "no_operator_data"));

            corrected = corrected.Remove(departure.Index, departure.Length)
                .Insert(departure.Index, MissingPhrase(input.Language, "departure"));
        }

        // ── Bookability ───────────────────────────────────────────────────
        //
        // SideQuest has no availability data from anyone. Any statement about
        // whether a table can be had is unsupported by construction.
        var bookability = BookabilityPattern.Match(corrected);
        if (bookability.Success)
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.Unknown, bookability.Value, "no_availability_data"));

            corrected = corrected.Remove(bookability.Index, bookability.Length)
                .Insert(bookability.Index, MissingPhrase(input.Language, "availability"));
        }

        // ── Entities that are not in the current plan ─────────────────────
        foreach (var mentioned in input.MentionedActivityIds)
        {
            if (input.KnownActivityIds.Contains(mentioned)) continue;

            contradictions.Add(new GroundingContradiction(
                GlunoClaimTypes.TripFact, mentioned.ToString(), "activity_not_in_current_plan"));
        }

        // ── Attribution ───────────────────────────────────────────────────
        var providersInLedger = ledger.Entries
            .Where(entry => entry.Provider != null)
            .Select(entry => entry.Provider!.ToLowerInvariant())
            .ToHashSet(StringComparer.Ordinal);

        foreach (var name in KnownProviderNames)
        {
            if (!folded.Contains(name, StringComparison.Ordinal)) continue;
            if (providersInLedger.Any(provider => provider.Contains(name, StringComparison.Ordinal))) continue;

            attributionErrors.Add(new AttributionError(
                name,
                providersInLedger.FirstOrDefault() ?? "none",
                "named a provider that supplied nothing this turn"));
        }

        // Gluno's own ranking presented as the provider's verdict. Different
        // claim, different authority — Tripadvisor rates places, it does not
        // know which one suits this trip.
        if (Regex.IsMatch(folded, @"\btripadvisor\b[a-z0-9 ]{0,20}\b(?:says?|recommends?|sager|rekommenderar|tycker)\b")
            || Regex.IsMatch(folded, @"\baccording to tripadvisor\b[a-z0-9 ]{0,20}\b(?:best|top|finest)\b"))
        {
            attributionErrors.Add(new AttributionError(
                "tripadvisor", "gluno_ranking",
                "presented SideQuest's own ranking as the provider's verdict"));
        }

        // ── Verdict ───────────────────────────────────────────────────────
        //
        // Regenerating is worth a model round when the answer's SUBSTANCE was
        // unsupported. A single stray price in an otherwise sound answer is
        // fixed by deletion; three unsupported claims, or a contradiction about
        // the user's own plan, means the answer was built on sand.
        var mustRegenerate = contradictions.Count > 0
            || unsupported.Count >= 3
            || unsupported.Any(claim => claim.Reason is "wrong_date_or_location" or "unknown_evidence_id");

        var passed = unsupported.Count == 0
            && contradictions.Count == 0
            && attributionErrors.Count == 0;

        return new GlunoGroundingResult
        {
            Passed = passed,
            UnsupportedClaims = unsupported,
            Contradictions = contradictions,
            StaleClaims = stale.DistinctBy(entry => entry.Id).ToList(),
            AttributionErrors = attributionErrors,
            SafeCorrections = passed ? null : Tidy(corrected),
            MustRegenerate = mustRegenerate,
            FallbackResponse = mustRegenerate
                ? GlunoFallbacks.Text(GlunoFallbackReason.GroundingFailed, input.Language)
                : null,
        };
    }

    /// <summary>
    /// Finds a claim shape, checks the ledger for a matching entry, and redacts
    /// when there is none.
    /// </summary>
    private static string CheckAndRedact(
        string text,
        string folded,
        Regex pattern,
        string claimType,
        string evidenceType,
        GlunoEvidenceLedger ledger,
        DateTime now,
        List<UnsupportedClaim> unsupported,
        List<GlunoEvidence> stale,
        string language)
    {
        if (!pattern.IsMatch(folded)) return text;

        var supporting = ledger.Entries
            .Where(entry => entry.Type == evidenceType)
            .ToList();

        var fresh = supporting.Where(entry => entry.IsFresh(now)).ToList();

        if (fresh.Count > 0)
        {
            // Supported. Any same-type entries past their window are still
            // worth surfacing so the answer can be labelled.
            stale.AddRange(supporting.Where(entry => !entry.IsFresh(now)));
            return text;
        }

        var reason = supporting.Count > 0 ? "stale" : "no_evidence";
        stale.AddRange(supporting);

        // Redact every occurrence, back to front so earlier indices stay valid.
        var matches = pattern.Matches(text).Cast<Match>().OrderByDescending(match => match.Index).ToList();

        foreach (var match in matches)
        {
            unsupported.Add(new UnsupportedClaim(claimType, match.Value, reason));
            text = text.Remove(match.Index, match.Length)
                .Insert(match.Index, MissingPhrase(language, evidenceType));
        }

        return text;
    }

    /// <summary>
    /// Travel times, which have a better correction than deletion.
    ///
    /// A stated time with no verified leg behind it becomes the straight-line
    /// distance we DID measure — "about 2.4 km away" instead of "a 20-minute
    /// walk". Same sentence, same usefulness, and now true.
    ///
    /// With no distance either, it is deleted like anything else.
    /// </summary>
    private static string CheckTravelTimes(
        string text,
        GlunoEvidenceLedger ledger,
        DateTime now,
        List<UnsupportedClaim> unsupported,
        List<GlunoEvidence> stale,
        string language)
    {
        var verified = ledger.Supporting(GlunoClaimTypes.VerifiedRouteTime, now);
        if (verified.Count > 0)
        {
            stale.AddRange(ledger.Stale(now).Where(entry => entry.Type == "route_leg"));
            return text;
        }

        var distances = ledger.Supporting(GlunoClaimTypes.StraightLineDistance, now)
            .Where(entry => entry.Value != null)
            .ToList();

        var matches = new List<Match>();
        matches.AddRange(TravelTimePattern.Matches(Fold(text)).Cast<Match>());
        matches.AddRange(TravelTimeReversePattern.Matches(Fold(text)).Cast<Match>());

        if (matches.Count == 0) return text;

        var replacement = distances.Count > 0
            ? DistancePhrase(language, distances[0].Value!)
            : MissingPhrase(language, "route");

        foreach (var match in matches.OrderByDescending(match => match.Index))
        {
            unsupported.Add(new UnsupportedClaim(
                GlunoClaimTypes.VerifiedRouteTime, match.Value,
                distances.Count > 0 ? "distance_stated_as_time" : "no_evidence"));

            // The folded text is the same LENGTH as the original — folding only
            // strips combining marks, never characters — so indices carry over.
            if (match.Index + match.Length <= text.Length)
            {
                text = text.Remove(match.Index, match.Length).Insert(match.Index, replacement);
            }
        }

        return text;
    }

    /// <summary>
    /// What replaces a redacted claim.
    ///
    /// Always an admission, never a substitute value. The moment this returns a
    /// number, the validator becomes a source of unverified facts and the whole
    /// arrangement is pointless.
    /// </summary>
    private static string MissingPhrase(string language, string what)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return what switch
        {
            "place_rating" => swedish ? "(betyg ej verifierat)" : "(rating not verified)",
            "place_review_count" => swedish ? "(antal omdömen ej verifierat)" : "(review count not verified)",
            "place_price_level" => swedish ? "(prisnivå ej verifierad)" : "(price not verified)",
            "opening_hours" or "hours" => swedish ? "(öppettider ej verifierade)" : "(opening hours not verified)",
            "route" => swedish ? "(restid ej verifierad)" : "(travel time not verified)",
            "availability" => swedish ? "(tillgänglighet okänd)" : "(availability unknown)",
            // Never a reassurance in return. The redaction says what is not
            // known, and the user is pointed at the authority.
            "safety" => swedish
                ? "(kontrollera UD:s reseinformation)"
                : "(check your government's travel advice)",
            "departure" => swedish
                ? "(avgångsstatus ej verifierad)"
                : "(departure status not verified)",
            // Rewritten as a property of the PLAN rather than of a person.
            "group_blame" => swedish
                ? "önskemålen går inte helt ihop"
                : "the wishes don't fully fit together",
            "compromise" => swedish
                ? "en kompromiss"
                : "a compromise",
            // Narrowed to THIS trip rather than a claim about the person.
            "generalisation" => swedish
                ? "för den här resan verkar du"
                : "for this trip you seem to",
            _ => swedish ? "(ej verifierat)" : "(not verified)",
        };
    }

    private static string DistancePhrase(string language, string kilometres)
        => string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase)
            ? $"ungefär {kilometres} km bort (fågelvägen)"
            : $"about {kilometres} km away (straight line)";

    /// Collapses the whitespace and stray punctuation a redaction leaves behind.
    private static string Tidy(string text)
    {
        var tidied = Regex.Replace(text, @"[ \t]{2,}", " ");
        tidied = Regex.Replace(tidied, @"\(\s+", "(");
        tidied = Regex.Replace(tidied, @"\s+\)", ")");
        tidied = Regex.Replace(tidied, @"\n{3,}", "\n\n");
        return tidied.Trim();
    }

    /// <summary>
    /// Accent-folded, same length.
    ///
    /// Length preservation matters: matches found in the folded text are used
    /// as indices into the ORIGINAL, so anything that removed or added
    /// characters would silently corrupt the answer during redaction.
    /// </summary>
    private static string Fold(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);

        foreach (var character in value)
        {
            builder.Append(character switch
            {
                'å' or 'ä' or 'à' or 'á' or 'â' => 'a',
                'Å' or 'Ä' or 'À' or 'Á' => 'A',
                'ö' or 'ø' or 'ó' or 'ô' => 'o',
                'Ö' or 'Ø' or 'Ó' => 'O',
                'é' or 'è' or 'ê' or 'ë' => 'e',
                'É' or 'È' or 'Ê' => 'E',
                'ü' or 'ú' or 'û' => 'u',
                'í' or 'ì' or 'î' => 'i',
                _ => character,
            });
        }

        return builder.ToString().ToLowerInvariant();
    }
}

public sealed class GlunoGroundingInput
{
    public string? AnswerText { get; init; }
    public required GlunoEvidenceLedger Ledger { get; init; }
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    public string Language { get; init; } = "en";

    /// The date the turn is about, for checking a forecast belongs to it.
    public string? ReferencedDate { get; init; }

    /// Activity ids the answer or its proposals referred to.
    public IReadOnlyList<Guid> MentionedActivityIds { get; init; } = Array.Empty<Guid>();
    /// Activity ids that actually exist right now.
    public IReadOnlySet<Guid> KnownActivityIds { get; init; } = new HashSet<Guid>();
}
