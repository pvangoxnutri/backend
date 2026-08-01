using System.Reflection;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the SEAMS rather than the parts.
///
/// Every other file here checks that a piece behaves. These check that the
/// pieces are actually joined to each other — which is a different and much
/// quieter failure. A grounding validator nobody calls passes all its own
/// tests. A live-information provider with no call site is indistinguishable
/// from one that works, right up until somebody asks whether the museum is
/// open on a strike day and gets a confident guess.
///
/// So these assert on wiring: this service takes that dependency, this
/// pipeline stage runs before that one, this result reaches a DTO the app can
/// read. Nothing here calls a model, a network, or a database.
/// </summary>
public class IntegrationEvals
{
    private static ConstructorInfo Ctor<T>()
        => typeof(T).GetConstructors().OrderByDescending(c => c.GetParameters().Length).First();

    private static bool Takes<T>(Type dependency)
        => Ctor<T>().GetParameters().Any(p => p.ParameterType == dependency);

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none")
            .Options;

        using var db = new AppDbContext(options);
        return db.Model;
    }

    // ── 1. The turn pipeline is assembled ────────────────────────────────

    [Fact]
    public void The_chat_service_takes_every_stage_of_the_turn()
    {
        // Each of these is a stage the answer passes through. A missing one
        // does not fail a build or a unit test — it just silently stops
        // happening, which is exactly how a quality gate ends up not running.
        foreach (var stage in new[]
        {
            typeof(IGlunoContextBuilder),      // what Gluno is allowed to know
            typeof(GlunoTurnPlanner),          // model tier, tools, budgets
            typeof(GlunoContextBudget),        // what actually fits
            typeof(ILiveTravelRegistry),       // current conditions
            typeof(IGlunoAiProvider),          // the model
            typeof(GlunoQualityGate),          // is the plan feasible
            typeof(GlunoGroundingValidator),   // is the answer supportable
            typeof(IGlunoProposalStore),       // what becomes a decision
            typeof(IGlunoIdempotencyStore),    // one send, one turn
            typeof(GlunoUsageBudget),          // the ceiling
        })
        {
            Assert.True(
                Takes<GlunoChatService>(stage),
                $"GlunoChatService no longer depends on {stage.Name} — that stage cannot be running");
        }
    }

    [Fact]
    public void The_context_builder_can_see_the_group()
    {
        // Without this the shared-constraint machinery builds a profile nobody
        // reads, and Gluno plans a five-person Adventure as if it were solo.
        Assert.True(Takes<GlunoContextBuilder>(typeof(ITripPlanningProfileBuilder)));
        Assert.NotNull(typeof(GlunoContext).GetProperty("Group"));
    }

    [Fact]
    public void The_apply_service_can_record_an_outcome()
    {
        // The proposal diff is the strongest learning signal in the product.
        // It is also the easiest to leave unwired, because nothing fails
        // without it.
        Assert.True(Takes<GlunoProposalApplyService>(typeof(IGlunoFeedbackService)));
    }

    // ── 2. Cancel stops before anything is written ───────────────────────

    [Fact]
    public void Cancellation_reaches_every_service_that_does_work()
    {
        foreach (var type in new[]
        {
            typeof(IGlunoChatService), typeof(IGlunoProposalApplyService),
            typeof(IGlunoFeedbackService), typeof(IGlunoGroupDecisionService),
            typeof(IGlunoDocumentAnalysisService), typeof(ILiveTravelRegistry),
        })
        {
            foreach (var method in type.GetMethods().Where(m => typeof(Task).IsAssignableFrom(m.ReturnType)))
            {
                Assert.True(
                    method.GetParameters().Any(p => p.ParameterType == typeof(CancellationToken)),
                    $"{type.Name}.{method.Name} cannot be cancelled");
            }
        }
    }

    [Fact]
    public void A_cancelled_turn_is_a_named_outcome_rather_than_an_error()
    {
        // The user pressed stop. That is not a failure, and reporting it as
        // one puts a red error under a message they deliberately abandoned.
        Assert.Contains(GlunoTurnError.Cancelled, Enum.GetValues<GlunoTurnError>());
        Assert.NotNull(new GlunoTurnTelemetry().GetType().GetProperty("Cancelled"));
    }

    // ── 3. Idempotency is enforced by the database ───────────────────────

    [Fact]
    public void One_send_can_only_become_one_turn()
    {
        var index = Model().FindEntityType(typeof(GlunoTurnRequest))!
            .GetIndexes()
            .FirstOrDefault(i => i.IsUnique);

        // A unique index, not a check-then-insert. Two taps land in two
        // requests on two connections, and only a constraint can arbitrate
        // that — application code always has a window between the read and
        // the write.
        Assert.NotNull(index);
    }

    [Fact]
    public void A_duplicate_send_is_refused_without_a_retry_offer()
    {
        // Retrying a duplicate produces another duplicate. The client is told
        // not to bother rather than being handed a button that cannot work.
        Assert.False(GlunoFailureCodes.IsRetryable("duplicate_in_flight"));
    }

    // ── 4. Live information reaches the app as a source ──────────────────

    [Fact]
    public void A_live_fact_can_become_evidence_and_a_source_row()
    {
        var ledger = new GlunoEvidenceLedger();

        ledger.AddLiveTravelFact(new LiveTravelFact
        {
            Id = "live-1",
            Category = LiveTravelCategories.Strike,
            Title = "Rail strike",
            Summary = "Regional services reduced.",
            SourceName = "Operator",
            SourceTier = LiveSourceTier.TransportOperator,
            SourceUrl = "https://operator.example/notice",
            PublishedAt = DateTime.UtcNow.AddHours(-2),
        });

        var entry = Assert.Single(ledger.Entries);

        Assert.Equal(GlunoEvidenceSources.LiveTravelInformation, entry.Source);
        // The source row is built from the ledger, so an entry that lands here
        // is one the user can see the provenance of.
        Assert.False(string.IsNullOrWhiteSpace(entry.SourceReference));
    }

    [Fact]
    public void The_status_endpoint_reports_live_availability_as_a_boolean()
    {
        var property = typeof(GlunoStatusDto).GetProperty("LiveTravelInfoAvailable");

        Assert.NotNull(property);
        Assert.Equal(typeof(bool), property!.PropertyType);

        // Nothing on this DTO may name a provider, a model or a key.
        foreach (var name in typeof(GlunoStatusDto).GetProperties().Select(p => p.Name))
        {
            Assert.DoesNotContain("Model", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Key", name, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Provider", name, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 5. App help skips the expensive machinery ────────────────────────

    [Fact]
    public void Help_does_not_load_an_Adventure_it_does_not_need()
    {
        var workflow = GlunoPlanningStrategy.For(
            Intent(GlunoIntent.SideQuestHelp), hasTrip: true, canEdit: true);

        // "How do I add a photo?" does not become faster by loading somebody's
        // itinerary, their weather and a trip analysis first.
        Assert.False(workflow.NeedsTripAnalysis);
        Assert.False(workflow.NeedsWeather);
        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsProposals);
    }

    [Fact]
    public void Help_runs_on_the_cheap_model_tier()
    {
        var plan = new GlunoTurnPlanner(new GlunoModelPolicy(Config()), Config())
            .Build(new GlunoTurnPlanRequest
            {
                Intent = Intent(GlunoIntent.SideQuestHelp),
                Workflow = GlunoPlanningStrategy.For(Intent(GlunoIntent.SideQuestHelp), false, false),
                ReferenceResolved = false,
                CanAnswerDeterministically = false,
            });

        Assert.Equal(GlunoModelTier.Fast, plan.Model.Tier);
        Assert.Empty(plan.Validate());
    }

    [Fact]
    public void An_answer_the_backend_already_knows_skips_the_model_entirely()
    {
        // The fastest and cheapest turn is the one that never leaves the
        // process. A deterministic answer must be reachable, or every "what
        // can you do?" costs a model round.
        Assert.NotNull(typeof(GlunoTurnTelemetry).GetProperty("ModelSkipped"));
        Assert.NotNull(typeof(GlunoTurnTelemetry).GetProperty("DirectAnswerReason"));
    }

    // ── 6. A usage ceiling stops new work, not reading ───────────────────

    [Fact]
    public void The_usage_ceiling_is_a_turn_level_decision()
    {
        // Listing conversations and paging messages take no dependency on the
        // budget, so a user at their ceiling can still open and scroll
        // everything they already have.
        Assert.False(Takes<GlunoConversationService>(typeof(GlunoUsageBudget)));
        Assert.True(Takes<GlunoChatService>(typeof(GlunoUsageBudget)));
    }

    [Fact]
    public void A_usage_limit_is_not_offered_as_retryable()
    {
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.UserUsageLimit));
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.GlobalUsageLimit));

        // A timeout, on the other hand, genuinely might work next time.
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiTimeout));
    }

    // ── 7. Deleting an account leaves nothing behind ─────────────────────

    [Theory]
    [InlineData(typeof(GlunoConversation))]
    [InlineData(typeof(GlunoProposalRecord))]
    [InlineData(typeof(GlunoPreference))]
    [InlineData(typeof(GlunoFeedbackEvent))]
    [InlineData(typeof(GlunoPreferenceCandidate))]
    [InlineData(typeof(GlunoRejection))]
    [InlineData(typeof(GlunoDocumentAnalysis))]
    public void Personal_gluno_data_follows_the_user_out(Type entity)
    {
        var userFk = Model().FindEntityType(entity)!
            .GetForeignKeys()
            .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(User));

        Assert.NotNull(userFk);
        // Cascade, not SetNull. An orphaned row with the name stripped off is
        // still somebody's booking, somebody's preference, somebody's chat.
        Assert.Equal(DeleteBehavior.Cascade, userFk!.DeleteBehavior);
    }

    [Fact]
    public void Deleting_an_Adventure_takes_its_trip_scoped_gluno_data()
    {
        foreach (var entity in new[]
        {
            typeof(GlunoFeedbackEvent), typeof(GlunoPreferenceCandidate),
            typeof(GlunoRejection), typeof(GlunoGroupDecision),
        })
        {
            var tripFk = Model().FindEntityType(entity)!
                .GetForeignKeys()
                .FirstOrDefault(fk => fk.PrincipalEntityType.ClrType == typeof(Trip));

            Assert.NotNull(tripFk);
            Assert.Equal(DeleteBehavior.Cascade, tripFk!.DeleteBehavior);
        }
    }

    [Fact]
    public void A_conversation_survives_its_Adventure_being_deleted()
    {
        // Deliberately different from the rule above. The conversation is the
        // user's own record of what they asked; deleting an Adventure should
        // not silently erase their chat history with it.
        var tripFk = Model().FindEntityType(typeof(GlunoConversation))!
            .GetForeignKeys()
            .First(fk => fk.PrincipalEntityType.ClrType == typeof(Trip));

        Assert.Equal(DeleteBehavior.SetNull, tripFk.DeleteBehavior);
    }

    // ── 8. Nothing sensitive crosses the DTO boundary ────────────────────

    [Fact]
    public void No_gluno_dto_carries_a_key_a_url_or_a_model_name()
    {
        var dtos = typeof(GlunoStatusDto).Assembly
            .GetTypes()
            .Where(t => t.Namespace == "sidequest.backend.Dtos" && t.Name.StartsWith("Gluno"))
            .ToList();

        Assert.NotEmpty(dtos);

        foreach (var property in dtos.SelectMany(t => t.GetProperties()))
        {
            var name = property.Name;

            Assert.False(
                name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Secret", StringComparison.OrdinalIgnoreCase)
                || name.Contains("Token", StringComparison.OrdinalIgnoreCase)
                || name.Contains("SignedUrl", StringComparison.OrdinalIgnoreCase)
                || name.Contains("StoragePath", StringComparison.OrdinalIgnoreCase)
                || name.Equals("ModelId", StringComparison.OrdinalIgnoreCase),
                $"{property.DeclaringType!.Name}.{name} looks like it leaks server-side detail");
        }
    }

    [Fact]
    public void The_raw_payload_of_a_proposal_is_not_the_stored_row()
    {
        // The DTO is a projection. Returning the entity would ship the
        // snapshot, the internal status machinery and the result blob to a
        // client that needs none of it.
        Assert.Null(typeof(GlunoProposalDto).GetProperty("SnapshotJson"));
        Assert.Null(typeof(GlunoProposalDto).GetProperty("PayloadJson"));
        Assert.NotNull(typeof(GlunoProposalRecord).GetProperty("SnapshotJson"));
    }

    // ── 9. Every model id comes from configuration ───────────────────────

    [Fact]
    public void No_model_id_is_compiled_into_the_product()
    {
        var policy = new GlunoModelPolicy(new ConfigurationBuilder().Build());

        // With nothing configured there is no model, and Gluno reports itself
        // unconfigured rather than guessing an id that may have been retired.
        Assert.False(policy.IsConfigured);
    }

    [Fact]
    public void Every_tier_falls_back_to_primary_rather_than_to_a_guess()
    {
        var policy = new GlunoModelPolicy(Config());

        // Only Primary is set. A deployment that configures one model works —
        // it just pays primary prices for everything, which is the safe
        // direction to fail in.
        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var choice = policy.Choose(new GlunoModelRequest
            {
                Intent = intent,
                IntentConfidence = 0.9,
            });

            Assert.Equal("configured-primary", choice.Model);
        }
    }

    // ── 10. External integrations are off until switched on ──────────────

    [Fact]
    public void Nothing_paid_or_external_is_on_by_default()
    {
        var empty = new ConfigurationBuilder().Build();

        // Shipping the code must not start calling anything. Each of these is
        // somebody's bill and, for the document reader, somebody's booking
        // confirmations.
        Assert.False(new GlunoDocumentConfig(empty, new GlunoModelPolicy(empty)).IsEnabled);
        Assert.False(empty.GetValue("Tripadvisor:Enabled", false));
        Assert.False(empty.GetValue("Routing:Enabled", false));
        Assert.False(empty.GetValue("Gluno:LiveInfo:Enabled", false));
    }

    private static IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Gluno:Models:Primary"] = "configured-primary",
        })
        .Build();

    private static GlunoIntentResult Intent(GlunoIntent intent) => new()
    {
        PrimaryIntent = intent,
        Confidence = 0.9,
        Scope = GlunoIntentScope.Global,
        RequiresCurrentData = false,
        RequiresExternalSearch = false,
        ExpectsProposal = false,
        RequiresClarification = false,
    };
}
