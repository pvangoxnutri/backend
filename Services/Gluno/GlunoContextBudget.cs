namespace sidequest.backend.Services.Gluno;

/// <summary>
/// What goes into the prompt when not all of it fits.
///
/// The order IS the design. Everything above a cut line survives; everything
/// below is dropped, in this order, and never randomly. A context that gets
/// trimmed arbitrarily produces an assistant that is subtly wrong in ways
/// nobody can reproduce — the same question answered well on Tuesday and badly
/// on Wednesday because a different slice happened to fall off.
/// </summary>
public enum GlunoContextPriority
{
    /// The rules and the safety boundaries. Never dropped; without them Gluno
    /// stops being Gluno.
    SystemRules = 0,
    /// What the user just asked. Dropping this is absurd, and stating it as
    /// priority 1 keeps it that way when someone reorders the rest.
    CurrentRequest = 1,
    /// The part of the Adventure the question is actually about.
    RelevantTrip = 2,
    /// A pending proposal and what the message pointed at.
    ProposalsAndReferences = 3,
    /// How they want to travel. Small, and dropping it makes Gluno ask again.
    Preferences = 4,
    /// The working summary of a long conversation.
    ConversationSummary = 5,
    /// The last few turns verbatim.
    RecentMessages = 6,
    /// The evidence ledger.
    Evidence = 7,
    /// Everything older. First to go, and rightly.
    OlderHistory = 8,
}

public sealed record GlunoContextSection(
    GlunoContextPriority Priority,
    string Name,
    string Content)
{
    /// <summary>
    /// True when this section must survive at any size.
    ///
    /// Used for the negations and hard constraints — "we don't want to hire a
    /// car", "no stairs" — which are SHORT and change every answer. Dropping
    /// one to save forty tokens produces a plan that is confidently wrong in
    /// exactly the way the user told us to avoid.
    /// </summary>
    public bool IsCritical { get; init; }

    public int EstimatedTokens => GlunoContextBudget.EstimateTokens(Content);
}

public sealed class GlunoContextResult
{
    public required string Json { get; init; }
    public required IReadOnlyDictionary<string, int> TokensByCategory { get; init; }
    public required int TotalTokens { get; init; }
    public required IReadOnlyList<string> DroppedSections { get; init; }

    /// <summary>
    /// True when even the protected sections do not fit.
    ///
    /// The correct response is a scoped, honest answer — "ask me about one day
    /// at a time" — not a randomly truncated context that produces a confident
    /// answer built on whatever survived.
    /// </summary>
    public required bool ExceedsBudgetEvenAfterTrimming { get; init; }
}

/// <summary>
/// Fits the turn's context into a token budget, dropping in priority order.
///
/// WHY A BUDGET AT ALL when the models have million-token windows. Three
/// reasons, and cost is the least interesting. Latency scales with input, so a
/// bloated context makes every answer slower. Attention does not distribute
/// evenly, so burying the actual question under the full itinerary measurably
/// degrades the answer. And a large context is mostly untrusted external text,
/// which is more surface for injected content.
///
/// So the aim is not to fit — it is to send the SMALLEST context that can
/// answer the question. A question about one Activity gets that Activity's day,
/// not the whole trip.
/// </summary>
public sealed class GlunoContextBudget
{
    private readonly IConfiguration _config;

    public GlunoContextBudget(IConfiguration config) => _config = config;

    /// <summary>
    /// Deliberately far below the model's window. This is a working budget
    /// chosen for latency and answer quality, not a technical ceiling.
    /// </summary>
    public int MaxTokens => Math.Clamp(_config.GetValue("Gluno:Context:MaxTokens", 24_000), 2_000, 200_000);

    /// <summary>
    /// Rough token estimate: about four characters per token.
    ///
    /// Approximate on purpose. An exact count needs the tokeniser, which means
    /// a dependency and a per-turn cost to answer a question where being 15%
    /// out changes nothing — the budget has far more slack than that.
    /// </summary>
    public static int EstimateTokens(string? text)
        => string.IsNullOrEmpty(text) ? 0 : (int)Math.Ceiling(text.Length / 4.0);

    /// <summary>
    /// Assembles the context, dropping the lowest-priority sections until it
    /// fits.
    /// </summary>
    public GlunoContextResult Fit(IReadOnlyList<GlunoContextSection> sections)
    {
        var budget = MaxTokens;

        var ordered = sections
            .OrderBy(section => (int)section.Priority)
            .ThenBy(section => section.Name, StringComparer.Ordinal)
            .ToList();

        var kept = new List<GlunoContextSection>();
        var dropped = new List<string>();
        var total = 0;

        foreach (var section in ordered)
        {
            var cost = section.EstimatedTokens;

            // Critical sections and the top three priorities are kept whatever
            // the arithmetic says. They are small, and they are what makes the
            // answer correct rather than merely fluent.
            var mustKeep = section.IsCritical || section.Priority <= GlunoContextPriority.RelevantTrip;

            if (mustKeep || total + cost <= budget)
            {
                kept.Add(section);
                total += cost;
                continue;
            }

            dropped.Add(section.Name);
        }

        var tokensByCategory = kept
            .GroupBy(section => section.Priority.ToString())
            .ToDictionary(group => group.Key, group => group.Sum(section => section.EstimatedTokens));

        var json = "{" + string.Join(",", kept.Select(section => $"\"{section.Name}\":{section.Content}")) + "}";

        return new GlunoContextResult
        {
            Json = json,
            TokensByCategory = tokensByCategory,
            TotalTokens = total,
            DroppedSections = dropped,
            // Even the protected core does not fit. The caller answers in a
            // scoped, honest way rather than cutting into what makes the answer
            // correct.
            ExceedsBudgetEvenAfterTrimming = total > budget,
        };
    }

    /// <summary>
    /// Narrows an Adventure to what the question is about.
    ///
    /// A question about Friday does not need Monday through Thursday. Sending
    /// them costs latency, dilutes attention, and gives the model more chances
    /// to answer about the wrong day.
    ///
    /// The day BEFORE and AFTER are kept: check-out times, an early flight and
    /// a late dinner on the neighbouring day all change what Friday can hold.
    /// </summary>
    public static GlunoTripContext NarrowToDate(GlunoTripContext trip, DateOnly? focusDate)
    {
        if (focusDate is not { } date) return trip;

        var from = date.AddDays(-1);
        var to = date.AddDays(1);

        return trip with
        {
            Activities = trip.Activities
                .Where(activity => activity.Date >= from && activity.Date <= to)
                .ToList(),
            Weather = trip.Weather
                .Where(weather => weather.Date >= from && weather.Date <= to)
                .ToList(),
            // Findings for other days would invite Gluno to answer about a day
            // nobody asked about.
            Findings = trip.Findings
                .Where(finding => finding.Date == null
                    || (DateOnly.TryParse(finding.Date, out var findingDate)
                        && findingDate >= from && findingDate <= to))
                .ToList(),
        };
    }

    /// <summary>
    /// The findings worth sending.
    ///
    /// Everything blocking, then the most severe of the rest. Sending all
    /// twenty produces an answer that recites a list instead of addressing the
    /// question.
    /// </summary>
    public static IReadOnlyList<TripFinding> RelevantFindings(
        IReadOnlyList<TripFinding> findings, string? focusDate, int max = 6)
        => findings
            .OrderByDescending(finding => finding.Date == focusDate)
            .ThenByDescending(finding => finding.Severity == "warning")
            .Take(max)
            .ToList();
}
