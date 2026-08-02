using System.Reflection;
using System.Text.Json;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the draft lifecycle and the fixes that need no model.
///
/// THE ONE INVARIANT. Nothing in the draft flow writes to an Adventure. A draft
/// is a conversation about a change; the change happens later, once, behind an
/// explicit approval. Every method here either edits a JSON document or moves a
/// status, and a test below proves the service exposes nothing that could do
/// otherwise.
///
/// THE SECOND. A version moves whenever content moves. If a payload could be
/// edited without DraftVersion moving, a tap carrying the old one would be
/// undetectable — and would resolve a conflict against a plan that had already
/// changed.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ProposalDraftEvals
{
    private const string DayPlan = """
        {
          "date": "2026-08-14",
          "activities": [
            { "title": "Museum", "time": "10:00" },
            { "title": "Lunch", "time": "13:00" },
            { "title": "Dinner", "time": "19:00" }
          ]
        }
        """;

    // ── Deterministic strategies ─────────────────────────────────────────

    [Fact]
    public void Skipping_the_new_item_removes_exactly_that_row()
    {
        var updated = GlunoProposalDraftService.ApplyDeterministic(
            DayPlan, GlunoConflictStrategies.RemoveNew, [2]);

        Assert.NotNull(updated);

        var activities = JsonSerializer.Deserialize<JsonElement>(updated!)
            .GetProperty("activities");

        Assert.Equal(2, activities.GetArrayLength());
        Assert.DoesNotContain("Dinner", updated);
        // Everything else survives untouched.
        Assert.Contains("Museum", updated);
        Assert.Contains("2026-08-14", updated);
    }

    [Fact]
    public void Keeping_both_leaves_the_draft_exactly_as_it_was()
    {
        // The plan already works; the user has decided the clash is
        // acceptable. There is nothing to rebuild.
        var updated = GlunoProposalDraftService.ApplyDeterministic(
            DayPlan, GlunoConflictStrategies.KeepBoth, [1]);

        Assert.Equal(DayPlan, updated);
    }

    [Fact]
    public void A_strategy_needing_a_rebuild_returns_null_rather_than_silence()
    {
        // Null tells the caller a rebuild is REQUIRED. Returning the payload
        // unchanged would look like a successful no-op and leave the conflict
        // in place while the draft claimed to be fixed.
        foreach (var strategy in new[]
        {
            GlunoConflictStrategies.MoveNew,
            GlunoConflictStrategies.MoveExisting,
            GlunoConflictStrategies.Shorten,
            GlunoConflictStrategies.ChooseAnotherDay,
            GlunoConflictStrategies.ChooseAnotherTime,
        })
        {
            Assert.Null(GlunoProposalDraftService.ApplyDeterministic(DayPlan, strategy, [0]));
        }
    }

    [Fact]
    public void A_malformed_payload_does_not_throw()
    {
        // This runs on a live turn. A payload that cannot be parsed is a
        // reason to fall back, never to fail the request.
        Assert.Null(GlunoProposalDraftService.ApplyDeterministic(
            "not json", GlunoConflictStrategies.RemoveNew, [0]));

        Assert.Null(GlunoProposalDraftService.ApplyDeterministic(
            "{}", GlunoConflictStrategies.RemoveNew, [0]));
    }

    [Fact]
    public void Removing_every_row_is_not_treated_as_a_valid_plan()
    {
        var single = """{ "title": "Dinner", "time": "19:00" }""";

        // A single-activity proposal with its only row removed leaves nothing
        // to propose — the caller treats that as a cancel rather than writing
        // an empty day.
        Assert.Null(GlunoProposalDraftService.ApplyDeterministic(
            single, GlunoConflictStrategies.RemoveNew, [0]));
    }

    [Fact]
    public void Removing_a_row_does_not_shift_the_others_wrongly()
    {
        var updated = GlunoProposalDraftService.ApplyDeterministic(
            DayPlan, GlunoConflictStrategies.RemoveNew, [0]);

        var activities = JsonSerializer.Deserialize<JsonElement>(updated!)
            .GetProperty("activities");

        Assert.Equal("Lunch", activities[0].GetProperty("title").GetString());
        Assert.Equal("Dinner", activities[1].GetProperty("title").GetString());
    }

    // ── Nothing here writes ──────────────────────────────────────────────

    [Fact]
    public void The_draft_service_exposes_nothing_that_could_change_an_Adventure()
    {
        var names = typeof(IGlunoProposalDraftService).GetMethods()
            .Select(method => method.Name)
            .ToList();

        // Create, read, update payload, record conflicts, validate, record a
        // rebuild, set status. No apply, no commit, no write.
        foreach (var forbidden in new[] { "Apply", "Commit", "Write", "Save", "Execute" })
        {
            Assert.DoesNotContain(names, name => name.Contains(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void Every_draft_operation_is_cancellable()
    {
        foreach (var method in typeof(IGlunoProposalDraftService).GetMethods()
            .Where(method => typeof(Task).IsAssignableFrom(method.ReturnType)))
        {
            Assert.True(
                method.GetParameters().Any(parameter => parameter.ParameterType == typeof(CancellationToken)),
                $"{method.Name} cannot be cancelled");
        }
    }

    [Fact]
    public void Every_draft_operation_takes_the_owner()
    {
        // Never a lookup by id alone. An id is not a capability.
        foreach (var method in typeof(IGlunoProposalDraftService).GetMethods()
            .Where(method => method.Name != nameof(IGlunoProposalDraftService.CreateAsync)))
        {
            Assert.Contains(
                method.GetParameters(),
                parameter => parameter.Name == "userId" && parameter.ParameterType == typeof(Guid));
        }
    }

    // ── The turn cannot bypass the draft ─────────────────────────────────

    [Fact]
    public void The_conflict_branch_runs_before_any_proposal_is_created()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var conflictAt = source.IndexOf("AskAboutConflictAsync(", StringComparison.Ordinal);
        var createAt = source.IndexOf("_proposals.CreateAsync(", StringComparison.Ordinal);

        Assert.True(conflictAt > 0, "the second clarification point is missing");
        Assert.True(createAt > 0);
        // The branch returns before reaching proposal creation — a conflicting
        // suggestion never becomes something with an Apply button on it.
        Assert.True(
            conflictAt < createAt,
            "a proposal can be created before the conflict check runs");
    }

    [Fact]
    public void There_is_exactly_one_place_a_chat_turn_creates_a_proposal()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        // A second creation path would be a way around the draft flow.
        var occurrences = source.Split("_proposals.CreateAsync(").Length - 1;
        Assert.Equal(1, occurrences);
    }

    // ── The conflict card ────────────────────────────────────────────────

    [Fact]
    public void A_conflict_is_only_asked_about_when_there_is_a_real_choice()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        // More than one strategy — Cancel alone is a notification, not a
        // question, and the ordinary gate note already covers that case.
        Assert.Contains("conflict.AllowedStrategies.Count > 1", source);
    }

    [Fact]
    public void The_conflict_question_carries_no_free_text_escape()
    {
        // The strategies are the answers. Free text would invite somebody to
        // describe a fix the validator has no way to check.
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoChatService.cs"));

        var index = source.IndexOf("Type = GlunoClarificationTypes.ProposalConflict", StringComparison.Ordinal);
        Assert.True(index > 0);

        var block = source[index..Math.Min(source.Length, index + 600)];
        Assert.Contains("AllowFreeText = false", block);
    }

    // ── Loop protection, end to end ──────────────────────────────────────

    [Fact]
    public void A_draft_that_keeps_conflicting_terminates()
    {
        var draft = new GlunoProposalDraft
        {
            Status = GlunoProposalDraftStatuses.AwaitingClarification,
        };

        // The worst case: every rebuild produces the same conflict and the
        // user picks a different fix each time, so WouldRepeat never fires.
        var attempts = 0;

        while (!draft.IsOutOfRebuilds)
        {
            draft.RebuildCount++;
            attempts++;

            Assert.True(attempts <= GlunoProposalDraft.MaxRebuilds, "the loop did not terminate");
        }

        Assert.Equal(GlunoProposalDraft.MaxRebuilds, attempts);
    }

    [Fact]
    public void Repeating_one_fix_is_caught_before_a_rebuild_is_spent()
    {
        var draft = new GlunoProposalDraft
        {
            Status = GlunoProposalDraftStatuses.AwaitingClarification,
            LastConflictType = GlunoConflictTypes.TimeOverlap,
            LastStrategy = GlunoConflictStrategies.MoveNew,
            RebuildCount = 0,
        };

        // Not after three wasted model rounds — immediately, while the counter
        // is still zero.
        Assert.True(draft.WouldRepeat(GlunoConflictTypes.TimeOverlap, GlunoConflictStrategies.MoveNew));
        Assert.False(draft.IsOutOfRebuilds);
    }

    // ── Status transitions ───────────────────────────────────────────────

    [Fact]
    public void Applied_is_terminal()
    {
        // A draft that could return to awaiting_clarification after an apply
        // would offer to rebuild something already written to the Adventure.
        var draft = new GlunoProposalDraft { Status = GlunoProposalDraftStatuses.Applied };

        Assert.False(draft.IsUsable);
        Assert.False(GlunoProposalDraftStatuses.IsOpen(GlunoProposalDraftStatuses.Applied));
    }

    [Fact]
    public void Only_ready_for_approval_may_become_a_proposal()
    {
        // The status name is the contract. Anything else still has an
        // unanswered question attached to it.
        Assert.True(GlunoProposalDraftStatuses.IsOpen(GlunoProposalDraftStatuses.ReadyForApproval));
        Assert.False(GlunoProposalDraftStatuses.IsOpen(GlunoProposalDraftStatuses.Failed));
    }

    [Fact]
    public void Every_status_the_service_can_set_is_on_the_closed_list()
    {
        foreach (var status in GlunoProposalDraftStatuses.All)
        {
            Assert.True(GlunoProposalDraftStatuses.IsKnown(status));
        }

        Assert.False(GlunoProposalDraftStatuses.IsKnown("half_done"));
    }

    // ── Errors are codes ─────────────────────────────────────────────────

    [Fact]
    public void Every_draft_failure_is_a_typed_code_rather_than_a_message()
    {
        Assert.True(typeof(GlunoDraftError).IsEnum);

        foreach (var value in Enum.GetValues<GlunoDraftError>())
        {
            Assert.False(string.IsNullOrWhiteSpace(value.ToString()));
        }

        // The two that matter most for a tap arriving late.
        Assert.Contains(GlunoDraftError.Stale, Enum.GetValues<GlunoDraftError>());
        Assert.Contains(GlunoDraftError.OutOfRebuilds, Enum.GetValues<GlunoDraftError>());
    }
}
