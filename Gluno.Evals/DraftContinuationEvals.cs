using System.Text.Json;
using sidequest.backend.Dtos;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for answering a conflict card.
///
/// THE INVARIANT THIS FILE EXISTS FOR: a tap can change a draft and it can
/// produce a proposal, but it can never change an Adventure. Every path below
/// ends in one of three places — a new question, a proposal awaiting approval,
/// or a controlled stop — and none of them writes.
///
/// THE SECOND: an option that cannot be honoured is never shown. A card whose
/// buttons error on tap is worse than one that offers fewer choices, because it
/// reads as the product being broken rather than being honest about its limits.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class DraftContinuationEvals
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Gluno", file));

    private static string ControllerSource() => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Controllers", "GlunoController.cs"));

    private const string DayPlan = """
        {
          "date": "2026-08-14",
          "activities": [
            { "title": "Museum", "time": "10:00", "endTime": "12:00" },
            { "title": "Dinner", "time": "19:00", "endTime": "21:00", "isFixed": true },
            { "title": "Rooftop bar", "time": "19:30" }
          ]
        }
        """;

    private static JsonElement Plan(string json) => JsonDocument.Parse(json).RootElement.Clone();

    private static GlunoQualityResult Blocked(params GlunoQualityIssue[] blockers)
        => new()
        {
            Passed = false,
            Blockers = blockers,
            Warnings = Array.Empty<GlunoQualityIssue>(),
            RequiresClarification = false,
        };

    private static GlunoQualityIssue Blocker(string code, int? index = null)
        => new(GlunoQualitySeverity.Blocker, code, "…") { ActivityIndex = index };

    // ── 1. The resolve path ──────────────────────────────────────────────

    [Fact]
    public void A_proposal_conflict_resolve_takes_its_own_continuation()
    {
        var source = ControllerSource();

        // Routed on the DRAFT BINDING, at the endpoint. Sending a conflict
        // answer through the ordinary continuation would replay the original
        // question through the model and could return a different plan than
        // the one the user was looking at when they tapped.
        //
        // The binding rather than the type, because a conflict can produce a
        // day or a time chooser and those are ordinary `day` and
        // `activity_time` cards — but they answer about a draft.
        Assert.Contains("{ DraftId: not null } => await _chat.ContinueFromDraftAsync", source);
        Assert.Contains("ContinueFromDraftAsync(userId, clarification, option, ct)", source);
    }

    [Fact]
    public void The_conflict_continuation_never_calls_a_model()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("public async Task<GlunoTurnResult> ContinueFromDraftAsync", StringComparison.Ordinal);
        Assert.True(start > 0);

        var end = source.IndexOf("private async Task<GlunoTurnResult> ReadyForApprovalAsync", StringComparison.Ordinal);
        var body = source[start..end];

        // Deterministic end to end. A model round here would be free to produce
        // a different plan, so the user would have answered about one
        // suggestion and been handed another.
        Assert.DoesNotContain("_provider", body);
        Assert.DoesNotContain("SendCoreAsync", body);
    }

    [Fact]
    public void The_continuation_takes_no_version_from_the_caller()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("Task<GlunoTurnResult> ContinueFromDraftAsync(\n        Guid userId", StringComparison.Ordinal);

        if (start < 0)
        {
            start = source.IndexOf("public async Task<GlunoTurnResult> ContinueFromDraftAsync", StringComparison.Ordinal);
        }

        var signature = source[start..(start + 300)];

        // A user, a clarification, an option, a token. No draft id, no version,
        // no strategy string — every one of those is read from the row the
        // server wrote.
        Assert.DoesNotContain("int draftVersion", signature);
        Assert.DoesNotContain("Guid draftId", signature);
    }

    // ── 2. A manipulated answer ──────────────────────────────────────────

    [Fact]
    public void An_unknown_strategy_is_refused()
    {
        // The option's Value is the strategy. A row carrying something not on
        // the closed list resolves to nothing.
        Assert.False(GlunoConflictStrategies.IsKnown("move_everything"));
        Assert.False(GlunoConflictStrategies.IsKnown(""));
        Assert.False(GlunoConflictStrategies.IsKnown(null));
    }

    [Fact]
    public void A_strategy_the_server_cannot_perform_is_never_offered()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.TimeOverlap,
            ConflictVersion = 1,
        };

        var offered = GlunoConflictMapper.Options(conflict, "en")
            .Select(option => option.Value)
            .ToList();

        // The filter is the invariant, not the current contents of the list.
        // Every strategy is carryable today; the gate stays so that adding a
        // tenth cannot ship as a dead button.
        Assert.NotEmpty(offered);
        Assert.All(offered, strategy => Assert.True(GlunoConflictStrategies.IsSupported(strategy)));

        // The two that were always answerable, still are.
        Assert.Contains(GlunoConflictStrategies.RemoveNew, offered);
        Assert.Contains(GlunoConflictStrategies.Cancel, offered);
    }

    [Fact]
    public void Every_offered_option_can_actually_be_carried_out()
    {
        // Across every conflict type, not just the one above.
        foreach (var type in GlunoConflictTypes.All)
        {
            var conflict = new GlunoProposalConflict { ConflictType = type, ConflictVersion = 1 };

            foreach (var option in GlunoConflictMapper.Options(conflict, "sv"))
            {
                Assert.True(
                    GlunoConflictStrategies.IsSupported(option.Value),
                    $"{type} offers {option.Value}, which nothing can honour");
            }
        }
    }

    [Fact]
    public void Backing_out_is_offered_for_every_conflict()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            var conflict = new GlunoProposalConflict { ConflictType = type, ConflictVersion = 1 };
            var offered = GlunoConflictMapper.Options(conflict, "en").Select(option => option.Value);

            Assert.Contains(GlunoConflictStrategies.Cancel, offered);
        }
    }

    // ── 3. Stale state ───────────────────────────────────────────────────

    [Fact]
    public void Both_versions_are_checked_and_neither_alone_is_enough()
    {
        var source = Source("GlunoProposalDraftService.cs");

        // Separately, with an OR. A tap carrying an old DraftVersion is
        // answering about different content; an old ConflictVersion is
        // answering a question that has since been recomputed. Checking only
        // one would let the other through.
        Assert.Contains(
            "draft.DraftVersion != draftVersion || draft.ConflictVersion != conflictVersion",
            source);
    }

    [Fact]
    public void Content_cannot_change_without_the_version_moving()
    {
        var source = Source("GlunoProposalDraftService.cs");
        var start = source.IndexOf("public async Task<GlunoProposalDraft?> UpdatePayloadAsync", StringComparison.Ordinal);
        var body = source[start..(start + 700)];

        // Unconditional, right after the assignment. A conditional bump is a
        // path that edits a draft quietly, and a stale tap against a quietly
        // edited draft is undetectable.
        Assert.Contains("draft.PayloadJson = payloadJson;", body);
        Assert.Contains("draft.DraftVersion++;", body);
    }

    [Fact]
    public void Accepting_the_same_conflict_twice_does_not_move_the_version()
    {
        var draft = new GlunoProposalDraft();

        Assert.True(draft.Accept(GlunoConflictTypes.DayCapacityExceeded));
        // The second tap is the same acceptance. Bumping for it would make
        // every card built from the first one stale for no reason.
        Assert.False(draft.Accept(GlunoConflictTypes.DayCapacityExceeded));
    }

    [Fact]
    public void An_acceptance_is_scoped_to_its_conflict_type()
    {
        var draft = new GlunoProposalDraft();
        draft.Accept(GlunoConflictTypes.DayCapacityExceeded);

        Assert.True(draft.HasAccepted(GlunoConflictTypes.DayCapacityExceeded));
        // Accepting a full day says nothing about a clash with a booking. A
        // blanket suppression would swallow the next real problem.
        Assert.False(draft.HasAccepted(GlunoConflictTypes.LockedBooking));
        Assert.False(draft.HasAccepted(GlunoConflictTypes.TimeOverlap));
    }

    [Fact]
    public void Several_acceptances_survive_a_round_trip_through_storage()
    {
        var draft = new GlunoProposalDraft();
        draft.Accept(GlunoConflictTypes.DayCapacityExceeded);
        draft.Accept(GlunoConflictTypes.OutsideOpeningHours);

        // As it would come back from the database.
        var reloaded = new GlunoProposalDraft { AcceptedConflictsJson = draft.AcceptedConflictsJson };

        Assert.True(reloaded.HasAccepted(GlunoConflictTypes.DayCapacityExceeded));
        Assert.True(reloaded.HasAccepted(GlunoConflictTypes.OutsideOpeningHours));
        Assert.False(reloaded.HasAccepted(GlunoConflictTypes.TimeOverlap));
    }

    // ── 4. Removing the new item ─────────────────────────────────────────

    [Fact]
    public void Skipping_the_suggestion_only_edits_the_draft()
    {
        var updated = GlunoProposalDraftService.ApplyDeterministic(
            DayPlan, GlunoConflictStrategies.RemoveNew, [2]);

        Assert.NotNull(updated);
        Assert.DoesNotContain("Rooftop bar", updated);
        // The booking it clashed with is untouched. Nothing about this strategy
        // reaches the Adventure at all.
        Assert.Contains("Dinner", updated);
        Assert.Contains("Museum", updated);
    }

    [Fact]
    public void Skipping_the_last_row_is_a_controlled_stop()
    {
        var source = Source("GlunoChatService.cs");

        // The strategy produces no payload; the continuation cancels the draft
        // and says so. A null treated as "unchanged" would leave a draft
        // claiming to be fixed with the conflict still in it.
        Assert.Contains("if (outcome.PayloadJson == null && !outcome.AcceptedInPlace)", source);
        Assert.Contains("GlunoProposalDraftStatuses.Cancelled", source);
    }

    // ── 5. Keeping both ──────────────────────────────────────────────────

    [Fact]
    public void Keeping_both_is_only_offered_where_the_validator_allows_it()
    {
        // Hours that are genuinely unknown can be planned around with a caveat.
        var uncertain = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.OutsideOpeningHours,
            HoursAreUncertain = true,
            ConflictVersion = 1,
        };

        // Definitely shut cannot.
        var known = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.OutsideOpeningHours,
            HoursAreUncertain = false,
            ConflictVersion = 1,
        };

        Assert.Contains(GlunoConflictStrategies.KeepBoth, uncertain.AllowedStrategies);
        Assert.DoesNotContain(GlunoConflictStrategies.KeepBoth, known.AllowedStrategies);
    }

    [Fact]
    public void Keeping_both_stops_the_same_question_coming_back()
    {
        var source = Source("GlunoChatService.cs");

        // Without the filter the gate re-runs, finds the identical conflict,
        // and asks the question the user just answered. Forever.
        Assert.Contains("!draft.HasAccepted(item.ConflictType)", source);
    }

    [Fact]
    public void An_accepted_conflict_does_not_hide_a_new_one()
    {
        var draft = new GlunoProposalDraft();
        draft.Accept(GlunoConflictTypes.DayCapacityExceeded);

        var found = new[]
        {
            new GlunoProposalConflict { ConflictType = GlunoConflictTypes.DayCapacityExceeded, ConflictVersion = 2 },
            new GlunoProposalConflict { ConflictType = GlunoConflictTypes.LockedBooking, ConflictVersion = 2 },
        };

        var remaining = found.Where(item => !draft.HasAccepted(item.ConflictType)).ToList();

        Assert.Single(remaining);
        Assert.Equal(GlunoConflictTypes.LockedBooking, remaining[0].ConflictType);
    }

    // ── 6. Cancelling ────────────────────────────────────────────────────

    [Fact]
    public void Cancelling_stops_before_any_revalidation()
    {
        var source = Source("GlunoChatService.cs");

        var cancelAt = source.IndexOf(
            "if (strategy == GlunoConflictStrategies.Cancel)", StringComparison.Ordinal);
        var validateAt = source.IndexOf("_qualityGate.Check(new GlunoQualityInput", StringComparison.Ordinal);

        Assert.True(cancelAt > 0);
        // The user said no. Running the gate, building context and creating a
        // proposal afterwards would all be work nobody asked for.
        Assert.True(cancelAt < validateAt);
    }

    [Fact]
    public void A_cancelled_draft_can_never_become_a_proposal()
    {
        var draft = new GlunoProposalDraft { Status = GlunoProposalDraftStatuses.Cancelled };

        Assert.False(draft.IsUsable);
        Assert.False(GlunoProposalDraftStatuses.IsOpen(GlunoProposalDraftStatuses.Cancelled));
    }

    // ── 7. Ready for approval ────────────────────────────────────────────

    [Fact]
    public void A_conflict_free_draft_becomes_ready_for_approval()
    {
        var source = Source("GlunoChatService.cs");

        // The status moves BEFORE the proposal is created, so a failure between
        // the two leaves a draft that cannot produce a second proposal on a
        // retry.
        var statusAt = source.IndexOf(
            "GlunoProposalDraftStatuses.ReadyForApproval, null, ct) ?? draft", StringComparison.Ordinal);
        var createAt = source.IndexOf("await CreateProposalsAsync(\n            conversation, assistantMessage.Id, [proposal]", StringComparison.Ordinal);

        Assert.True(statusAt > 0);
        if (createAt > 0) Assert.True(statusAt < createAt);
    }

    [Fact]
    public void A_proposal_from_a_draft_carries_the_draft_and_its_version()
    {
        var source = Source("GlunoChatService.cs");

        Assert.Contains("draftId: ready.Id, draftVersion: ready.DraftVersion", source);
    }

    [Fact]
    public void There_is_still_exactly_one_place_a_proposal_is_created()
    {
        var source = Source("GlunoChatService.cs");

        // Both the ordinary turn and the continuation go through the same
        // helper. A second call to the store would be a way to produce
        // something with an Apply button that never went past the draft flow.
        Assert.Equal(1, source.Split("_proposals.CreateAsync(").Length - 1);
    }

    [Fact]
    public void The_proposal_record_can_carry_a_draft_reference()
    {
        var draftId = Guid.NewGuid();

        var record = new GlunoProposalRecord { DraftId = draftId, DraftVersion = 4 };

        Assert.Equal(draftId, record.DraftId);
        Assert.Equal(4, record.DraftVersion);

        // Null is the legacy shape, not a default that quietly skips the check.
        Assert.Null(new GlunoProposalRecord().DraftId);
    }

    // ── 8. Apply protection ──────────────────────────────────────────────

    [Fact]
    public void Apply_refuses_a_proposal_whose_draft_moved_on()
    {
        var source = Source("GlunoProposalApplyService.cs");

        Assert.Contains("if (draft.DraftVersion != proposal.DraftVersion)", source);
        Assert.Contains("draft_changed", source);
    }

    [Fact]
    public void Apply_refuses_a_draft_that_is_not_ready()
    {
        var source = Source("GlunoProposalApplyService.cs");

        // Cancelled, failed, stale, already applied — none of them may produce
        // a write, and only one status may.
        Assert.Contains(
            "draft.Status != GlunoProposalDraftStatuses.ReadyForApproval",
            source);
        Assert.Contains("draft_not_ready", source);
    }

    [Fact]
    public void The_draft_check_runs_before_the_write_is_claimed()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var draftCheckAt = source.IndexOf("if (proposal.DraftId is { } draftId)", StringComparison.Ordinal);
        // THE claim itself — the conditional UPDATE that moves the row into
        // `applying`. Not the earlier read-only status comparisons, which are
        // refusals rather than the write boundary.
        var claimAt = source.IndexOf(
            "SetProperty(p => p.Status, GlunoProposalStatuses.Applying)", StringComparison.Ordinal);

        Assert.True(draftCheckAt > 0);
        Assert.True(claimAt > 0);
        // Refusing before the claim means there is no transaction to unwind and
        // no half-applied plan — the write never starts.
        Assert.True(draftCheckAt < claimAt);
    }

    [Fact]
    public void A_refused_apply_writes_nothing()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var start = source.IndexOf("private async Task<GlunoApplyResult> MarkStaleAsync", StringComparison.Ordinal);
        var body = source[start..(start + 900)];

        Assert.True(start > 0);
        // A status and a code. No transaction, no dispatch, nothing that
        // touches TripActivities.
        Assert.DoesNotContain("ApplyCoreAsync", body);
        Assert.DoesNotContain("BeginTransaction", body);
    }

    [Fact]
    public void A_successful_apply_makes_the_draft_terminal()
    {
        var source = Source("GlunoProposalApplyService.cs");

        Assert.Contains("GlunoProposalDraftStatuses.Applied", source);
        // Terminal by contract, so a later continuation cannot offer to rebuild
        // something already written to the plan.
        Assert.False(GlunoProposalDraftStatuses.IsOpen(GlunoProposalDraftStatuses.Applied));
    }

    [Fact]
    public void A_second_apply_returns_the_first_result_rather_than_writing_again()
    {
        var source = Source("GlunoProposalApplyService.cs");

        // The existing replay path, unchanged by the draft check — which sits
        // after it, so a repeat apply never reaches the new refusals either.
        var replayAt = source.IndexOf("GlunoApplyError.AlreadyApplied", StringComparison.Ordinal);
        var draftCheckAt = source.IndexOf("if (proposal.DraftId is { } draftId)", StringComparison.Ordinal);

        Assert.True(replayAt > 0 && replayAt < draftCheckAt);
    }

    [Fact]
    public void A_legacy_proposal_without_a_draft_still_applies()
    {
        var source = Source("GlunoProposalApplyService.cs");

        // Guarded by `is { }`, so a null skips the block rather than failing.
        // Proposals that predate this flow are protected by the snapshot check
        // exactly as they always were.
        Assert.Contains("if (proposal.DraftId is { } draftId)", source);
    }

    // ── 9. Locked bookings ───────────────────────────────────────────────

    [Fact]
    public void A_clash_with_a_fixed_booking_is_a_locked_booking_conflict()
    {
        // Row 2 collides with row 1, which the gate marked isFixed.
        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 2)), conflictVersion: 1, dayPlan: Plan(DayPlan));

        Assert.Single(conflicts);
        Assert.Equal(GlunoConflictTypes.LockedBooking, conflicts[0].ConflictType);
    }

    [Fact]
    public void A_clash_between_two_suggestions_stays_an_ordinary_overlap()
    {
        var plan = """
            {
              "date": "2026-08-14",
              "activities": [
                { "title": "Museum", "time": "10:00", "endTime": "12:00" },
                { "title": "Gallery", "time": "11:00" }
              ]
            }
            """;

        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 1)), conflictVersion: 1, dayPlan: Plan(plan));

        // Neither is fixed and neither is already in the Adventure, so nothing
        // here belongs to anybody else's system.
        Assert.Equal(GlunoConflictTypes.TimeOverlap, conflicts[0].ConflictType);
    }

    [Fact]
    public void An_existing_activity_counts_as_locked_too()
    {
        var plan = $$"""
            {
              "date": "2026-08-14",
              "activities": [
                { "title": "Booked tour", "time": "10:00", "endTime": "12:00",
                  "existingActivityId": "{{Guid.NewGuid()}}" },
                { "title": "Museum", "time": "11:00" }
              ]
            }
            """;

        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 1)), conflictVersion: 1, dayPlan: Plan(plan));

        // Something already in the plan is not a suggestion's to rearrange.
        Assert.Equal(GlunoConflictTypes.LockedBooking, conflicts[0].ConflictType);
    }

    [Fact]
    public void A_locked_booking_is_never_offered_up_for_moving_or_removal()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.LockedBooking,
            // Even if something upstream got this wrong.
            ExistingIsMovable = true,
            ConflictVersion = 1,
        };

        var allowed = conflict.AllowedStrategies;

        Assert.DoesNotContain(GlunoConflictStrategies.MoveExisting, allowed);
        Assert.DoesNotContain(GlunoConflictStrategies.ReplaceExisting, allowed);
        Assert.DoesNotContain(GlunoConflictStrategies.KeepBoth, allowed);
    }

    [Fact]
    public void Check_in_and_check_out_are_protected_the_same_way()
    {
        foreach (var type in new[]
        {
            GlunoConflictTypes.CheckInConflict,
            GlunoConflictTypes.CheckOutConflict,
        })
        {
            var conflict = new GlunoProposalConflict
            {
                ConflictType = type,
                ExistingIsMovable = true,
                ConflictVersion = 1,
            };

            Assert.DoesNotContain(GlunoConflictStrategies.MoveExisting, conflict.AllowedStrategies);
            Assert.DoesNotContain(GlunoConflictStrategies.ReplaceExisting, conflict.AllowedStrategies);
        }
    }

    [Fact]
    public void A_locked_neighbour_makes_the_existing_item_immovable()
    {
        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 2)),
            conflictVersion: 1,
            // Even when the caller's own callback says otherwise.
            existingIsMovable: _ => true,
            dayPlan: Plan(DayPlan));

        Assert.False(conflicts[0].ExistingIsMovable);
    }

    [Fact]
    public void The_lock_is_read_from_the_gates_own_markers()
    {
        var source = Source("GlunoConflictMapper.cs");

        // isFixed and existingActivityId are what the quality gate already
        // uses. A parallel lock concept would give two answers to one question
        // and they would drift.
        Assert.Contains("\"isFixed\"", source);
        Assert.Contains("existingActivityId", source);
    }

    // ── 10. What the card shows ──────────────────────────────────────────

    [Fact]
    public void A_conflict_carries_the_day_and_the_time_it_is_about()
    {
        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 2)), conflictVersion: 1, dayPlan: Plan(DayPlan));

        Assert.Equal("2026-08-14", conflicts[0].Date);
        Assert.Equal("19:30", conflicts[0].StartTime);
    }

    [Fact]
    public void A_conflict_names_the_existing_activity_it_clashes_with()
    {
        var activityId = Guid.NewGuid();

        var plan = $$"""
            {
              "date": "2026-08-14",
              "activities": [
                { "title": "Dinner", "time": "19:00", "existingActivityId": "{{activityId}}" },
                { "title": "Bar", "time": "19:30" }
              ]
            }
            """;

        var conflicts = GlunoConflictMapper.From(
            Blocked(Blocker("time_overlap", 1)), conflictVersion: 1, dayPlan: Plan(plan));

        // So the card can say which booking rather than "something".
        Assert.Contains(activityId, conflicts[0].AffectedExistingActivityIds);
    }

    [Fact]
    public void The_card_payload_carries_no_ids_and_no_versions()
    {
        var properties = typeof(GlunoConflictDto).GetProperties().Select(property => property.Name).ToList();

        // The one part of the clarification that reaches the client. A version
        // the app can see is a version the app can send back, and the moment
        // one is trusted the staleness check is decorative.
        Assert.DoesNotContain(properties, name => name.Contains("Id", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, name => name.Contains("Version", StringComparison.Ordinal));
        Assert.DoesNotContain(properties, name => name.Contains("Draft", StringComparison.Ordinal));
    }

    [Fact]
    public void An_unreadable_card_payload_does_not_fail_the_turn()
    {
        var source = ControllerSource();
        var start = source.IndexOf("private static GlunoConflictDto? ReadConflictMeta", StringComparison.Ordinal);
        var body = source[start..(start + 500)];

        Assert.True(start > 0);
        // A card that renders without its subtitle beats a turn that 500s over
        // one.
        Assert.Contains("catch", body);
        Assert.Contains("return null", body);
    }

    [Fact]
    public void An_unknown_conflict_type_still_produces_a_usable_card()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = "something_this_build_has_never_seen",
            ConflictVersion = 1,
        };

        var options = GlunoConflictMapper.Options(conflict, "sv");

        // The default branch: skip it, or back out. Both are honest, and both
        // are answerable.
        Assert.NotEmpty(options);
        Assert.Contains(options, option => option.Value == GlunoConflictStrategies.Cancel);
    }

    [Fact]
    public void Every_strategy_label_exists_in_both_languages()
    {
        foreach (var strategy in GlunoConflictStrategies.All)
        {
            var swedish = GlunoConflictStrategies.Label(strategy, "sv");
            var english = GlunoConflictStrategies.Label(strategy, "en");

            Assert.False(string.IsNullOrWhiteSpace(swedish));
            Assert.False(string.IsNullOrWhiteSpace(english));
            // A missing translation shows as the other language, which is the
            // failure this catches.
            Assert.NotEqual(swedish, english);
        }
    }

    [Fact]
    public void Every_conflict_explanation_exists_in_both_languages()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            var swedish = GlunoConflictStrategies.Explain(type, "sv");
            var english = GlunoConflictStrategies.Explain(type, "en");

            Assert.False(string.IsNullOrWhiteSpace(swedish));
            Assert.False(string.IsNullOrWhiteSpace(english));
            Assert.NotEqual(swedish, english);
        }
    }

    // ── 11. Loop protection, end to end ──────────────────────────────────

    [Fact]
    public void Several_conflicts_are_asked_about_one_at_a_time()
    {
        var conflicts = GlunoConflictMapper.From(
            Blocked(
                Blocker("too_many_stops_for_pace", 2),
                Blocker("activity_outside_trip_dates", 0)),
            conflictVersion: 1,
            dayPlan: Plan(DayPlan));

        var first = GlunoConflictMapper.MostBlocking(conflicts);

        Assert.Equal(2, conflicts.Count);
        // The worst first: resolving it often removes the rest, and asking
        // about a full day while the day is also outside the trip is asking
        // the wrong question.
        Assert.Equal(GlunoConflictTypes.OutsideTripDates, first!.ConflictType);
    }

    [Fact]
    public void A_rebuild_is_counted_whatever_the_strategy_was()
    {
        var source = Source("GlunoChatService.cs");

        // Including the ones that changed nothing. The limit exists to stop the
        // case where each fix reintroduces the last, and an uncounted attempt
        // is a free lap round that loop.
        var recordAt = source.IndexOf("_drafts.RecordRebuildAsync(draftId, userId, conflictType, strategy, ct)", StringComparison.Ordinal);
        var gateAt = source.IndexOf("_qualityGate.Check(new GlunoQualityInput", StringComparison.Ordinal);

        Assert.True(recordAt > 0);
        Assert.True(recordAt < gateAt);
    }

    [Fact]
    public void Running_out_of_rebuilds_stops_rather_than_asking_again()
    {
        var source = Source("GlunoChatService.cs");

        Assert.Contains("if (draft.IsOutOfRebuilds)", source);
        Assert.Contains("GlunoDraftError.OutOfRebuilds", source);
    }

    [Fact]
    public void A_stopped_draft_produces_no_proposal()
    {
        var source = Source("GlunoChatService.cs");

        var start = source.IndexOf("private async Task<GlunoTurnResult> ConflictStoppedAsync", StringComparison.Ordinal);
        var body = source[start..(start + 1800)];

        Assert.True(start > 0);
        Assert.DoesNotContain("CreateProposalsAsync", body);
    }

    [Fact]
    public void The_stop_message_says_nothing_technical()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("private async Task<GlunoTurnResult> ConflictStoppedAsync", StringComparison.Ordinal);
        var body = source[start..(start + 1800)];

        // No version numbers, no internal error names, no blame. A technical
        // explanation would not help somebody decide what to do next.
        Assert.DoesNotContain("DraftVersion", body);
        Assert.DoesNotContain("ConflictVersion", body);
        Assert.Contains("Jag lyckades inte få ihop planen automatiskt", body);
        Assert.Contains("I couldn't make the plan work automatically", body);
    }

    // ── 12. The question is asked once ───────────────────────────────────

    [Fact]
    public void A_follow_up_conflict_reuses_the_original_question()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("private async Task<GlunoTurnResult> AskNextConflictAsync", StringComparison.Ordinal);
        var body = source[start..(start + 2000)];

        Assert.True(start > 0);
        // Referenced, not appended. Two conflicts on one suggestion must not
        // put what the user typed into the history twice.
        Assert.Contains("OriginalUserMessageId = previous.OriginalUserMessageId", body);
        Assert.DoesNotContain("GlunoMessageRoles.User", body);
    }

    [Fact]
    public void Every_continuation_outcome_is_replayable()
    {
        var source = Source("GlunoChatService.cs");

        // Each terminal path binds its message to the clarification, so a
        // second tap returns the first answer instead of running the work
        // again. Three paths, three bindings.
        Assert.True(
            source.Split("RecordContinuationAsync(").Length - 1 >= 4,
            "a continuation path does not record its answer");
    }

    [Fact]
    public void Indexes_are_recomputed_rather_than_remembered()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("private static IReadOnlyList<int> ConflictIndexesFor", StringComparison.Ordinal);

        Assert.True(start > 0);
        // They are positions in an array, and an array that has had a row
        // removed since would make a remembered index point at the wrong
        // activity.
        Assert.Contains("JsonDocument.Parse(draft.PayloadJson)", source[start..(start + 800)]);
    }

    // ── 13. Nothing here writes ──────────────────────────────────────────

    [Fact]
    public void The_continuation_never_touches_an_activity()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("public async Task<GlunoTurnResult> ContinueFromDraftAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static IReadOnlyList<int> ConflictIndexesFor", StringComparison.Ordinal);
        var body = source[start..end];

        // The one rule the whole flow rests on. A draft is a conversation about
        // a change; the change happens later, once, behind an approval.
        Assert.DoesNotContain("_db.TripActivities.Add", body);
        Assert.DoesNotContain("_db.TripActivities.Remove", body);
        Assert.DoesNotContain("_db.TripDayLocations", body);
    }

    [Fact]
    public void The_only_database_read_on_the_continuation_is_a_membership_check()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("public async Task<GlunoTurnResult> ContinueFromDraftAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private sealed record GlunoStrategyOutcome", StringComparison.Ordinal);
        var body = source[start..end];

        // At TAP time, not at ask time. An hour is long enough to leave a
        // group, and a stale button must not become an access path.
        Assert.Contains("_db.TripMembers.AnyAsync", body);
    }

    [Fact]
    public void The_ordinary_clarification_path_is_untouched()
    {
        var source = ControllerSource();

        // Adventure, day, activity, place, transport, pace, budget, scope —
        // all still replay the original turn through the model.
        Assert.Contains("_chat.ContinueFromClarificationAsync(", source);
    }
}
