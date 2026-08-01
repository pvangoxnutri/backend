namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What kind of booking an extracted item is.
///
/// Closed list. <see cref="Unknown"/> is a real answer — a document that says
/// something travel-shaped but not identifiably a flight should say so rather
/// than be forced into the nearest category, because the category is what
/// decides which Activity gets proposed.
/// </summary>
public static class GlunoBookingTypes
{
    public const string Flight = "flight";
    public const string Hotel = "hotel";
    public const string Train = "train";
    public const string Ferry = "ferry";
    public const string Bus = "bus";
    public const string CarRental = "car_rental";
    public const string RestaurantReservation = "restaurant_reservation";
    public const string ActivityBooking = "activity_booking";
    public const string OtherReservation = "other_reservation";
    public const string Unknown = "unknown";

    public static readonly IReadOnlyList<string> All =
    [
        Flight, Hotel, Train, Ferry, Bus, CarRental,
        RestaurantReservation, ActivityBooking, OtherReservation, Unknown,
    ];

    public static bool IsKnown(string? value) => value != null && All.Contains(value);

    /// Types that move a person from one place to another. They get an arrival
    /// as well as a departure, and a timezone difference between the two is
    /// normal rather than an error.
    public static bool IsJourney(string type)
        => type is Flight or Train or Ferry or Bus;

    /// SideQuest Activity category for a booking type.
    public static string ToActivityCategory(string type) => type switch
    {
        Hotel => "hotel",
        Flight or Train or Ferry or Bus => "transport",
        CarRental => "transport",
        RestaurantReservation => "food",
        ActivityBooking => "activity",
        _ => "other",
    };
}

/// <summary>
/// A date read out of a document, kept in BOTH forms.
///
/// WHY THE ORIGINAL TEXT SURVIVES. "05/08/2026" is the fifth of August in
/// Sweden and the eighth of May in the United States, and no amount of
/// confidence makes that guessable from the string alone. Keeping the raw text
/// means the ambiguity can be shown to the user and resolved by them, rather
/// than silently resolved by us and discovered at an airport.
/// </summary>
public sealed record GlunoExtractedDate
{
    /// Exactly as it appeared. Never shown as if it were normalised.
    public required string OriginalText { get; init; }

    /// ISO, when it could be read unambiguously.
    public string? NormalisedDate { get; init; }

    /// HH:mm local to the place, when stated.
    public string? NormalisedTime { get; init; }

    /// <summary>
    /// IANA id, ONLY when a real place identified it. Never inferred from a
    /// city name alone — "Springfield" is a dozen timezones.
    /// </summary>
    public string? TimeZoneId { get; init; }

    /// 0–1. Below <see cref="GlunoDocumentConfidence.NeedsReview"/> the user
    /// has to confirm it before anything is proposed.
    public double Confidence { get; init; }

    /// <summary>
    /// The readings that both fit, when the format is genuinely ambiguous.
    /// Non-empty means the user must choose — we do not pick.
    /// </summary>
    public IReadOnlyList<string> AlternativeReadings { get; init; } = Array.Empty<string>();

    public bool IsAmbiguous => AlternativeReadings.Count > 0;

    public bool IsUsable => NormalisedDate != null && !IsAmbiguous;
}

/// <summary>
/// One booking found in a document.
///
/// Every field is nullable and every absent field stays null. A hotel
/// confirmation that does not state a check-out time gets a null check-out
/// time — not midnight, not 11:00, not "typical". A default here becomes a
/// number in someone's itinerary that nobody wrote and nobody can trace.
/// </summary>
public sealed record GlunoExtractedItem
{
    /// Stable within the analysis, so the user can select individual items.
    public required string Id { get; init; }

    /// <see cref="GlunoBookingTypes"/>.
    public required string Type { get; init; }

    /// The airline, hotel chain, rental company. Sanitised text.
    public string? Provider { get; init; }

    /// A short human title for the proposed Activity.
    public required string Title { get; init; }

    /// <summary>
    /// The booking reference.
    ///
    /// NEVER goes into the Gluno conversation context, an Activity description,
    /// a push notification, a share page or a log line. It is here so the
    /// review screen can show a masked version and the user can confirm they
    /// are looking at the right booking.
    /// </summary>
    public string? ConfirmationNumber { get; init; }

    /// "confirmed", "pending", "cancelled" — only when the document SAYS so.
    /// Never inferred: a confirmation email that omits the word "confirmed" is
    /// not evidence of anything.
    public string? BookingStatus { get; init; }

    public GlunoExtractedDate? Start { get; init; }
    public GlunoExtractedDate? End { get; init; }

    public string? DepartureLocation { get; init; }
    public string? ArrivalLocation { get; init; }
    public string? Address { get; init; }

    public string? Terminal { get; init; }
    public string? Gate { get; init; }
    public string? Seat { get; init; }

    public int? TravellersCount { get; init; }

    public GlunoExtractedDate? CheckIn { get; init; }
    public GlunoExtractedDate? CheckOut { get; init; }

    public string? PickupLocation { get; init; }
    public string? DropoffLocation { get; init; }

    /// <summary>
    /// Coordinates, ONLY when a separate place lookup resolved them.
    ///
    /// A model producing latitude and longitude from a hotel name is producing
    /// plausible numbers, not a location — and a plan built on those puts
    /// someone in the wrong part of a city with total confidence.
    /// </summary>
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// How the coordinates were resolved. Null when there are none.
    public string? CoordinatesSource { get; init; }

    public string? Currency { get; init; }
    public decimal? TotalPrice { get; init; }

    /// 1-based page the item was found on, for the review screen.
    public int? SourcePage { get; init; }

    /// Per-field confidence, keyed by field name. Absent means unscored.
    public IReadOnlyDictionary<string, double> FieldConfidence { get; init; }
        = new Dictionary<string, double>(StringComparer.Ordinal);

    /// Machine codes: "ambiguous_date", "no_timezone", "low_confidence".
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();

    /// Overall confidence for the item.
    public double Confidence { get; init; }

    /// <summary>
    /// A masked reference for display: "•••• 4821".
    ///
    /// The review screen shows this rather than the full number, so a shoulder
    /// glance or a screenshot does not hand over a booking someone could change.
    /// </summary>
    public string? MaskedConfirmation()
    {
        if (string.IsNullOrWhiteSpace(ConfirmationNumber)) return null;

        var trimmed = ConfirmationNumber.Trim();
        return trimmed.Length <= 4 ? new string('•', trimmed.Length) : "•••• " + trimmed[^4..];
    }
}

/// <summary>Confidence thresholds, in one place so the UI and the validator agree.</summary>
public static class GlunoDocumentConfidence
{
    /// Below this, an item is not offered as a proposal at all.
    public const double TooLow = 0.35;

    /// Below this, the user must confirm the field before it is used.
    public const double NeedsReview = 0.7;

    public static string Bucket(double confidence) => confidence switch
    {
        >= 0.9 => "high",
        >= NeedsReview => "medium",
        >= TooLow => "low",
        _ => "very_low",
    };
}

/// <summary>
/// The complete result of analysing one document.
/// </summary>
public sealed record GlunoDocumentExtraction
{
    /// <summary>
    /// Bumped when the shape or the extraction rules change. Stored on the row
    /// so a result produced by an older build is recognisable — and so a
    /// proposal can record which version it was grounded in.
    /// </summary>
    public const int CurrentVersion = 1;

    public int Version { get; init; } = CurrentVersion;

    public IReadOnlyList<GlunoExtractedItem> Items { get; init; } = Array.Empty<GlunoExtractedItem>();

    /// Pages actually read. Bounded by configuration.
    public int PagesAnalysed { get; init; }

    /// <summary>
    /// True when the document contains a QR code.
    ///
    /// Recorded as a FACT ABOUT THE DOCUMENT and nothing more. It is never
    /// decoded, never followed, and never turned into a link — a QR code in a
    /// booking is exactly the shape of thing an attacker would use to send a
    /// backend somewhere.
    /// </summary>
    public bool ContainsQrCode { get; init; }

    /// <summary>
    /// Hosts of links seen in the document, for the user's information only.
    ///
    /// Nothing here is ever fetched. A URL in an untrusted document must never
    /// become a request target — see GlunoDocumentSafety.
    /// </summary>
    public IReadOnlyList<string> LinkHosts { get; init; } = Array.Empty<string>();

    /// True when the document text tried to issue instructions.
    public bool ContainsInjectionAttempt { get; init; }

    /// Machine codes about the document as a whole.
    public IReadOnlyList<string> Warnings { get; init; } = Array.Empty<string>();
}
