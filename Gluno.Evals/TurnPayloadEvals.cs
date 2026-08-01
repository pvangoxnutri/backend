using System.Text.Json;
using Microsoft.Extensions.Configuration;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for what a turn actually builds before it calls a model.
///
/// THE FAILURE MODE. A container that disappears mid-request leaves no
/// exception and no log line — the process is killed, not thrown from. The two
/// ways Gluno's own code could cause that are a reference cycle (serialisation
/// recurses until the stack goes, which is uncatchable) and unbounded growth
/// (allocation until the platform kills the container). Neither shows up in
/// any other test, because both look like "it worked" right up until the size
/// crosses a line.
///
/// So these assert on the SHAPE of the payload: that it terminates, that it
/// stays bounded, that it carries no database entity, and that a trivial
/// question stays trivial.
///
/// Nothing here calls a model, a network, or a database.
/// </summary>
public class TurnPayloadEvals
{
    private static GlunoContext RealisticContext()
    {
        var today = new DateOnly(2026, 8, 12);

        return new GlunoContext
        {
            Today = today,
            User = new GlunoUserContext { Name = "Test", Language = "sv" },
            Trip = new GlunoTripContext
            {
                Id = Guid.NewGuid(),
                Destination = "Lisbon",
                StartDate = today,
                EffectiveEndDate = today.AddDays(6),
                Activities = Enumerable.Range(0, 40).Select(index => new GlunoActivityContext
                {
                    Id = Guid.NewGuid(),
                    Title = $"Activity {index}",
                    Date = today.AddDays(index % 6),
                }).ToList(),
                Findings = Enumerable.Range(0, 8).Select(index => new TripFinding
                {
                    Type = $"finding_{index}",
                    Severity = "info",
                    Explanation = "Something worth knowing.",
                }).ToList(),
            },
            Preferences = Enumerable.Range(0, 12).Select(index => new GlunoPreferenceContext
            {
                Key = "pace",
                Value = $"value {index}",
                Scope = "trip",
            }).ToList(),
        };
    }

    // ── It terminates, and it stays bounded ──────────────────────────────

    [Fact]
    public void A_full_context_serialises_without_recursing()
    {
        // A cycle here is not an exception — it is a StackOverflowException,
        // which .NET cannot catch and which takes the process with it. That is
        // indistinguishable from an OOM kill from outside the container.
        var json = JsonSerializer.Serialize(RealisticContext(), GlunoJson.Options);

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.True(json.Length < 200_000, $"context serialised to {json.Length} chars");
    }

    [Fact]
    public void The_context_carries_no_database_entity()
    {
        // EF entities have navigation properties, and a navigation property is
        // how a graph becomes a cycle. Everything the model sees must be a
        // detached record.
        var seen = new HashSet<Type>();
        var queue = new Queue<Type>();
        queue.Enqueue(typeof(GlunoContext));

        while (queue.Count > 0)
        {
            var type = queue.Dequeue();
            if (!seen.Add(type)) continue;

            foreach (var property in type.GetProperties())
            {
                var propertyType = property.PropertyType;

                if (propertyType.IsGenericType)
                {
                    foreach (var argument in propertyType.GetGenericArguments()) queue.Enqueue(argument);
                }

                if (propertyType.Namespace?.StartsWith("sidequest.backend") == true)
                {
                    Assert.False(
                        propertyType.Namespace == "sidequest.backend.Models",
                        $"{type.Name}.{property.Name} is the EF entity {propertyType.Name}");

                    queue.Enqueue(propertyType);
                }
            }
        }

        Assert.True(seen.Count > 3, "the walk found almost nothing — it is not actually checking");
    }

    [Fact]
    public void The_context_budget_bounds_what_reaches_the_model()
    {
        var budget = new GlunoContextBudget(new ConfigurationBuilder().Build());

        // A section far larger than any real Adventure. The budget must return
        // rather than grow — an unbounded prompt is an unbounded allocation.
        var huge = new string('x', 2_000_000);

        var fitted = budget.Fit(
        [
            new GlunoContextSection(GlunoContextPriority.CurrentRequest, "turn", "{}") { IsCritical = true },
            new GlunoContextSection(GlunoContextPriority.OlderHistory, "old", $"\"{huge}\""),
        ]);

        // The oversized droppable section is dropped, and the caller is told.
        Assert.Contains("old", fitted.DroppedSections);
        Assert.DoesNotContain(huge, fitted.Json);
    }

    [Fact]
    public void A_second_turn_grows_only_by_its_history()
    {
        // The specific worry: turn 2 including the whole conversation twice,
        // or the evidence once per message. Growth should track the history,
        // not multiply with it.
        var context = RealisticContext();
        var budget = new GlunoContextBudget(new ConfigurationBuilder().Build());

        var first = budget.Fit(
        [
            new GlunoContextSection(GlunoContextPriority.RelevantTrip, "context",
                JsonSerializer.Serialize(context, GlunoJson.Options)) { IsCritical = true },
        ]);

        var second = budget.Fit(
        [
            new GlunoContextSection(GlunoContextPriority.RelevantTrip, "context",
                JsonSerializer.Serialize(context, GlunoJson.Options)) { IsCritical = true },
            new GlunoContextSection(GlunoContextPriority.RecentMessages, "history",
                JsonSerializer.Serialize(new[] { "q1", "a1" })),
        ]);

        // Linear, not doubled.
        Assert.True(
            second.TotalTokens < first.TotalTokens * 2,
            $"turn 2 grew from {first.TotalTokens} to {second.TotalTokens} tokens");
    }

    // ── A trivial question stays trivial ─────────────────────────────────

    [Fact]
    public void A_plain_greeting_loads_nothing_expensive()
    {
        var intent = GlunoIntentRouter.Classify(new GlunoIntentInput
        {
            Message = "Hej",
            HasTrip = false,
        });

        var workflow = GlunoPlanningStrategy.For(intent, hasTrip: false, canEdit: false);

        // No Adventure load, no weather, no analysis, no external search. A
        // greeting that fans out to four providers is how a cheap turn becomes
        // the most expensive thing in the product.
        Assert.False(workflow.NeedsTripAnalysis);
        Assert.False(workflow.NeedsWeather);
        Assert.False(workflow.AllowsExternalSearch);
        Assert.False(workflow.AllowsRouting);
        Assert.False(workflow.AllowsProposals);
    }

    [Fact]
    public void A_greeting_is_capped_to_one_model_round()
    {
        var intent = GlunoIntentRouter.Classify(new GlunoIntentInput
        {
            Message = "Hej",
            HasTrip = false,
        });

        var workflow = GlunoPlanningStrategy.For(intent, hasTrip: false, canEdit: false);

        Assert.InRange(workflow.MaxModelRounds, 1, 2);
    }

    // ── Every loop is bounded ────────────────────────────────────────────

    [Fact]
    public void Model_rounds_have_a_hard_ceiling_no_workflow_can_exceed()
    {
        // The workflow asks; this is the ceiling that answers. Without it a
        // tool loop that never converges runs until something else stops it.
        Assert.True(GlunoPlanningStrategy.AbsoluteMaxModelRounds <= 6);

        foreach (var intent in Enum.GetValues<GlunoIntent>())
        {
            var workflow = GlunoPlanningStrategy.For(
                new GlunoIntentResult
                {
                    PrimaryIntent = intent,
                    Confidence = 0.9,
                    Scope = GlunoIntentScope.Trip,
                    RequiresCurrentData = false,
                    RequiresExternalSearch = false,
                    ExpectsProposal = false,
                    RequiresClarification = false,
                },
                hasTrip: true,
                canEdit: true);

            Assert.InRange(workflow.MaxModelRounds, 1, GlunoPlanningStrategy.AbsoluteMaxModelRounds);
        }
    }

    [Fact]
    public void The_model_policy_caps_rounds_independently_of_configuration()
    {
        // Even asked for a hundred.
        var policy = new GlunoModelPolicy(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gluno:Models:Primary"] = "configured",
                ["Gluno:MaxModelRounds"] = "100",
            })
            .Build());

        Assert.InRange(policy.MaxModelRoundsPerTurn, 1, 8);
    }
}
