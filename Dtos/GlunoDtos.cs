using System.Text.Json;

namespace sidequest.backend.Dtos;

/// <summary>
/// Whether Gluno can be used at all right now, and if not, why. The mobile app
/// asks once and hides the entry point rather than letting a user open a chat
/// that cannot answer.
/// </summary>
public class GlunoStatusDto
{
    /// <summary>
    /// Whether Gluno can answer AT ALL. The core only: switched on, and able
    /// to reach a model.
    ///
    /// Deliberately NOT a summary of everything below it. Tripadvisor,
    /// routing, live information and document analysis are all optional
    /// extras — Gluno plans perfectly well without any of them and simply
    /// says its travel times are estimates. Folding a capability into this
    /// boolean would take the whole assistant away because one paid API is
    /// switched off.
    /// </summary>
    public bool Available { get; set; }
    /// Whether this environment has Gluno switched on at all.
    public bool Enabled { get; set; }
    /// <summary>
    /// Whether the AI provider can be reached: a key and a primary model.
    ///
    /// A boolean — which provider, which model, and on what credentials never
    /// crosses this boundary.
    /// </summary>
    public bool AiConfigured { get; set; }
    /// "disabled" (not on for this environment) | "not_configured" (no AI
    /// provider key) | null when available.
    public string? Reason { get; set; }
    /// Which backend system-prompt version this environment is running, so a
    /// behaviour change is identifiable from the client side too.
    public int SystemPromptVersion { get; set; }
    /// True when SideQuest has an external travel-data provider wired up.
    /// False today — Gluno then answers from its own knowledge and says so.
    public bool TravelDataAvailable { get; set; }

    /// <summary>
    /// True when a routing provider can verify travel times.
    ///
    /// A boolean, deliberately: which provider, on which base URL, with which
    /// key, is server-side detail that never crosses this boundary. False means
    /// the app shows day plans with an "estimated travel times" note instead of
    /// presenting them as measured.
    /// </summary>
    public bool VerifiedTravelTimes { get; set; }

    /// <summary>
    /// True when Gluno can look up current travel information — strikes,
    /// closures, events, holidays.
    ///
    /// A boolean, like the others. Which provider, on what credentials, is
    /// server-side detail that never crosses this boundary.
    /// </summary>
    public bool LiveTravelInfoAvailable { get; set; }
}

public class GlunoConversationDto
{
    public Guid Id { get; set; }
    /// Null for a global conversation.
    public Guid? TripId { get; set; }
    /// The Adventure's current name, so the chat header can show it without
    /// the client having to carry it through navigation (and without it going
    /// stale when the trip is renamed). Null for a global conversation.
    public string? TripTitle { get; set; }
    public string? Title { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}

/// <summary>
/// A validated change Gluno suggested. Purely a preview — accepting it happens
/// through the ordinary trip endpoints, from the app, on the user's tap.
/// </summary>
public class GlunoProposalDto
{
    /// <summary>
    /// The proposal's server-side id. This is what the app applies, edits or
    /// rejects against, and it is the idempotency key — the same id can only
    /// ever be applied once.
    ///
    /// Guid.Empty for a legacy proposal stored before proposals became rows;
    /// those render as history and cannot be applied.
    /// </summary>
    public Guid Id { get; set; }
    /// "activity" | "day_plan" | "day_location" | "activity_move" | "trip_dates"
    public string Kind { get; set; } = string.Empty;
    /// The allow-listed action name, e.g. "propose_activity".
    public string ActionType { get; set; } = string.Empty;
    public Guid TripId { get; set; }
    public string Summary { get; set; } = string.Empty;
    /// Shape depends on Kind — see GlunoProposal in the backend for the map.
    public JsonElement Payload { get; set; }
    /// Schema version of Payload, so an older shape is recognisable.
    public int PayloadVersion { get; set; }
    /// pending | applying | applied | rejected | failed | stale
    public string Status { get; set; } = "stale";
    /// Machine-readable reason for a failed or stale proposal.
    public string? FailureCode { get; set; }
    public DateTime? AppliedAt { get; set; }
}

/// <summary>
/// What an apply changed. Lets the app refresh exactly the affected Adventure
/// instead of dropping every cache it holds.
/// </summary>
public class GlunoApplyChangesDto
{
    public Guid? TripId { get; set; }
    public List<Guid> CreatedActivityIds { get; set; } = new();
    public List<Guid> UpdatedActivityIds { get; set; } = new();
    public List<Guid> CreatedDayLocationIds { get; set; } = new();
    public List<Guid> UpdatedDayLocationIds { get; set; } = new();
    public bool TripDatesChanged { get; set; }
    /// ISO dates whose plan changed — enough to refresh the feed and weather
    /// for just those days.
    public List<string> AffectedDates { get; set; } = new();
}

public class GlunoApplyResponseDto
{
    public GlunoProposalDto Proposal { get; set; } = new();
    public GlunoApplyChangesDto Changes { get; set; } = new();
    /// A short, neutral sentence for the user. Never a database or model
    /// message. Null on success.
    public string? Message { get; set; }
}

/// <summary>
/// The user's edited version of a pending proposal. The payload replaces the
/// stored one wholesale and is re-validated at apply — this endpoint only
/// records what the user reviewed.
/// </summary>
public class UpdateGlunoProposalDto
{
    public JsonElement Payload { get; set; }
}

/// <summary>
/// One external place, as the chat renders it.
///
/// Never the provider's raw payload: the app is not expected to understand
/// any provider's response shape, and a null field means the provider did not
/// return it — never a default the app should display as fact.
/// </summary>
public class GlunoPlaceDto
{
    /// Provider id, e.g. "tripadvisor". Stays on each individual result so two
    /// providers' data can never be presented under one attribution.
    public string Provider { get; set; } = string.Empty;
    /// Namespaced id, e.g. "tripadvisor:12345".
    public string ExternalId { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    /// "restaurant" | "attraction" | "hotel" | "general"
    public string Category { get; set; } = string.Empty;
    public string? CategoryLabel { get; set; }
    public string? Address { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    /// On the provider's own scale — see <see cref="RatingScaleMax"/>.
    public double? Rating { get; set; }
    public double? RatingScaleMax { get; set; }
    public int? ReviewCount { get; set; }
    public string? PriceLevel { get; set; }
    public string? ImageUrl { get; set; }
    public string? ProviderUrl { get; set; }
    /// Must be shown wherever this result appears.
    public string SourceAttribution { get; set; } = string.Empty;
    public double? DistanceKm { get; set; }
    public List<string> OpeningHours { get; set; } = new();
    public string? ReviewSummary { get; set; }
    /// SideQuest's own ranking signals — not the provider's ordering.
    public List<string> Signals { get; set; } = new();
}

/// <summary>
/// A screen the chat may offer to open.
///
/// A stable target id plus verified entity ids — never a route or a URL. The
/// app owns the mapping to an actual screen, so a target it does not
/// recognise is ignored rather than followed. Opening a screen changes
/// nothing, and the card must never be presented as if it had.
/// </summary>
public class GlunoNavigationDto
{
    public string TargetId { get; set; } = string.Empty;
    /// What the app calls this screen, in the user's language.
    public string Label { get; set; } = string.Empty;
    /// One short line on why it helps. Never claims anything was saved.
    public string? Reason { get; set; }
    public Guid? TripId { get; set; }
    public Guid? ActivityId { get; set; }
    /// ISO date, for a feed day or to prefill a create form.
    public string? Date { get; set; }
}

/// <summary>
/// One entry in the chat's compact "Sources" row.
///
/// Deliberately small and deliberately human. It says where a fact came from,
/// what it supports and when it was checked — nothing about evidence ids,
/// database structure, tool names or prompts, none of which would mean anything
/// to a traveller and all of which are internal.
/// </summary>
public class GlunoSourceDto
{
    /// "route" | "weather" | "plan" | "provider" | "hours". Drives the icon.
    public string Kind { get; set; } = string.Empty;
    /// Already localised by the backend.
    public string Label { get; set; } = string.Empty;
    /// What this source backs, in a few words.
    public string Supports { get; set; } = string.Empty;
    public DateTime? VerifiedAt { get; set; }
    /// Past its freshness window. The app labels it rather than hiding it.
    public bool IsStale { get; set; }
    /// Provider brand, when attribution is owed.
    public string? Provider { get; set; }
}

public class GlunoMessageDto
{
    public Guid Id { get; set; }
    /// "user" | "assistant" | "system" | "tool"
    public string Role { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
    /// False for system and tool turns. The app renders a chat bubble only
    /// when this is true; the other rows exist for replay and audit.
    public bool IsRenderable { get; set; }
    /// Proposals produced by this turn, if any. Only ever set on an assistant
    /// turn.
    public List<GlunoProposalDto> Proposals { get; set; } = new();
    /// External places found for this turn, rendered as cards. Only ever set
    /// on an assistant turn.
    public List<GlunoPlaceDto> Places { get; set; } = new();
    /// Screens the user can choose to open. Nothing navigates without a tap.
    public List<GlunoNavigationDto> Navigations { get; set; } = new();
    /// The compact "Sources" row: where the non-place facts in this turn came
    /// from. Place attribution rides on the place cards themselves.
    public List<GlunoSourceDto> Sources { get; set; } = new();
    public DateTime CreatedAt { get; set; }
}

public class GlunoConversationDetailDto
{
    public GlunoConversationDto Conversation { get; set; } = new();
    /// The newest page, oldest message first.
    public List<GlunoMessageDto> Messages { get; set; } = new();
    /// True when older messages exist beyond this page.
    public bool HasMore { get; set; }
}

/// <summary>
/// One page of older messages, oldest first. Requested with the timestamp of
/// the oldest message the client already holds.
/// </summary>
public class GlunoMessagePageDto
{
    public List<GlunoMessageDto> Messages { get; set; } = new();
    public bool HasMore { get; set; }
}

public class SendGlunoMessageDto
{
    /// Continue an existing conversation. Omit to start a new one.
    public Guid? ConversationId { get; set; }
    /// Scope a NEW conversation to an Adventure. Ignored when ConversationId
    /// is given — a conversation's scope is fixed at creation.
    public Guid? TripId { get; set; }
    public string Message { get; set; } = string.Empty;

    /// <summary>
    /// A client-generated key that makes this send idempotent.
    ///
    /// Generated once per composed message and reused across retries, so a
    /// dropped connection or a double tap cannot produce a second answer, a
    /// second charge, or a second applicable proposal. The backend treats it as
    /// an opaque token — validated for shape, never parsed.
    ///
    /// Optional: an older client that omits it simply proceeds unprotected.
    /// </summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>
    /// The stable screen id the user opened Gluno from (see SideQuestScreens
    /// on the backend), when the client knows it.
    ///
    /// Only ever used to make help shorter and more relevant — an unknown or
    /// absent value degrades to generic instructions, never to a guess, and
    /// nothing acts on it.
    /// </summary>
    public string? Screen { get; set; }
}

public class GlunoTurnResponseDto
{
    public GlunoConversationDto Conversation { get; set; } = new();
    public GlunoMessageDto UserMessage { get; set; } = new();
    public GlunoMessageDto AssistantMessage { get; set; } = new();
}

/// <summary>
/// Whether Gluno can read documents in this environment. The app asks before
/// showing the action, so it never offers a button that cannot work.
/// </summary>
public class GlunoDocumentStatusDto
{
    public bool Available { get; set; }
    /// "disabled" | "not_configured" | null when available.
    public string? Reason { get; set; }
    /// Lowercase format names. A server fact, so the app does not maintain its
    /// own list and drift from ours.
    public List<string> SupportedFormats { get; set; } = new();
    public long MaxFileSizeBytes { get; set; }
}

/// <summary>
/// One reading of one document, as the app sees it.
///
/// Deliberately absent: the storage path, any signed URL, the document's text,
/// the provider's response, and the full confirmation numbers.
/// </summary>
public class GlunoDocumentAnalysisDto
{
    public Guid Id { get; set; }
    public Guid DocumentId { get; set; }
    /// pending | processing | completed | failed | cancelled | superseded
    public string Status { get; set; } = string.Empty;
    /// A stable code the app localises. Never a provider message.
    public string? FailureCode { get; set; }
    public int ExtractionVersion { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    /// When a human actually read the result. Nothing from the document enters
    /// Gluno's Adventure context before this is set.
    public DateTime? ReviewedAt { get; set; }
    /// The document changed since. This result describes a file that is gone.
    public bool IsSuperseded { get; set; }
    /// An existing analysis of the same bytes was returned instead of a new run.
    public bool WasReplay { get; set; }
    /// The document contains a QR code. Recorded as a fact; never decoded.
    public bool ContainsQrCode { get; set; }
    /// Hosts the document linked to, for information only. Never fetched.
    public List<string> LinkHosts { get; set; } = new();
    /// Something needs a human decision — an ambiguous date, a duplicate.
    public bool RequiresReview { get; set; }
    public List<GlunoDocumentItemDto> Items { get; set; } = new();
}

public class GlunoDocumentItemDto
{
    public string Id { get; set; } = string.Empty;
    /// flight | hotel | train | ferry | bus | car_rental | …
    public string Type { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Provider { get; set; }
    /// "•••• 4821". The full value never leaves the backend.
    public string? MaskedConfirmation { get; set; }
    public string? BookingStatus { get; set; }
    public string? StartDate { get; set; }
    public string? StartTime { get; set; }
    public string? EndDate { get; set; }
    public string? EndTime { get; set; }
    /// IANA id, only when a real place identified it. Never guessed.
    public string? TimeZoneId { get; set; }
    /// Exactly as the document printed it, so the user can compare.
    public string? StartDateOriginalText { get; set; }
    /// Non-empty means the date is genuinely ambiguous and the user must pick.
    public List<string> AlternativeDateReadings { get; set; } = new();
    public string? DepartureLocation { get; set; }
    public string? ArrivalLocation { get; set; }
    public string? Address { get; set; }
    /// high | medium | low | very_low. A bucket, not a number.
    public string ConfidenceBucket { get; set; } = "medium";
    public List<string> Warnings { get; set; } = new();
    public List<string> Blockers { get; set; } = new();
    public bool IsPossibleDuplicate { get; set; }
}

/// <summary>The items the user explicitly chose. There is no "all" shortcut.</summary>
public class GlunoDocumentProposalRequestDto
{
    public List<string>? ItemIds { get; set; }
}

/// <summary>
/// The Adventure's shared planning profile, as the app sees it.
///
/// Deliberately thin. It reports THAT constraints exist and how they clash —
/// never their values and never whose they are. A group screen showing "someone
/// needs short walking distances" is a screen that has revealed something
/// personal, however carefully it is worded.
/// </summary>
public class GlunoGroupProfileDto
{
    public int Version { get; set; }
    public int GroupSize { get; set; }
    /// How many members have shared anything. Never who.
    public int ContributingMembers { get; set; }
    /// Preference KEYS only — "pace", "walking_distance". Never the values.
    public List<string> SharedConstraintKeys { get; set; } = new();
    public int HardConstraintCount { get; set; }
    public List<GlunoGroupConflictDto> Conflicts { get; set; } = new();
}

/// <summary>
/// A clash between shared constraints.
///
/// The explanation names WHAT does not fit, never WHO. A planner that assigns
/// blame turns a scheduling problem into an argument.
/// </summary>
public class GlunoGroupConflictDto
{
    /// Stable machine code — "pace_mismatch", "walking_vs_distance".
    public string Type { get; set; } = string.Empty;
    /// "info" | "warning" | "blocking"
    public string Severity { get; set; } = string.Empty;
    /// Already localised. Never mentions a member.
    public string Explanation { get; set; } = string.Empty;
    /// One to three concrete ways forward.
    public List<string> Compromises { get; set; } = new();
    public bool RequiresGroupDecision { get; set; }
}

public class GlunoPreferenceVisibilityDto
{
    /// "private" | "trip_shared" | "global_private"
    public string? Visibility { get; set; }
    /// Whether the user states this as a hard requirement rather than a wish.
    public bool? IsHardConstraint { get; set; }
}

public class GlunoSharedPreferenceDto
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Visibility { get; set; } = string.Empty;
    public bool IsHardConstraint { get; set; }
}

public class GlunoGroupOptionDto
{
    public string Id { get; set; } = string.Empty;
    public string Label { get; set; } = string.Empty;
    public string? Summary { get; set; }
    /// How many chose it. Never WHO chose it.
    public int Votes { get; set; }
}

/// <summary>
/// A group decision and its current tally.
///
/// Counts and the caller's own vote. Never a per-member breakdown — a poll that
/// reveals individual votes is one people answer strategically rather than
/// honestly.
/// </summary>
public class GlunoGroupDecisionDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    /// The decision schema version. An unknown one is refused, not interpreted.
    public int Version { get; set; }
    /// "pace" | "budget" | "transport" | "day_plan_choice" | …
    public string Kind { get; set; } = string.Empty;
    public string Question { get; set; } = string.Empty;
    /// pending | accepted | rejected | expired | superseded
    public string Status { get; set; } = string.Empty;
    /// "all_voted" | "owner_closes" | "deadline"
    public string ClosingRule { get; set; } = string.Empty;
    public DateTime? ClosesAt { get; set; }
    public string? AcceptedOptionId { get; set; }
    public List<GlunoGroupOptionDto> Options { get; set; } = new();
    public int Responded { get; set; }
    public int GroupSize { get; set; }
    /// A tie is never resolved automatically — the group chooses again.
    public bool IsTie { get; set; }
    /// The caller's own choice. Null when they haven't answered or abstained.
    public string? MyVote { get; set; }
    public bool HasVoted { get; set; }
}

/// <summary>
/// One vote.
///
/// Note the absence of a userId: the voter is always the authenticated
/// principal, and there is no legitimate reason for a client to name a
/// different one.
/// </summary>
public class GlunoGroupVoteDto
{
    /// Null is a deliberate abstention, which is a real answer.
    public string? OptionId { get; set; }
}

/// <summary>
/// One signal from the app.
///
/// Note the absence of a userId: the author is always the authenticated
/// principal, and there is no legitimate reason for a client to name another.
/// </summary>
public class GlunoFeedbackDto
{
    public Guid ConversationId { get; set; }
    public Guid? TripId { get; set; }
    /// The assistant turn this is about.
    public Guid? MessageId { get; set; }
    public Guid? ProposalId { get; set; }
    /// Namespaced provider id when the feedback is about one recommendation.
    public string? RecommendationRef { get; set; }
    /// See GlunoFeedbackTypes on the backend. An unknown value is refused.
    public string? EventType { get; set; }
    /// A structured reason from the closed list the app offered.
    public string? Reason { get; set; }
    /// <summary>
    /// An optional short comment. Sanitised and capped server-side, stored as
    /// DATA — it never enters a prompt and nothing reads it for instructions.
    /// </summary>
    public string? Note { get; set; }
    /// conversation | trip | global
    public string? Scope { get; set; }
}

public class GlunoFeedbackResponseDto
{
    public bool Recorded { get; set; }
    /// <summary>
    /// Set only when a candidate JUST crossed its evidence threshold, so the
    /// app asks the confirmation question once rather than on every tap.
    /// </summary>
    public GlunoCandidateDto? ReadyCandidate { get; set; }
}

/// <summary>
/// A preference Gluno has noticed and has NOT started assuming.
///
/// Deliberately carries no evidence count and no confidence score. "We saw you
/// do this four times" reads as surveillance and does not help anyone decide.
/// </summary>
public class GlunoCandidateDto
{
    public Guid Id { get; set; }
    /// From the existing allow-list — "start_time", "pace", "budget".
    public string Key { get; set; } = string.Empty;
    public string ProposedValue { get; set; } = string.Empty;
    /// conversation | trip | global — what confirming would apply to.
    public string Scope { get; set; } = string.Empty;
    public Guid? TripId { get; set; }
}

public class GlunoCandidateDecisionDto
{
    public bool Confirm { get; set; }
    /// <summary>
    /// The scope the user chose. Global is never the default and never
    /// inferred — it takes an explicit choice.
    /// </summary>
    public string? Scope { get; set; }
}

/// <summary>
/// What Gluno currently assumes, for the user to inspect and change.
///
/// Product language, never raw feedback rows — a list of every tap somebody
/// made is a surveillance log, not a settings screen.
/// </summary>
public class GlunoLearnedDto
{
    public List<GlunoLearnedPreferenceDto> Preferences { get; set; } = new();
    public List<GlunoCandidateDto> Candidates { get; set; } = new();
    /// <summary>
    /// Titles for the Adventures referenced above, so the app can say "this
    /// Adventure" with a name attached instead of an id.
    ///
    /// Only Adventures the caller is still a member of appear here. Somebody
    /// who left keeps their own preference row — it is theirs — but does not
    /// keep learning the trip's name from it.
    /// </summary>
    public List<GlunoTripLabelDto> Trips { get; set; } = new();
}

public class GlunoTripLabelDto
{
    public Guid Id { get; set; }
    public string Title { get; set; } = string.Empty;
}

public class GlunoLearnedPreferenceDto
{
    public Guid Id { get; set; }
    public string Key { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    /// conversation | trip | global
    public string Scope { get; set; } = string.Empty;
    /// private | trip_shared | global_private
    public string Visibility { get; set; } = string.Empty;
    public bool IsHardConstraint { get; set; }
    public DateTime? ConfirmedAt { get; set; }
    /// Set on a trip-scoped preference, so the app can group by Adventure and
    /// show which one this applies to.
    public Guid? TripId { get; set; }
    /// Set on a conversation-scoped preference. The app uses it to say "only
    /// this conversation" about the RIGHT conversation.
    public Guid? ConversationId { get; set; }
    /// <summary>
    /// Which control to offer: choice | time | minutes | text | read_only.
    ///
    /// An app that does not recognise the kind must render the row read-only
    /// rather than guessing an editor.
    /// </summary>
    public string Editor { get; set; } = string.Empty;
    /// <summary>
    /// Stable option ids for a choice editor, empty otherwise.
    ///
    /// Ids, never display text — the app owns the localised wording, and
    /// shipping English through this field would put it on a Swedish screen.
    /// </summary>
    public List<string> Options { get; set; } = new();
}

/// <summary>
/// A change to one of the caller's OWN confirmed preferences.
///
/// No userId and no key: the row already knows whose it is and what it is
/// about. Only the value, and optionally the scope, can move.
/// </summary>
public class GlunoPreferenceUpdateDto
{
    public string? Value { get; set; }
    /// <summary>
    /// conversation | trip | global. Optional — omitting it leaves the scope
    /// alone.
    ///
    /// Widening to global is a real decision ("use this on every future trip")
    /// and the app confirms it separately. The server does not infer it.
    /// </summary>
    public string? Scope { get; set; }
}
