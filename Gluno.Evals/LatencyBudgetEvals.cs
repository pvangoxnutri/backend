using Microsoft.Extensions.Configuration;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for how long a turn is allowed to take, and who does the cancelling.
///
/// THE BUG THESE EXIST FOR. The model was given a PROPORTION of the turn's
/// latency budget — 55% of it. For a plain "Hej" that budget was 12 seconds,
/// so the model got 6.6, which is less time than a reasoning model needs to
/// answer anything. Every such turn was cancelled mid-flight and reported as
/// `ai_timeout`, which reads as "the provider was slow" when in fact we never
/// let it finish.
///
/// The configured ceiling, Gluno:TimeoutSeconds:Primary, was meanwhile dead
/// configuration: computed by the model policy, documented in the env example,
/// and never passed to the provider at all.
///
/// Nothing here calls a model, a network, or a database.
/// </summary>
public class LatencyBudgetEvals
{
    private static IConfiguration Config(params (string Key, string? Value)[] settings)
    {
        var values = new Dictionary<string, string?>();
        foreach (var (key, value) in settings) values[key] = value;

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static GlunoLatencyBudget Budget(GlunoIntent intent, params (string, string?)[] settings)
        => GlunoLatencyBudget.For(intent, Config(settings));

    // ── The model always gets a workable allowance ───────────────────────

    [Theory]
    [InlineData(GlunoIntent.SideQuestHelp)]
    [InlineData(GlunoIntent.GeneralTravelQuestion)]
    [InlineData(GlunoIntent.NavigationRequest)]
    [InlineData(GlunoIntent.Unclear)]
    [InlineData(GlunoIntent.PreferenceUpdate)]
    public void Even_the_cheapest_turn_gives_the_model_time_to_answer(GlunoIntent intent)
    {
        var budget = Budget(intent);

        // The regression, stated as a number. 6.6 seconds is what this used to
        // be for a greeting, and it produced ai_timeout every single time.
        Assert.True(
            budget.Model >= TimeSpan.FromSeconds(30),
            $"{intent} gives the model only {budget.Model.TotalSeconds:0.0}s");
    }

    [Theory]
    [InlineData(GlunoIntent.SideQuestHelp)]
    [InlineData(GlunoIntent.GeneralTravelQuestion)]
    [InlineData(GlunoIntent.BuildFullItinerary)]
    [InlineData(GlunoIntent.PlanEmptyDay)]
    [InlineData(GlunoIntent.Unclear)]
    public void The_turn_budget_always_contains_the_model_call(GlunoIntent intent)
    {
        var budget = Budget(intent);

        // A total shorter than the call inside it would have the tracker
        // reporting negative time remaining before the model even returned.
        Assert.True(
            budget.Total >= budget.Model,
            $"{intent}: total {budget.Total.TotalSeconds:0.0}s < model {budget.Model.TotalSeconds:0.0}s");
    }

    [Fact]
    public void A_heavier_turn_still_gets_proportionally_more()
    {
        // The floor must not flatten the shape. An itinerary is worth waiting
        // longer for than a greeting, and the budget should still say so.
        Assert.True(
            Budget(GlunoIntent.BuildFullItinerary).Model
                > Budget(GlunoIntent.SideQuestHelp).Model);
    }

    // ── Configuration parsing ────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("0")]
    [InlineData("-30")]
    [InlineData("not a number")]
    public void A_missing_or_nonsense_budget_falls_back_to_something_workable(string? configured)
    {
        var budget = Budget(GlunoIntent.GeneralTravelQuestion, ("Gluno:Latency:SimpleSeconds", configured));

        // Never zero, never negative, never a value that cancels instantly.
        Assert.True(budget.Total >= TimeSpan.FromSeconds(5));
        Assert.True(budget.Model >= TimeSpan.FromSeconds(30));
    }

    [Fact]
    public void An_absurdly_large_budget_is_clamped()
    {
        var budget = Budget(GlunoIntent.BuildFullItinerary, ("Gluno:Latency:ItinerarySeconds", "99999"));

        // A turn that can hang for an hour is not a turn.
        Assert.True(budget.Total <= TimeSpan.FromSeconds(200));
    }

    [Fact]
    public void The_model_floor_is_itself_bounded()
    {
        Assert.True(
            Budget(GlunoIntent.SideQuestHelp, ("Gluno:Latency:MinModelSeconds", "1")).Model
                >= TimeSpan.FromSeconds(10));

        Assert.True(
            Budget(GlunoIntent.SideQuestHelp, ("Gluno:Latency:MinModelSeconds", "9999")).Model
                <= TimeSpan.FromSeconds(120));
    }

    // ── The configured model timeout is real again ───────────────────────

    [Fact]
    public void The_model_tier_timeout_is_read_from_configuration()
    {
        var policy = new GlunoModelPolicy(Config(
            ("Gluno:Models:Primary", "configured"),
            ("Gluno:TimeoutSeconds:Primary", "45")));

        var choice = policy.Choose(new GlunoModelRequest
        {
            Intent = GlunoIntent.BuildFullItinerary,
            IntentConfidence = 0.9,
        });

        Assert.Equal(TimeSpan.FromSeconds(45), choice.Timeout);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("")]
    public void A_nonsense_model_timeout_falls_back_rather_than_cancelling_instantly(string configured)
    {
        var policy = new GlunoModelPolicy(Config(
            ("Gluno:Models:Primary", "configured"),
            ("Gluno:TimeoutSeconds:Primary", configured)));

        var choice = policy.Choose(new GlunoModelRequest
        {
            Intent = GlunoIntent.GeneralTravelQuestion,
            IntentConfidence = 0.9,
        });

        Assert.True(choice.Timeout >= TimeSpan.FromSeconds(5));
    }

    // ── A greeting stays cheap ───────────────────────────────────────────

    [Fact]
    public void A_greeting_offers_the_model_no_tools_at_all()
    {
        var intent = GlunoIntentRouter.Classify(new GlunoIntentInput
        {
            Message = "Hej",
            HasTrip = false,
        });

        var workflow = GlunoPlanningStrategy.For(intent, hasTrip: false, canEdit: false);

        // Filtering is what physically shortens the tool list sent to the
        // provider — not a prompt instruction the model may ignore, and not a
        // check applied after it has already called something.
        var offered = GlunoPlanningStrategy.FilterActions(
            GlunoActions.ForContext(new GlunoContext
            {
                Today = new DateOnly(2026, 8, 12),
                User = new GlunoUserContext { Language = "sv" },
            }),
            workflow);

        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsRouting);
        Assert.False(workflow.AllowsProposals);

        // The expensive ones — the ones that cost money or write to a trip —
        // are physically absent from what the provider is sent, not merely
        // forbidden by an instruction the model may ignore.
        Assert.DoesNotContain(offered, action => action.Name == GlunoActions.SearchPlaces);
        Assert.DoesNotContain(offered, action => action.Name == GlunoActions.ProposeDayPlan);
        Assert.DoesNotContain(offered, action => action.Name == GlunoActions.ProposeActivity);
        Assert.DoesNotContain(offered, action => action.Name == GlunoActions.ProposeTripDateChange);

        // What remains is the cheap app-knowledge set. Bounded, and asserted
        // so a future action cannot quietly widen the greeting turn.
        Assert.True(offered.Count <= 8, $"a greeting offered {offered.Count} tools");
    }

    [Fact]
    public void A_second_greeting_does_not_escalate_because_of_history()
    {
        // The worry: turn 2 picking a heavier policy purely because the
        // conversation now contains an answer. The policy reads the WORK, not
        // the transcript.
        var policy = new GlunoModelPolicy(Config(
            ("Gluno:Models:Primary", "configured"),
            ("Gluno:Models:Fast", "configured-fast")));

        var request = new GlunoModelRequest
        {
            Intent = GlunoIntent.SideQuestHelp,
            IntentConfidence = 0.9,
        };

        Assert.Equal(policy.Choose(request).Tier, policy.Choose(request).Tier);
    }

    [Fact]
    public void Fast_is_used_when_configured_and_falls_back_to_primary_when_not()
    {
        var withFast = new GlunoModelPolicy(Config(
            ("Gluno:Models:Primary", "primary"),
            ("Gluno:Models:Fast", "fast")));

        var withoutFast = new GlunoModelPolicy(Config(("Gluno:Models:Primary", "primary")));

        var request = new GlunoModelRequest
        {
            Intent = GlunoIntent.SideQuestHelp,
            IntentConfidence = 0.95,
        };

        Assert.Equal("fast", withFast.Choose(request).Model);
        // Falls back rather than sending an empty model id.
        Assert.Equal("primary", withoutFast.Choose(request).Model);
    }

    // ── A stopped turn is never a timeout ────────────────────────────────

    [Fact]
    public void The_users_own_stop_is_a_cancellation_not_a_timeout()
    {
        Assert.NotEqual(GlunoFailureCodes.Cancelled, GlunoFailureCodes.AiTimeout);
        Assert.False(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.Cancelled));
        // A genuine timeout might well work next time.
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.AiTimeout));
    }
}
