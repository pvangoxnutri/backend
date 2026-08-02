using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;

namespace sidequest.backend.Services.Gluno;

/// <summary>An Activity the conversation has actually talked about.</summary>
public sealed record MentionedActivity(Guid Id, string Title, string Date, string? Category, string Role)
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
}

/// <summary>
/// A place from a provider, kept only as much as a follow-up needs.
///
/// Deliberately NOT the provider payload. Storing rating, review text, photos
/// and hours would mean a second copy of third-party data ageing quietly in our
/// database — and the freshness rules that govern opening hours could not be
/// applied to it. What survives is identity plus enough to re-fetch: id, name,
/// where it is, and when we last looked.
/// </summary>
public sealed record MentionedPlace(string ExternalId, string Name, string? Category)
{
    public double? Latitude { get; init; }
    public double? Longitude { get; init; }
    /// When the details behind this id were last fetched. Drives whether a
    /// follow-up can reuse them or has to refresh.
    public DateTime FetchedAtUtc { get; init; } = DateTime.UtcNow;
    /// Position in the list the user was shown. "The second one" is this.
    public int Position { get; init; }
}

public sealed record MentionedProposal(Guid Id, string Kind, string Summary, string Status);

public sealed record MentionedDayLocation(string Label, string Date, double? Latitude, double? Longitude);

/// <summary>
/// Something the user said no to.
///
/// Kept so it is not offered again two turns later, which is the single most
/// irritating thing a recommender does. <see cref="Reason"/> is the user's
/// stated objection when they gave one — "too expensive" narrows what to
/// suggest next, where a bare rejection does not.
/// </summary>
public sealed record RejectedOption(string Kind, string Id, string Label, string? Reason);

/// <summary>
/// The structured summary of a long conversation.
///
/// Everything here is a DECISION or an OPEN QUESTION — the things that change
/// what the next answer should be. Chat that led nowhere is not summarised,
/// because carrying it forward would only crowd out what matters.
///
/// The one rule that needs stating: negations survive. "We don't want to hire a
/// car" compressed into "transport: car" is worse than no summary at all, so
/// preferences are carried verbatim in the user's own words rather than
/// paraphrased.
/// </summary>
public sealed class GlunoWorkingState
{
    /// Bumped whenever this shape changes. A row from an older version is
    /// discarded rather than reinterpreted.
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    /// What the user is trying to get done right now, in a few words.
    public string? Goal { get; set; }

    /// Preferences already settled, verbatim. Read before asking anything —
    /// asking twice about the budget is how an expert starts to feel like a form.
    public List<GlunoStatePreference> DecidedPreferences { get; set; } = [];

    public List<RejectedOption> RejectedOptions { get; set; } = [];

    /// Places the user said yes to, or that ended up in a proposal.
    public List<MentionedPlace> ChosenPlaces { get; set; } = [];

    public List<Guid> PendingProposalIds { get; set; } = [];

    /// Dates that came up and still matter, yyyy-MM-dd.
    public List<string> KeyDates { get; set; } = [];

    /// Questions Gluno asked that the user has not answered. Prevents asking
    /// the same one again, and lets a later turn pick the thread back up.
    public List<string> OpenQuestions { get; set; } = [];

    public GlunoRecentMentions Recent { get; set; } = new();

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>
    /// True when the last turn left something a pronoun could point at. Feeds
    /// the intent router: "the second one" is only a follow-up if there was a
    /// list.
    /// </summary>
    public bool HasReferents()
        => Recent.Places.Count > 0
        || Recent.Activities.Count > 0
        || Recent.Proposals.Count > 0
        || Recent.DayLocations.Count > 0;
}

public sealed record GlunoStatePreference(string Key, string Value);

/// <summary>
/// What the last few turns put on the table, newest first.
///
/// Bounded hard. This is working memory, not history — the transcript is the
/// history. An unbounded list would grow into the context window and slow every
/// turn down for the sake of a reference nobody is going to make.
/// </summary>
public sealed class GlunoRecentMentions
{
    public const int MaxActivities = 8;
    public const int MaxPlaces = 8;
    public const int MaxProposals = 5;
    public const int MaxDates = 6;
    public const int MaxDayLocations = 5;
    public const int MaxHotels = 3;
    public const int MaxNavigationTargets = 3;

    /// <summary>
    /// The Adventure this conversation last actually settled on.
    ///
    /// WHY IT IS HERE. A global conversation can be about a trip without being
    /// scoped to one: somebody asks about Semester 2026, gets an answer, then
    /// says "and now?". The second message names nothing, so without this the
    /// turn has no trip and Gluno asks which Adventure — about the one it
    /// answered ten seconds ago.
    ///
    /// WRITTEN ONLY FROM A VERIFIED RESOLUTION: an Adventure the user named
    /// outright, one they tapped, or one the Adventure header supplied. Never
    /// from a guess and never from the model, which has no way to produce a
    /// trip id at all.
    ///
    /// A WEAK SIGNAL BY DESIGN. It is the last thing consulted, it never
    /// overrides a trip the current message names, and it is re-verified
    /// against membership every time it is used — a trip that was deleted or
    /// left in the meantime is simply not a candidate.
    /// </summary>
    public Guid? LastAdventureId { get; set; }

    public List<MentionedActivity> Activities { get; set; } = [];
    public List<MentionedPlace> Places { get; set; } = [];
    public List<MentionedProposal> Proposals { get; set; } = [];
    public List<string> Dates { get; set; } = [];
    public List<MentionedDayLocation> DayLocations { get; set; } = [];
    public List<MentionedActivity> Hotels { get; set; } = [];
    public List<string> NavigationTargets { get; set; } = [];

    /// <summary>
    /// Newest first, de-duplicated, capped. Re-mentioning something moves it to
    /// the front rather than adding a second copy — recency is exactly what
    /// "the one we just talked about" means.
    /// </summary>
    public static void Promote<T>(List<T> list, T item, Func<T, string> key, int max)
    {
        var identity = key(item);
        list.RemoveAll(existing => key(existing) == identity);
        list.Insert(0, item);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
    }
}

public interface IGlunoWorkingStateStore
{
    /// <summary>
    /// The conversation's working memory, or a fresh one. Never throws on a
    /// malformed or out-of-version row — continuity is a nice-to-have, and a
    /// corrupt row must not be able to break a turn.
    /// </summary>
    Task<GlunoWorkingState> LoadAsync(Guid conversationId, CancellationToken ct);

    Task SaveAsync(Guid conversationId, GlunoWorkingState state, CancellationToken ct);
}

public sealed class GlunoWorkingStateStore : IGlunoWorkingStateStore
{
    private readonly AppDbContext _db;
    private readonly ILogger<GlunoWorkingStateStore> _logger;

    public GlunoWorkingStateStore(AppDbContext db, ILogger<GlunoWorkingStateStore> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task<GlunoWorkingState> LoadAsync(Guid conversationId, CancellationToken ct)
    {
        var row = await _db.GlunoConversationStates
            .AsNoTracking()
            .FirstOrDefaultAsync(state => state.ConversationId == conversationId, ct);

        if (row == null) return new GlunoWorkingState();

        // An older format is discarded, not migrated in place. Reinterpreting
        // one shape as another is how a reference silently resolves to the
        // wrong object, and one turn of lost continuity is much cheaper.
        if (row.Version != GlunoWorkingState.CurrentVersion)
        {
            _logger.LogInformation(
                "[GLUNO] working state version {Found} != {Expected}, rebuilding",
                row.Version, GlunoWorkingState.CurrentVersion);
            return new GlunoWorkingState();
        }

        try
        {
            return JsonSerializer.Deserialize<GlunoWorkingState>(row.StateJson, GlunoJson.Options)
                ?? new GlunoWorkingState();
        }
        catch (JsonException)
        {
            // Category only. The payload holds conversation-derived data.
            _logger.LogWarning("[GLUNO] working state could not be read, rebuilding");
            return new GlunoWorkingState();
        }
    }

    public async Task SaveAsync(Guid conversationId, GlunoWorkingState state, CancellationToken ct)
    {
        state.Version = GlunoWorkingState.CurrentVersion;
        state.UpdatedAtUtc = DateTime.UtcNow;

        var json = JsonSerializer.Serialize(state, GlunoJson.Options);

        var row = await _db.GlunoConversationStates
            .FirstOrDefaultAsync(existing => existing.ConversationId == conversationId, ct);

        if (row == null)
        {
            _db.GlunoConversationStates.Add(new Models.GlunoConversationState
            {
                ConversationId = conversationId,
                Version = GlunoWorkingState.CurrentVersion,
                StateJson = json,
            });
        }
        else
        {
            row.Version = GlunoWorkingState.CurrentVersion;
            row.StateJson = json;
            row.UpdatedAt = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(ct);
    }
}
