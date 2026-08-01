using System.Diagnostics;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One structured log line per Gluno turn.
///
/// WHAT IS HERE AND WHY IT IS SAFE. Every field below is a shape, a count, a
/// duration or a machine code. Nothing on this object could identify a person,
/// reveal where they are going, what they eat, who they travel with, or what
/// they typed.
///
/// WHAT IS DELIBERATELY ABSENT, and must stay absent: the user's message, the
/// model's answer, the trip context, preferences, place names, coordinates,
/// provider payloads, and any part of a proposal. Those are the whole point of
/// the product and none of them belong in a log aggregator. The one identifier
/// carried is the conversation id, because a support question about a specific
/// conversation is otherwise unanswerable — and it is a Guid that resolves to
/// nothing without database access.
///
/// This is what makes the system debuggable in production without making it
/// surveillable. "intent=plan_empty_day tools=2 providerCalls=1 rounds=3
/// proposal=true gate=passed 4.1s" answers almost every operational question
/// anyone actually has.
/// </summary>
public sealed class GlunoTurnTelemetry
{
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly List<string> _tools = [];

    public Guid ConversationId { get; init; }

    public string Intent { get; set; } = "unknown";
    public double IntentConfidence { get; set; }

    /// walking | trip | day | activity — how far the turn reached.
    public string Scope { get; set; } = "global";

    /// How many model round trips the turn spent, tool loops included.
    public int ModelRounds { get; set; }

    /// Calls that left the process to a third party.
    public int ProviderCalls { get; set; }
    public long ProviderMilliseconds { get; set; }

    public bool ProposalCreated { get; set; }

    /// passed | blocked | skipped
    public string QualityGate { get; set; } = "skipped";
    public int QualityBlockers { get; set; }
    public int QualityWarnings { get; set; }

    public bool ReviewRan { get; set; }
    public bool RevisionRan { get; set; }

    /// A machine code, never a message: "provider_failed", "refused",
    /// "iteration_limit", "reference_ambiguous".
    public string? FailureCategory { get; set; }

    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }

    // ── Grounding ─────────────────────────────────────────────────────────
    //
    // Counts and category codes only. An evidence VALUE is a rating, a place
    // name, a forecast for somebody's holiday — none of it belongs in a log,
    // and none of it is needed to answer an operational question.

    /// How many evidence entries came from each source. Keyed by source id.
    private readonly Dictionary<string, int> _evidenceBySource = new(StringComparer.Ordinal);

    public int UnsupportedClaims { get; set; }
    public int SafeCorrectionsApplied { get; set; }
    public int RegenerationCount { get; set; }
    /// "no_evidence", "stale", "wrong_date_or_location", "contradiction".
    public string? GroundingFailureCategory { get; set; }
    public int StaleEvidenceUsed { get; set; }
    public int AttributionErrors { get; set; }
    /// How many external strings tripped the injection detector this turn.
    public int InjectionSignals { get; private set; }
    /// Which fallback the user ended up with, if any.
    public string? FinalFallbackUsed { get; set; }

    // ── Orchestration ─────────────────────────────────────────────────────
    //
    // Shapes and outcomes. Still nothing about WHAT was asked or answered.

    /// "Fast:app_help", "Primary:complex_planning". A tier and a reason, never
    /// the model id — that is deployment configuration.
    public string? ModelPolicy { get; set; }

    /// True when the turn was answered from structured data with no model call.
    public bool ModelSkipped { get; set; }
    public string? DirectAnswerReason { get; set; }

    /// "app_help", "day_plan", "recommendation".
    public string? PlanType { get; set; }

    /// Context size per priority category — counts only, never the content.
    private IReadOnlyDictionary<string, int> _contextTokens = new Dictionary<string, int>();

    /// How many tool calls actually ran concurrently.
    public int ToolParallelism { get; set; }

    /// The user pressed stop. Not a failure.
    public bool Cancelled { get; set; }

    /// <see cref="GlunoDegradationLevel"/>.
    public string? DegradationLevel { get; set; }

    /// "Allowed", "UserLimitReached", "GlobalLimitReached".
    public string? UsageLimit { get; set; }

    /// A bucket, not a figure — see GlunoUsageBudget.CostBucket.
    public string? CostBucket { get; set; }

    public int CacheHits { get; set; }

    /// "in_flight" or "completed" when a duplicate send was detected.
    public string? IdempotencyReplay { get; set; }

    /// Counts only. Never a query, a place name or a source URL.
    public int LiveSearches { get; set; }
    public int LiveFacts { get; set; }
    public int LiveConflicts { get; set; }

    private IReadOnlyDictionary<string, long> _stageMs = new Dictionary<string, long>();

    public void RecordStages(GlunoLatencyTracker tracker) => _stageMs = tracker.StageMilliseconds;

    public void RecordContextTokens(IReadOnlyDictionary<string, int> tokensByCategory)
        => _contextTokens = tokensByCategory;

    /// <summary>
    /// Records only the SHAPE of the ledger — how many entries, from which
    /// sources. Never a value.
    /// </summary>
    public void RecordEvidence(GlunoEvidenceLedger ledger)
    {
        _evidenceBySource.Clear();

        foreach (var entry in ledger.Entries)
        {
            _evidenceBySource[entry.Source] = _evidenceBySource.GetValueOrDefault(entry.Source) + 1;
        }
    }

    /// The detector's CODE, never the text that tripped it — the text is
    /// exactly the untrusted content we are trying to keep contained.
    public void RecordInjectionSignal(string signal)
    {
        InjectionSignals++;
        if (_injectionSignals.Count < 5) _injectionSignals.Add(signal);
    }

    private readonly List<string> _injectionSignals = [];

    /// <summary>
    /// Tool NAMES only. They come from a fixed allow-list in GlunoActions, so
    /// there is no path by which user content reaches this field.
    /// </summary>
    public void RecordTool(string name)
    {
        if (_tools.Count < 12) _tools.Add(name);
    }

    public void RecordProviderCall(long milliseconds)
    {
        ProviderCalls++;
        ProviderMilliseconds += Math.Max(0, milliseconds);
    }

    public void Write(ILogger logger)
    {
        _stopwatch.Stop();

        // Structured properties rather than an interpolated string, so a log
        // backend can aggregate on intent or gate outcome without parsing.
        logger.LogInformation(
            "[GLUNO] turn conversation={Conversation} intent={Intent} confidence={Confidence} scope={Scope} " +
            "tools={Tools} toolCount={ToolCount} providerCalls={ProviderCalls} providerMs={ProviderMs} " +
            "rounds={Rounds} proposal={Proposal} gate={Gate} blockers={Blockers} warnings={Warnings} " +
            "review={Review} revision={Revision} failure={Failure} inTokens={InTokens} outTokens={OutTokens} " +
            "evidence={Evidence} evidenceCount={EvidenceCount} unsupported={Unsupported} " +
            "corrections={Corrections} regenerations={Regenerations} groundingFailure={GroundingFailure} " +
            "staleEvidence={StaleEvidence} attributionErrors={AttributionErrors} " +
            "injectionSignals={InjectionSignals} injectionKinds={InjectionKinds} fallback={Fallback} " +
            "modelPolicy={ModelPolicy} modelSkipped={ModelSkipped} direct={Direct} planType={PlanType} " +
            "contextTokens={ContextTokens} parallelism={Parallelism} stages={Stages} cancelled={Cancelled} " +
            "degradation={Degradation} usageLimit={UsageLimit} cost={CostBucket} cacheHits={CacheHits} " +
            "idempotency={Idempotency} liveSearches={LiveSearches} liveFacts={LiveFacts} " +
            "liveConflicts={LiveConflicts} totalMs={TotalMs}",
            ConversationId,
            Intent,
            Math.Round(IntentConfidence, 2),
            Scope,
            string.Join(',', _tools),
            _tools.Count,
            ProviderCalls,
            ProviderMilliseconds,
            ModelRounds,
            ProposalCreated,
            QualityGate,
            QualityBlockers,
            QualityWarnings,
            ReviewRan,
            RevisionRan,
            FailureCategory ?? "none",
            InputTokens,
            OutputTokens,
            // "sidequest_db=12,tripadvisor=3" — counts per source, no values.
            string.Join(',', _evidenceBySource.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
            _evidenceBySource.Values.Sum(),
            UnsupportedClaims,
            SafeCorrectionsApplied,
            RegenerationCount,
            GroundingFailureCategory ?? "none",
            StaleEvidenceUsed,
            AttributionErrors,
            InjectionSignals,
            string.Join(',', _injectionSignals),
            FinalFallbackUsed ?? "none",
            ModelPolicy ?? "none",
            ModelSkipped,
            DirectAnswerReason ?? "none",
            PlanType ?? "none",
            // "RelevantTrip=1200,Evidence=400" — counts per category, never the
            // content of any of them.
            string.Join(',', _contextTokens.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
            ToolParallelism,
            string.Join(',', _stageMs.OrderBy(pair => pair.Key).Select(pair => $"{pair.Key}={pair.Value}")),
            Cancelled,
            DegradationLevel ?? "Full",
            UsageLimit ?? "Allowed",
            CostBucket ?? "unpriced",
            CacheHits,
            IdempotencyReplay ?? "none",
            LiveSearches,
            LiveFacts,
            LiveConflicts,
            _stopwatch.ElapsedMilliseconds);
    }
}
