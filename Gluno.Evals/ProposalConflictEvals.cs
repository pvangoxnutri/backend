using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using sidequest.backend.Data;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for what happens when a suggestion clashes with the plan.
///
/// THE RULE THIS FILE DEFENDS. Never offer a fix that cannot work. A strategy
/// the validator already knows is impossible is worse than no strategy: the
/// user taps it, waits, and gets the same card back — which reads as the
/// product not listening rather than as a plan that genuinely does not fit.
///
/// And two things must never be offered as movable, however convenient it
/// would be. A booking with a reference number lives in somebody else's
/// system, and a check-in time is a contract with a hotel. Moving either from
/// a chat card would desynchronise the plan from reality.
///
/// Nothing here calls a model, a provider, or a database.
/// </summary>
public class ProposalConflictEvals
{
    private static GlunoProposalConflict Conflict(
        string type, bool existingMovable = false, bool uncertainHours = false, bool resolvable = true)
        => new()
        {
            ConflictType = type,
            ExistingIsMovable = existingMovable,
            HoursAreUncertain = uncertainHours,
            IsResolvable = resolvable,
            ConflictVersion = 1,
        };

    private static IModel Model()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=none").Options;

        using var db = new AppDbContext(options);
        return db.Model;
    }

    // ── Nothing impossible is ever offered ───────────────────────────────

    [Theory]
    [InlineData(GlunoConflictTypes.LockedBooking)]
    [InlineData(GlunoConflictTypes.CheckInConflict)]
    [InlineData(GlunoConflictTypes.CheckOutConflict)]
    public void A_fixed_booking_is_never_offered_as_movable(string type)
    {
        // Even when the caller claims it is. A reference number and a hotel
        // check-in are commitments outside SideQuest, and moving one from a
        // chat card would desynchronise the plan from a real reservation.
        var strategies = GlunoConflictStrategies.For(Conflict(type, existingMovable: true));

        Assert.DoesNotContain(GlunoConflictStrategies.MoveExisting, strategies);
        Assert.DoesNotContain(GlunoConflictStrategies.ReplaceExisting, strategies);
    }

    [Fact]
    public void Moving_the_existing_one_is_offered_only_when_it_can_move()
    {
        Assert.Contains(
            GlunoConflictStrategies.MoveExisting,
            GlunoConflictStrategies.For(Conflict(GlunoConflictTypes.TimeOverlap, existingMovable: true)));

        Assert.DoesNotContain(
            GlunoConflictStrategies.MoveExisting,
            GlunoConflictStrategies.For(Conflict(GlunoConflictTypes.TimeOverlap)));
    }

    [Fact]
    public void A_day_outside_the_Adventure_cannot_be_kept()
    {
        var strategies = GlunoConflictStrategies.For(Conflict(GlunoConflictTypes.OutsideTripDates));

        // Keeping it would mean planning a day the trip does not cover, and
        // the trip is never extended silently to make a suggestion fit.
        Assert.DoesNotContain(GlunoConflictStrategies.KeepBoth, strategies);
        Assert.Contains(GlunoConflictStrategies.ChooseAnotherDay, strategies);
    }

    [Fact]
    public void A_place_known_to_be_shut_cannot_be_planned_around()
    {
        var certain = GlunoConflictStrategies.For(Conflict(GlunoConflictTypes.OutsideOpeningHours));
        var uncertain = GlunoConflictStrategies.For(
            Conflict(GlunoConflictTypes.OutsideOpeningHours, uncertainHours: true));

        // Definitely shut and possibly shut are different conflicts. Only the
        // second can honestly be planned around.
        Assert.DoesNotContain(GlunoConflictStrategies.KeepBoth, certain);
        Assert.Contains(GlunoConflictStrategies.KeepBoth, uncertain);
    }

    [Fact]
    public void An_unresolvable_conflict_offers_only_backing_out()
    {
        var strategies = GlunoConflictStrategies.For(
            Conflict(GlunoConflictTypes.TimeOverlap, existingMovable: true, resolvable: false));

        Assert.Equal([GlunoConflictStrategies.Cancel], strategies);
    }

    [Fact]
    public void Cancel_is_always_available()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            Assert.Contains(GlunoConflictStrategies.Cancel, GlunoConflictStrategies.For(Conflict(type)));
        }
    }

    [Fact]
    public void Every_offered_strategy_is_on_the_closed_list()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            foreach (var strategy in GlunoConflictStrategies.For(Conflict(type, existingMovable: true)))
            {
                Assert.True(GlunoConflictStrategies.IsKnown(strategy));
            }
        }
    }

    [Fact]
    public void A_full_day_is_a_judgement_rather_than_an_impossibility()
    {
        // Too much in one day is somebody's call, not a validator's veto.
        Assert.Contains(
            GlunoConflictStrategies.KeepBoth,
            GlunoConflictStrategies.For(Conflict(GlunoConflictTypes.DayCapacityExceeded)));
    }

    [Fact]
    public void Shortening_is_offered_only_when_there_is_time_to_shorten()
    {
        var withRoom = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.InsufficientTravelTime,
            AvailableMinutes = 40,
            RequiredTravelMinutes = 55,
        };

        var withNone = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.InsufficientTravelTime,
            AvailableMinutes = 0,
        };

        Assert.Contains(GlunoConflictStrategies.Shorten, GlunoConflictStrategies.For(withRoom));
        Assert.DoesNotContain(GlunoConflictStrategies.Shorten, GlunoConflictStrategies.For(withNone));
    }

    // ── Ordering and single-answer shortcuts ─────────────────────────────

    [Fact]
    public void The_most_blocking_conflict_is_asked_about_first()
    {
        // Resolving the worst often removes the rest. Asking about a full day
        // while the date is also outside the trip is the wrong question.
        Assert.True(
            GlunoConflictTypes.Severity(GlunoConflictTypes.OutsideTripDates)
                < GlunoConflictTypes.Severity(GlunoConflictTypes.DayCapacityExceeded));

        Assert.True(
            GlunoConflictTypes.Severity(GlunoConflictTypes.TimeOverlap)
                < GlunoConflictTypes.Severity(GlunoConflictTypes.DuplicateActivity));
    }

    [Fact]
    public void A_conflict_with_one_real_option_is_applied_without_a_card()
    {
        // A card whose only choice is "skip it" is a notification with extra
        // steps.
        var only = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.TimeOverlap,
            IsResolvable = false,
        };

        Assert.Null(GlunoConflictMapper.OnlySafeStrategy(only));

        var several = Conflict(GlunoConflictTypes.TimeOverlap, existingMovable: true);
        Assert.Null(GlunoConflictMapper.OnlySafeStrategy(several));
    }

    [Fact]
    public void Options_never_exceed_what_fits_on_a_card()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            var options = GlunoConflictMapper.Options(
                Conflict(type, existingMovable: true, uncertainHours: true), "sv");

            Assert.True(options.Count <= GlunoClarificationBuilder.MaxOptions);
        }
    }

    // ── Mapping from the validator ───────────────────────────────────────

    [Fact]
    public void Only_blockers_a_user_can_choose_about_become_conflicts()
    {
        Assert.True(GlunoConflictMapper.IsAnswerable("time_overlap"));
        Assert.True(GlunoConflictMapper.IsAnswerable("activity_outside_trip_dates"));

        // Gluno's own mistakes. Asking somebody to pick a fix for a fabricated
        // travel time would be asking them to work around a bug.
        Assert.False(GlunoConflictMapper.IsAnswerable("fabricated_travel_time"));
        Assert.False(GlunoConflictMapper.IsAnswerable("claims_already_saved"));
        Assert.False(GlunoConflictMapper.IsAnswerable("unrequested_proposal"));
    }

    [Fact]
    public void A_validation_with_no_answerable_blocker_produces_no_conflict()
    {
        var validation = new GlunoQualityResult
        {
            Passed = false,
            Blockers = [new GlunoQualityIssue(GlunoQualitySeverity.Blocker, "fabricated_travel_time", "x")],
            Warnings = [],
            RequiresClarification = false,
        };

        Assert.Empty(GlunoConflictMapper.From(validation, conflictVersion: 1));
    }

    [Fact]
    public void The_mapper_orders_by_severity()
    {
        var validation = new GlunoQualityResult
        {
            Passed = false,
            Blockers =
            [
                new GlunoQualityIssue(GlunoQualitySeverity.Blocker, "too_many_stops_for_pace", "x"),
                new GlunoQualityIssue(GlunoQualitySeverity.Blocker, "activity_outside_trip_dates", "x"),
            ],
            Warnings = [],
            RequiresClarification = false,
        };

        var conflicts = GlunoConflictMapper.From(validation, conflictVersion: 1);

        Assert.Equal(GlunoConflictTypes.OutsideTripDates, conflicts[0].ConflictType);
    }

    [Fact]
    public void A_check_in_blocker_is_never_marked_movable_by_the_mapper()
    {
        var validation = new GlunoQualityResult
        {
            Passed = false,
            Blockers = [new GlunoQualityIssue(GlunoQualitySeverity.Blocker, "activity_before_checkin", "x")],
            Warnings = [],
            RequiresClarification = false,
        };

        // Even when the caller's predicate says it is.
        var conflict = Assert.Single(
            GlunoConflictMapper.From(validation, 1, existingIsMovable: _ => true));

        Assert.False(conflict.ExistingIsMovable);
    }

    // ── The draft ────────────────────────────────────────────────────────

    [Fact]
    public void A_new_draft_is_building_and_owns_nothing_yet()
    {
        var draft = new GlunoProposalDraft();

        Assert.Equal(GlunoProposalDraftStatuses.Building, draft.Status);
        Assert.Null(draft.ProposalId);
        Assert.True(draft.IsUsable);
        Assert.Equal(0, draft.RebuildCount);
    }

    [Fact]
    public void Only_an_open_draft_can_still_change()
    {
        foreach (var status in new[]
        {
            GlunoProposalDraftStatuses.Applied,
            GlunoProposalDraftStatuses.Cancelled,
            GlunoProposalDraftStatuses.Stale,
            GlunoProposalDraftStatuses.Failed,
        })
        {
            Assert.False(GlunoProposalDraftStatuses.IsOpen(status));
            Assert.False(new GlunoProposalDraft { Status = status }.IsUsable);
        }
    }

    [Fact]
    public void An_expired_draft_cannot_continue_even_while_open()
    {
        var draft = new GlunoProposalDraft
        {
            Status = GlunoProposalDraftStatuses.AwaitingClarification,
            ExpiresAt = DateTime.UtcNow.AddMinutes(-1),
        };

        Assert.False(draft.IsUsable);
    }

    // ── The loop guard ───────────────────────────────────────────────────

    [Fact]
    public void A_draft_runs_out_of_rebuilds()
    {
        var draft = new GlunoProposalDraft();

        for (var attempt = 0; attempt < GlunoProposalDraft.MaxRebuilds; attempt++)
        {
            Assert.False(draft.IsOutOfRebuilds);
            draft.RebuildCount++;
        }

        // The counter is what guarantees termination. Without it a conflict
        // each fix reintroduces would bounce between two cards forever.
        Assert.True(draft.IsOutOfRebuilds);
    }

    [Fact]
    public void The_same_fix_is_not_tried_twice_on_the_same_conflict()
    {
        var draft = new GlunoProposalDraft
        {
            LastConflictType = GlunoConflictTypes.TimeOverlap,
            LastStrategy = GlunoConflictStrategies.MoveNew,
        };

        // Repeating it against unchanged state cannot produce a different
        // outcome — caught before a rebuild is spent, not three rounds later.
        Assert.True(draft.WouldRepeat(GlunoConflictTypes.TimeOverlap, GlunoConflictStrategies.MoveNew));

        Assert.False(draft.WouldRepeat(GlunoConflictTypes.TimeOverlap, GlunoConflictStrategies.MoveExisting));
        Assert.False(draft.WouldRepeat(GlunoConflictTypes.OutsideTripDates, GlunoConflictStrategies.MoveNew));
    }

    [Fact]
    public void The_rebuild_ceiling_is_small_enough_to_be_survivable()
    {
        // Each rebuild costs a model round and a validation pass. Three is a
        // conversation; ten is somebody watching a spinner.
        Assert.InRange(GlunoProposalDraft.MaxRebuilds, 1, 3);
    }

    // ── Versions catch stale taps ────────────────────────────────────────

    [Fact]
    public void A_draft_carries_both_versions_and_they_move_independently()
    {
        var draft = new GlunoProposalDraft();

        Assert.Equal(1, draft.DraftVersion);
        Assert.Equal(1, draft.ConflictVersion);

        // Content changed, conflicts not yet recomputed.
        draft.DraftVersion++;
        Assert.NotEqual(draft.DraftVersion, draft.ConflictVersion);
    }

    [Fact]
    public void A_conflict_remembers_which_version_produced_it()
    {
        var conflict = Conflict(GlunoConflictTypes.TimeOverlap) with { ConflictVersion = 4 };

        // A tap carrying an older version is answering about a plan that no
        // longer exists.
        Assert.Equal(4, conflict.ConflictVersion);
    }

    // ── Storage ──────────────────────────────────────────────────────────

    [Fact]
    public void A_draft_dies_with_its_owner_its_conversation_and_its_Adventure()
    {
        var entity = Model().FindEntityType(typeof(GlunoProposalDraft))!;

        foreach (var principal in new[]
        {
            typeof(User), typeof(GlunoConversation), typeof(Trip),
        })
        {
            var fk = entity.GetForeignKeys()
                .FirstOrDefault(key => key.PrincipalEntityType.ClrType == principal);

            Assert.NotNull(fk);
            // Unlike a proposal, a draft is not history: it is a suggestion
            // that was never agreed, and keeping it after its trip is gone
            // leaves an unapplyable half-plan behind.
            Assert.Equal(DeleteBehavior.Cascade, fk!.DeleteBehavior);
        }
    }

    [Fact]
    public void A_draft_stores_a_payload_rather_than_an_entity_graph()
    {
        var properties = typeof(GlunoProposalDraft).GetProperties();

        // One navigation only, and it is the conversation. Anything else would
        // serialise EF navigations into the draft and rot the moment the plan
        // changed.
        var navigations = properties
            .Where(property => property.PropertyType.Namespace == "sidequest.backend.Models")
            .Select(property => property.Name)
            .ToList();

        Assert.Equal(["Conversation"], navigations);
        Assert.Equal(typeof(string), typeof(GlunoProposalDraft).GetProperty("PayloadJson")!.PropertyType);
    }

    [Fact]
    public void The_payload_round_trips_as_plain_json()
    {
        var draft = new GlunoProposalDraft
        {
            PayloadJson = JsonSerializer.Serialize(new { date = "2026-08-14", activities = Array.Empty<int>() }),
        };

        var parsed = JsonSerializer.Deserialize<JsonElement>(draft.PayloadJson);
        Assert.Equal("2026-08-14", parsed.GetProperty("date").GetString());
    }

    // ── Localisation ─────────────────────────────────────────────────────

    [Fact]
    public void Every_strategy_and_conflict_reads_in_both_languages()
    {
        foreach (var language in new[] { "sv", "en" })
        {
            foreach (var strategy in GlunoConflictStrategies.All)
            {
                Assert.False(string.IsNullOrWhiteSpace(
                    GlunoConflictStrategies.Label(strategy, language)));
            }

            foreach (var type in GlunoConflictTypes.All)
            {
                var explanation = GlunoConflictStrategies.Explain(type, language);

                Assert.False(string.IsNullOrWhiteSpace(explanation));
                // One sentence. The card shows the day and the items
                // separately, and a paragraph before a choice is the response
                // contract's whole complaint.
                Assert.True(explanation.Length < 80, $"{type} in {language}: {explanation.Length} chars");
            }
        }
    }

    [Fact]
    public void An_unknown_conflict_type_still_reads_as_something()
    {
        // A future type must not render an empty card or a raw code.
        Assert.False(string.IsNullOrWhiteSpace(GlunoConflictStrategies.Explain("something_new", "sv")));
        Assert.NotEmpty(GlunoConflictStrategies.For(Conflict("something_new")));
    }

    [Fact]
    public void No_explanation_leaks_a_time_a_title_or_a_place()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            var explanation = GlunoConflictStrategies.Explain(type, "en");

            // This string also reaches the log line.
            Assert.DoesNotContain(":", explanation);
            Assert.DoesNotMatch(@"\d{4}-\d{2}-\d{2}", explanation);
        }
    }
}
