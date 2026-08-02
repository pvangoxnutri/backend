using System.Text.Json;
using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the strategies that fix a plan rather than abandon it.
///
/// THE RULE THIS FILE ENFORCES: an option that is shown can be carried out. A
/// day offered is a day the activity fits on; a time offered is a time the
/// schedule engine would accept. The alternative — offer everything, validate
/// on tap — turns a choice into a guessing game with a round trip per guess,
/// and a card that errors reads as the product being broken.
///
/// THE SECOND: nothing here writes. Moving or replacing something the user
/// already has is recorded as an intention on the draft and carried out inside
/// the apply transaction, against the live row, behind the button.
///
/// Nothing calls a model, a provider, or a database.
/// </summary>
public class DraftStrategyEvals
{
    private static string Source(string file) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Gluno", file));

    private static JsonElement Plan(string json) => JsonDocument.Parse(json).RootElement.Clone();

    /// Museum 10–12, a booked dinner 19–21, and a bar suggested on top of it.
    private const string DayPlan = """
        {
          "date": "2026-08-14",
          "activities": [
            { "title": "Museum", "time": "10:00", "endTime": "12:00", "durationMinutes": 120 },
            { "title": "Dinner", "time": "19:00", "endTime": "21:00", "isFixed": true },
            { "title": "Rooftop bar", "time": "19:30", "endTime": "21:00", "durationMinutes": 90 }
          ]
        }
        """;

    private static GlunoTripContext Trip(
        DateOnly start, DateOnly end, params GlunoActivityContext[] activities)
        => new()
        {
            Id = Guid.NewGuid(),
            Title = "Spain",
            StartDate = start,
            EndDate = end,
            EffectiveEndDate = end,
            Activities = activities,
        };

    // ── Reading the plan ─────────────────────────────────────────────────

    [Fact]
    public void The_row_a_strategy_acts_on_is_the_one_Gluno_suggested()
    {
        var rows = GlunoDraftPlan.Rows(Plan(DayPlan));
        var target = GlunoDraftPlan.NewestSuggestion(rows);

        // Not the booking, and not the museum that came before it. A locked row
        // is by definition not what this suggestion added.
        Assert.Equal("Rooftop bar", target!.Title);
        Assert.False(target.IsLocked);
    }

    [Fact]
    public void A_booking_and_an_existing_activity_are_both_locked()
    {
        var plan = $$"""
            {
              "date": "2026-08-14",
              "activities": [
                { "title": "Train", "time": "08:00", "isFixed": true },
                { "title": "Tour", "time": "10:00", "existingActivityId": "{{Guid.NewGuid()}}" },
                { "title": "Museum", "time": "14:00" }
              ]
            }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        Assert.True(rows[0].IsLocked);
        Assert.True(rows[1].IsLocked);
        Assert.False(rows[2].IsLocked);
    }

    [Fact]
    public void An_unknown_length_is_never_treated_as_zero()
    {
        var plan = """{ "date": "2026-08-14", "activities": [ { "title": "Museum" } ] }""";
        var rows = GlunoDraftPlan.Rows(Plan(plan));

        // A row assumed instantaneous is a row the scheduler will happily stack
        // something else on top of.
        Assert.Null(rows[0].DurationMinutes);
        Assert.Equal(GlunoDraftPlan.DefaultDurationMinutes, rows[0].EffectiveDuration);
    }

    [Fact]
    public void A_malformed_plan_produces_no_rows_rather_than_throwing()
    {
        Assert.Empty(GlunoDraftPlan.Rows(Plan("{}")));
        Assert.Empty(GlunoDraftPlan.Rows(Plan("""{ "activities": "nope" }""")));
        Assert.Null(GlunoDraftPlan.DateOf(Plan("""{ "date": "not a date" }""")));
    }

    // ── choose_another_day ───────────────────────────────────────────────

    [Fact]
    public void Only_days_inside_the_trip_are_offered()
    {
        var trip = Trip(new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 16));
        var row = GlunoDraftPlan.Rows(Plan(DayPlan))[2];

        var days = GlunoDraftPlan.AvailableDays(trip, row, new DateOnly(2026, 8, 14), capacityPerDay: 5);

        Assert.All(days, day => Assert.InRange(day, trip.StartDate, trip.EndDate!.Value));
    }

    [Fact]
    public void The_day_the_clash_is_on_is_not_offered_back()
    {
        var trip = Trip(new DateOnly(2026, 8, 12), new DateOnly(2026, 8, 16));
        var row = GlunoDraftPlan.Rows(Plan(DayPlan))[2];

        var days = GlunoDraftPlan.AvailableDays(trip, row, new DateOnly(2026, 8, 14), capacityPerDay: 5);

        // Offering it would be offering the option of changing nothing.
        Assert.DoesNotContain(new DateOnly(2026, 8, 14), days);
        Assert.Equal(4, days.Count);
    }

    [Fact]
    public void A_day_already_at_capacity_is_not_offered()
    {
        var full = new DateOnly(2026, 8, 15);

        var trip = Trip(
            new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 16),
            new GlunoActivityContext { Date = full, Title = "One" },
            new GlunoActivityContext { Date = full, Title = "Two" },
            new GlunoActivityContext { Date = full, Title = "Three" });

        var row = GlunoDraftPlan.Rows(Plan(DayPlan))[2];
        var days = GlunoDraftPlan.AvailableDays(trip, row, null, capacityPerDay: 3);

        // One more would produce the capacity conflict this choice escapes.
        Assert.DoesNotContain(full, days);
    }

    [Fact]
    public void A_day_whose_same_hour_is_already_booked_is_not_offered()
    {
        var busy = new DateOnly(2026, 8, 15);

        var trip = Trip(
            new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 16),
            new GlunoActivityContext { Date = busy, Title = "Dinner", Time = "19:00", EndTime = "21:00" });

        var row = GlunoDraftPlan.Rows(Plan(DayPlan))[2];
        var days = GlunoDraftPlan.AvailableDays(trip, row, null, capacityPerDay: 5);

        // 19:30 on that day is the same clash, one day over.
        Assert.DoesNotContain(busy, days);
        Assert.Contains(new DateOnly(2026, 8, 16), days);
    }

    [Fact]
    public void An_activity_with_no_time_does_not_rule_out_a_day()
    {
        var day = new DateOnly(2026, 8, 15);

        var trip = Trip(
            new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 16),
            new GlunoActivityContext { Date = day, Title = "Something, sometime" });

        var row = GlunoDraftPlan.Rows(Plan(DayPlan))[2];
        var days = GlunoDraftPlan.AvailableDays(trip, row, null, capacityPerDay: 5);

        // An activity with no time is not a statement about when it happens.
        Assert.Contains(day, days);
    }

    [Fact]
    public void Choosing_a_day_moves_the_plan_and_keeps_its_times()
    {
        var moved = GlunoDraftPlan.WithDate(DayPlan, new DateOnly(2026, 8, 16));

        Assert.NotNull(moved);
        Assert.Equal(new DateOnly(2026, 8, 16), GlunoDraftPlan.DateOf(Plan(moved!)));
        // The day was chosen BECAUSE those times were free on it. Re-laying
        // them out would discard the thing that made it offerable.
        Assert.Contains("\"19:30\"", moved);
        Assert.Contains("Museum", moved);
    }

    // ── choose_another_time ──────────────────────────────────────────────

    [Fact]
    public void Offered_times_do_not_collide_with_anything_already_there()
    {
        var rows = GlunoDraftPlan.Rows(Plan(DayPlan));
        var target = rows[2];

        var times = GlunoDraftPlan.AvailableTimes(
            rows, target, new TimeOnly(8, 0), new TimeOnly(22, 0));

        Assert.NotEmpty(times);

        foreach (var time in times)
        {
            var end = time.AddMinutes(target.EffectiveDuration);

            // Clear of the museum (10–12) and clear of the dinner (19–21).
            Assert.False(time < new TimeOnly(12, 0) && end > new TimeOnly(10, 0));
            Assert.False(time < new TimeOnly(21, 0) && end > new TimeOnly(19, 0));
        }
    }

    [Fact]
    public void At_most_five_times_are_offered()
    {
        var plan = """
            { "date": "2026-08-14",
              "activities": [ { "title": "Museum", "durationMinutes": 30 } ] }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        var times = GlunoDraftPlan.AvailableTimes(
            rows, rows[0], new TimeOnly(8, 0), new TimeOnly(22, 0));

        // A whole empty day would otherwise produce twenty-eight rows. A list
        // somebody scrolls is a list they stop reading.
        Assert.Equal(GlunoDraftPlan.MaxTimeOptions, times.Count);
    }

    [Fact]
    public void Known_opening_hours_narrow_the_window()
    {
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Museum", "durationMinutes": 60,
                  "openingHours": { "opensAt": "14:00", "closesAt": "17:00" } }
              ] }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        var times = GlunoDraftPlan.AvailableTimes(
            rows, rows[0], new TimeOnly(8, 0), new TimeOnly(22, 0));

        Assert.NotEmpty(times);
        Assert.All(times, time => Assert.InRange(time, new TimeOnly(14, 0), new TimeOnly(16, 0)));
    }

    [Fact]
    public void Unknown_opening_hours_do_not_hide_the_day()
    {
        var plan = """
            { "date": "2026-08-14", "activities": [ { "title": "Museum", "durationMinutes": 60 } ] }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        var times = GlunoDraftPlan.AvailableTimes(
            rows, rows[0], new TimeOnly(8, 0), new TimeOnly(22, 0));

        // An unknown-hours place is a caveat on the answer, never a reason to
        // remove half the choices.
        Assert.Equal(new TimeOnly(8, 0), times[0]);
    }

    [Fact]
    public void Travel_time_is_kept_clear_either_side()
    {
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Museum", "time": "10:00", "endTime": "12:00" },
                { "title": "Gallery", "durationMinutes": 60,
                  "travelFromPrevious": { "minutes": 45 } }
              ] }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        var times = GlunoDraftPlan.AvailableTimes(
            rows, rows[1], new TimeOnly(8, 0), new TimeOnly(22, 0));

        // A slot at 12:00 would leave no time for a 45-minute journey.
        Assert.DoesNotContain(new TimeOnly(12, 0), times);
        Assert.DoesNotContain(new TimeOnly(12, 30), times);
        Assert.Contains(new TimeOnly(13, 0), times);
    }

    [Fact]
    public void A_full_day_offers_no_times_at_all()
    {
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "All day thing", "time": "08:00", "endTime": "22:00", "isFixed": true },
                { "title": "Museum", "durationMinutes": 60 }
              ] }
            """;

        var rows = GlunoDraftPlan.Rows(Plan(plan));

        // The caller then offers another day, or stops. It never claims the
        // strategy worked.
        Assert.Empty(GlunoDraftPlan.AvailableTimes(
            rows, rows[1], new TimeOnly(8, 0), new TimeOnly(22, 0)));
    }

    [Fact]
    public void Choosing_a_time_carries_the_length_with_it()
    {
        var moved = GlunoDraftPlan.WithTime(DayPlan, 2, new TimeOnly(15, 0), 90);

        Assert.NotNull(moved);

        var row = GlunoDraftPlan.Rows(Plan(moved!))[2];

        Assert.Equal(new TimeOnly(15, 0), row.Start);
        // A row whose start moved and whose end did not is a row that silently
        // changed length.
        Assert.Equal(new TimeOnly(16, 30), row.End);
    }

    [Fact]
    public void Moving_one_row_leaves_the_others_alone()
    {
        var moved = GlunoDraftPlan.WithTime(DayPlan, 2, new TimeOnly(15, 0), 90);
        var rows = GlunoDraftPlan.Rows(Plan(moved!));

        Assert.Equal(new TimeOnly(10, 0), rows[0].Start);
        Assert.Equal(new TimeOnly(19, 0), rows[1].Start);
        Assert.True(rows[1].IsFixed);
    }

    // ── shorten ──────────────────────────────────────────────────────────

    [Fact]
    public void Shortening_below_the_minimum_is_refused()
    {
        Assert.Null(GlunoDraftPlan.WithShortened(DayPlan, 2, 15));
        Assert.Null(GlunoDraftPlan.WithShortened(DayPlan, 2, 0));

        // Below the floor "shorten it" stops being a plan and becomes a way of
        // pretending something fits.
        Assert.Equal(30, GlunoDraftPlan.MinimumDurationMinutes);
    }

    [Fact]
    public void Shortening_a_booking_is_refused()
    {
        // Index 1 is the fixed dinner. A booking is not Gluno's to trim.
        Assert.Null(GlunoDraftPlan.WithShortened(DayPlan, 1, 60));
    }

    [Fact]
    public void Shortening_to_longer_than_it_was_is_refused()
    {
        // The bar is 90 minutes. 120 is not shortening.
        Assert.Null(GlunoDraftPlan.WithShortened(DayPlan, 2, 120));
    }

    [Fact]
    public void Shortening_moves_the_end_time_and_the_duration_together()
    {
        var shortened = GlunoDraftPlan.WithShortened(DayPlan, 2, 60);

        Assert.NotNull(shortened);

        var row = GlunoDraftPlan.Rows(Plan(shortened!))[2];

        Assert.Equal(60, row.DurationMinutes);
        Assert.Equal(new TimeOnly(19, 30), row.Start);
        Assert.Equal(new TimeOnly(20, 30), row.End);
    }

    // ── move_existing and replace_existing ───────────────────────────────

    [Fact]
    public void A_move_of_an_existing_activity_is_stored_not_performed()
    {
        var activityId = Guid.NewGuid();

        var updated = GlunoDraftPlan.WithOperation(DayPlan, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = activityId,
            ToDate = "2026-08-15",
            ToTime = "20:00",
            FromDate = "2026-08-14",
            FromTime = "19:00",
            FromTitle = "Dinner",
        });

        Assert.NotNull(updated);

        var operations = GlunoDraftPlan.Operations(Plan(updated!));

        Assert.Single(operations);
        Assert.Equal(activityId, operations[0].ActivityId);
        // The plan's own rows are untouched. Nothing about this reaches the
        // Adventure until apply.
        Assert.Equal(3, GlunoDraftPlan.Rows(Plan(updated!)).Count);
    }

    [Fact]
    public void An_operation_carries_what_the_activity_looked_like()
    {
        var updated = GlunoDraftPlan.WithOperation(DayPlan, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = Guid.NewGuid(),
            ToDate = "2026-08-15",
            FromDate = "2026-08-14",
            FromTime = "19:00",
            FromTitle = "Dinner",
        });

        var operation = GlunoDraftPlan.Operations(Plan(updated!))[0];

        // The snapshot apply re-checks. Without it a booking somebody else
        // moved in the meantime would be silently overwritten.
        Assert.Equal("2026-08-14", operation.FromDate);
        Assert.Equal("19:00", operation.FromTime);
        Assert.Equal("Dinner", operation.FromTitle);
    }

    [Fact]
    public void The_same_activity_is_never_operated_on_twice()
    {
        var activityId = Guid.NewGuid();

        var once = GlunoDraftPlan.WithOperation(DayPlan, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = activityId,
            ToTime = "20:00",
        });

        var twice = GlunoDraftPlan.WithOperation(once!, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = activityId,
            ToTime = "21:00",
        });

        var operations = GlunoDraftPlan.Operations(Plan(twice!));

        // Two moves of one thing, and the second computed from a position the
        // first invalidated. The latest wins.
        Assert.Single(operations);
        Assert.Equal("21:00", operations[0].ToTime);
    }

    [Fact]
    public void Operations_on_different_activities_both_survive()
    {
        var first = GlunoDraftPlan.WithOperation(DayPlan, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.MoveExisting,
            ActivityId = Guid.NewGuid(),
        });

        var second = GlunoDraftPlan.WithOperation(first!, new GlunoDraftOperation
        {
            Type = GlunoDraftOperationTypes.ReplaceExisting,
            ActivityId = Guid.NewGuid(),
        });

        Assert.Equal(2, GlunoDraftPlan.Operations(Plan(second!)).Count);
    }

    [Fact]
    public void An_operation_naming_no_activity_is_discarded()
    {
        var plan = """
            { "date": "2026-08-14", "activities": [],
              "operations": [ { "type": "move_existing", "activityId": "00000000-0000-0000-0000-000000000000" } ] }
            """;

        // It could not be carried out, and must not become a silent no-op at
        // apply time.
        Assert.Empty(GlunoDraftPlan.Operations(Plan(plan)));
    }

    [Fact]
    public void An_unreadable_operation_does_not_discard_the_others()
    {
        var good = Guid.NewGuid();

        var plan = $$"""
            { "date": "2026-08-14", "activities": [],
              "operations": [
                "not an object",
                { "type": "move_existing", "activityId": "{{good}}" }
              ] }
            """;

        var operations = GlunoDraftPlan.Operations(Plan(plan));

        Assert.Single(operations);
        Assert.Equal(good, operations[0].ActivityId);
    }

    [Fact]
    public void Only_two_operation_types_exist()
    {
        Assert.True(GlunoDraftOperationTypes.IsKnown(GlunoDraftOperationTypes.MoveExisting));
        Assert.True(GlunoDraftOperationTypes.IsKnown(GlunoDraftOperationTypes.ReplaceExisting));

        // A closed list. Apply refuses anything else rather than guessing.
        Assert.False(GlunoDraftOperationTypes.IsKnown("delete_everything"));
        Assert.False(GlunoDraftOperationTypes.IsKnown(null));
    }

    // ── Apply carries them out, and re-checks first ──────────────────────

    [Fact]
    public void Apply_refuses_an_operation_whose_activity_changed()
    {
        var source = Source("GlunoProposalApplyService.cs");

        Assert.Contains("MatchesSnapshot(activity, operation)", source);
        Assert.Contains("activity_changed", source);
    }

    [Fact]
    public void Apply_refuses_an_operation_whose_activity_is_gone()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var start = source.IndexOf("private async Task<(string, string)?> ApplyOperationsAsync", StringComparison.Ordinal);
        var body = source[start..(start + 3000)];

        Assert.Contains("activity_missing", body);
    }

    [Fact]
    public void Operations_run_inside_the_same_transaction_as_the_new_rows()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var operationsAt = source.IndexOf("await ApplyOperationsAsync(", StringComparison.Ordinal);
        var createAt = source.IndexOf("await CreateActivityAsync(trip, userId, draft, changes, ct, explicitSortIndex", StringComparison.Ordinal);

        Assert.True(operationsAt > 0);
        // Before the new rows, so a replacement's slot is free by the time its
        // replacement is written — and the whole answer lands together or not
        // at all.
        Assert.True(operationsAt < createAt);
    }

    [Fact]
    public void A_replacement_writes_a_feed_entry()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var start = source.IndexOf("private async Task<(string, string)?> ReplaceExistingAsync", StringComparison.Ordinal);
        var body = source[start..(start + 1600)];

        // A change that appears in somebody's Adventure without appearing in
        // its history is a change they cannot account for.
        Assert.Contains("activity_removed", body);
    }

    [Fact]
    public void A_moved_stay_is_revalidated_as_a_whole()
    {
        var source = Source("GlunoProposalApplyService.cs");

        var start = source.IndexOf("private async Task<(string, string)?> MoveExistingAsync", StringComparison.Ordinal);
        var body = source[start..(start + 2200)];

        // Same rule as the ordinary move endpoint. Gluno gets no wider
        // permission than a person.
        Assert.Contains("ValidateStayRange", body);
        Assert.Contains("TripDateRange.Contains", body);
    }

    // ── Every offered option resolves ────────────────────────────────────

    [Fact]
    public void Every_strategy_a_conflict_offers_can_be_carried_out()
    {
        foreach (var type in GlunoConflictTypes.All)
        {
            foreach (var movable in new[] { true, false })
            {
                var conflict = new GlunoProposalConflict
                {
                    ConflictType = type,
                    ExistingIsMovable = movable,
                    HoursAreUncertain = true,
                    AvailableMinutes = 30,
                    ConflictVersion = 1,
                };

                foreach (var option in GlunoConflictMapper.Options(conflict, "sv"))
                {
                    Assert.True(
                        GlunoConflictStrategies.IsSupported(option.Value),
                        $"{type} offers {option.Value}, which nothing can honour");
                }
            }
        }
    }

    [Fact]
    public void All_nine_strategies_are_now_supported()
    {
        foreach (var strategy in GlunoConflictStrategies.All)
        {
            Assert.True(
                GlunoConflictStrategies.IsSupported(strategy),
                $"{strategy} is on the list but cannot be carried out");
        }
    }

    [Fact]
    public void A_time_overlap_now_offers_a_real_fix_rather_than_only_skipping()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.TimeOverlap,
            ExistingIsMovable = true,
            ConflictVersion = 1,
        };

        var offered = GlunoConflictMapper.Options(conflict, "en").Select(option => option.Value).ToList();

        Assert.Contains(GlunoConflictStrategies.MoveNew, offered);
        Assert.Contains(GlunoConflictStrategies.ChooseAnotherTime, offered);
        Assert.Contains(GlunoConflictStrategies.MoveExisting, offered);
    }

    [Fact]
    public void A_locked_booking_still_offers_nothing_that_touches_it()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.LockedBooking,
            ExistingIsMovable = true,
            ConflictVersion = 1,
        };

        var offered = GlunoConflictMapper.Options(conflict, "en").Select(option => option.Value).ToList();

        Assert.DoesNotContain(GlunoConflictStrategies.MoveExisting, offered);
        Assert.DoesNotContain(GlunoConflictStrategies.ReplaceExisting, offered);
        // But it does offer to move the NEW one, which is always allowed.
        Assert.Contains(GlunoConflictStrategies.MoveNew, offered);
    }

    // ── The sub-questions ────────────────────────────────────────────────

    [Fact]
    public void A_time_option_is_a_fixed_vocabulary_value()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("private static GlunoStrategyOutcome TimeQuestion", StringComparison.Ordinal);
        var body = source[start..(start + 1800)];

        Assert.True(start > 0);
        // Nothing behind it to rot, and nothing the model had any part in
        // producing.
        Assert.Contains("GlunoClarificationEntityTypes.Enum", body);
    }

    [Fact]
    public void A_sub_question_spends_no_rebuild_and_moves_no_version()
    {
        var source = Source("GlunoChatService.cs");

        var askAt = source.IndexOf("if (outcome.NextCard is { } nextCard)", StringComparison.Ordinal);
        var recordAt = source.IndexOf(
            "draft = await _drafts.RecordRebuildAsync(draftId, userId, conflictType, strategy, ct)",
            StringComparison.Ordinal);

        Assert.True(askAt > 0);
        // The user picked a WAY to fix it, not a fix. Nothing has changed yet.
        Assert.True(askAt < recordAt);
    }

    [Fact]
    public void The_sub_question_reuses_the_original_user_message()
    {
        var source = Source("GlunoChatService.cs");
        var start = source.IndexOf("private async Task<GlunoTurnResult> AskSubQuestionAsync", StringComparison.Ordinal);
        var body = source[start..(start + 2400)];

        Assert.True(start > 0);
        Assert.Contains("OriginalUserMessageId = previous.OriginalUserMessageId", body);
        // Never appends a second copy of what somebody typed.
        Assert.DoesNotContain("GlunoMessageRoles.User", body);
    }

    [Fact]
    public void A_day_or_time_card_is_routed_to_the_draft_path()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..", "Controllers", "GlunoController.cs"));

        // On the draft binding, not the type. A day chooser produced by a
        // conflict is an ordinary `day` card, and routing on the type alone
        // would send it through the model and lose the plan it was fixing.
        Assert.Contains("clarification.DraftId.HasValue", source);
    }

    [Fact]
    public void The_strategy_comes_from_the_card_not_from_the_tap()
    {
        var source = Source("GlunoChatService.cs");

        // A day card's option is a DATE. Reading it as a strategy would send a
        // date into the strategy switch.
        Assert.Contains("GlunoClarificationTypes.Day => GlunoConflictStrategies.ChooseAnotherDay", source);
        Assert.Contains("GlunoClarificationTypes.ActivityTime => GlunoConflictStrategies.ChooseAnotherTime", source);
    }

    [Fact]
    public void A_chosen_day_is_rechecked_against_the_trip()
    {
        var source = Source("GlunoChatService.cs");

        // The card may be an hour old, and the trip's dates can have changed.
        Assert.Contains("TripDateRange.Contains(context.Trip!.StartDate, context.Trip.EndDate, chosen)", source);
    }

    // ── wrong_destination_day ────────────────────────────────────────────

    [Fact]
    public void A_far_away_activity_on_an_explicit_day_is_flagged()
    {
        var date = new DateOnly(2026, 8, 14);

        var trip = Trip(date, date.AddDays(3));
        trip = trip with
        {
            // Málaga, explicitly, on the 14th.
            DayLocations = [new GlunoDayLocationContext
            {
                Date = date, SortIndex = 0, Label = "Málaga", Latitude = 36.72, Longitude = -4.42,
            }],
            Destinations = new TripDestinationSummary
            {
                Title = "Spain",
                StartDate = "2026-08-14",
                EndDate = "2026-08-17",
                Stops = [new TripStop
                {
                    Label = "Málaga", From = "2026-08-14", To = "2026-08-14",
                    Source = "day_location", IsExplicit = true,
                }],
            },
        };

        // Barcelona: 800 km away.
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Sagrada Família", "time": "10:00", "latitude": 41.40, "longitude": 2.17 }
              ] }
            """;

        Assert.Contains(0, GlunoDestinationCheck.Mismatched(Plan(plan), trip));
    }

    [Fact]
    public void A_normal_day_trip_is_not_flagged()
    {
        var date = new DateOnly(2026, 8, 14);

        var trip = Trip(date, date.AddDays(3)) with
        {
            DayLocations = [new GlunoDayLocationContext
            {
                Date = date, SortIndex = 0, Label = "Málaga", Latitude = 36.72, Longitude = -4.42,
            }],
            Destinations = new TripDestinationSummary
            {
                Title = "Spain",
                StartDate = "2026-08-14",
                Stops = [new TripStop
                {
                    Label = "Málaga", From = "2026-08-14", To = "2026-08-14",
                    Source = "day_location", IsExplicit = true,
                }],
            },
        };

        // Ronda: about 100 km, and a perfectly ordinary day out from Málaga.
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Puente Nuevo", "time": "11:00", "latitude": 36.74, "longitude": -5.16 }
              ] }
            """;

        Assert.Empty(GlunoDestinationCheck.Mismatched(Plan(plan), trip));
    }

    [Fact]
    public void Free_text_never_produces_a_destination_mismatch()
    {
        var date = new DateOnly(2026, 8, 14);

        var trip = Trip(date, date.AddDays(3)) with
        {
            DayLocations = [new GlunoDayLocationContext
            {
                Date = date, SortIndex = 0, Label = "Málaga", Latitude = 36.72, Longitude = -4.42,
            }],
            Destinations = new TripDestinationSummary
            {
                Title = "Spain",
                StartDate = "2026-08-14",
                Stops = [new TripStop
                {
                    Label = "Málaga", From = "2026-08-14", To = "2026-08-14",
                    Source = "day_location", IsExplicit = true,
                }],
            },
        };

        // Names another city in the description and carries no coordinates.
        // This is the exact case that must NOT flag: the sentence is a
        // suggestion, not a location.
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Dinner", "time": "19:00",
                  "description": "somewhere near the old town in Barcelona, or maybe Sevilla" }
              ] }
            """;

        Assert.Empty(GlunoDestinationCheck.Mismatched(Plan(plan), trip));
    }

    [Fact]
    public void A_carried_forward_day_never_produces_a_mismatch()
    {
        var trip = Trip(new DateOnly(2026, 8, 14), new DateOnly(2026, 8, 17)) with
        {
            // The location is set on the 14th only; the 16th inherits it.
            DayLocations = [new GlunoDayLocationContext
            {
                Date = new DateOnly(2026, 8, 14), SortIndex = 0,
                Label = "Málaga", Latitude = 36.72, Longitude = -4.42,
            }],
            Destinations = new TripDestinationSummary
            {
                Title = "Spain",
                StartDate = "2026-08-14",
                Stops = [new TripStop
                {
                    Label = "Málaga", From = "2026-08-14", To = "2026-08-17",
                    Source = "day_location", IsExplicit = true,
                }],
            },
        };

        var plan = """
            { "date": "2026-08-16",
              "activities": [
                { "title": "Sagrada Família", "time": "10:00", "latitude": 41.40, "longitude": 2.17 }
              ] }
            """;

        // A day inheriting its location from two days earlier is not evidence
        // of where the trip is.
        Assert.Empty(GlunoDestinationCheck.Mismatched(Plan(plan), trip));
    }

    [Fact]
    public void An_existing_activity_is_never_the_mismatch()
    {
        var date = new DateOnly(2026, 8, 14);

        var trip = Trip(date, date.AddDays(3)) with
        {
            DayLocations = [new GlunoDayLocationContext
            {
                Date = date, SortIndex = 0, Label = "Málaga", Latitude = 36.72, Longitude = -4.42,
            }],
            Destinations = new TripDestinationSummary
            {
                Title = "Spain",
                StartDate = "2026-08-14",
                Stops = [new TripStop
                {
                    Label = "Málaga", From = "2026-08-14", To = "2026-08-14",
                    Source = "day_location", IsExplicit = true,
                }],
            },
        };

        var plan = $$"""
            { "date": "2026-08-14",
              "activities": [
                { "title": "Their own booking", "time": "10:00", "latitude": 41.40, "longitude": 2.17,
                  "existingActivityId": "{{Guid.NewGuid()}}" }
              ] }
            """;

        // Already in the plan. Not this suggestion's doing, so not its mistake.
        Assert.Empty(GlunoDestinationCheck.Mismatched(Plan(plan), trip));
    }

    [Fact]
    public void A_destination_mismatch_offers_a_day_or_a_skip()
    {
        var conflict = new GlunoProposalConflict
        {
            ConflictType = GlunoConflictTypes.WrongDestinationDay,
            ConflictVersion = 1,
        };

        var offered = GlunoConflictMapper.Options(conflict, "sv").Select(option => option.Value).ToList();

        Assert.Contains(GlunoConflictStrategies.ChooseAnotherDay, offered);
        Assert.Contains(GlunoConflictStrategies.RemoveNew, offered);
        Assert.Contains(GlunoConflictStrategies.Cancel, offered);
    }

    // ── Travel shortfall on the card ─────────────────────────────────────

    [Fact]
    public void A_travel_conflict_carries_how_short_the_gap_is()
    {
        var plan = """
            { "date": "2026-08-14",
              "activities": [
                { "title": "Museum", "time": "10:00", "endTime": "12:00" },
                { "title": "Gallery", "time": "12:20", "travelFromPrevious": { "minutes": 45 } }
              ] }
            """;

        var validation = new GlunoQualityResult
        {
            Passed = false,
            Blockers = [new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "travel_time_does_not_fit", "…") { ActivityIndex = 1 }],
            Warnings = Array.Empty<GlunoQualityIssue>(),
            RequiresClarification = false,
        };

        var conflict = GlunoConflictMapper.From(validation, 1, dayPlan: Plan(plan))[0];

        // 45 minutes needed, 20 available. 25 short — which is what the card
        // says, because "there isn't time" is true and unhelpful.
        Assert.Equal(45, conflict.RequiredTravelMinutes);
        Assert.Equal(20, conflict.AvailableMinutes);
    }

    [Fact]
    public void Other_conflict_types_carry_no_travel_figure()
    {
        var validation = new GlunoQualityResult
        {
            Passed = false,
            Blockers = [new GlunoQualityIssue(
                GlunoQualitySeverity.Blocker, "time_overlap", "…") { ActivityIndex = 2 }],
            Warnings = Array.Empty<GlunoQualityIssue>(),
            RequiresClarification = false,
        };

        var conflict = GlunoConflictMapper.From(validation, 1, dayPlan: Plan(DayPlan))[0];

        // A shortfall on an overlap would be a number about nothing.
        Assert.Equal(0, conflict.RequiredTravelMinutes);
        Assert.Equal(0, conflict.AvailableMinutes);
    }

    // ── Still no model, still no writes ──────────────────────────────────

    [Fact]
    public void The_strategy_engine_calls_no_model()
    {
        var source = Source("GlunoChatService.cs");

        var start = source.IndexOf("private async Task<GlunoStrategyOutcome> ApplyStrategyAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private static GlunoStrategyOutcome DayQuestion", StringComparison.Ordinal);
        var body = source[start..end];

        Assert.True(start > 0 && end > start);
        // Every branch is arithmetic over data the backend already holds. A
        // model asked to "move this to a better time" would be guessing at all
        // of it — and two identical taps could produce different plans.
        Assert.DoesNotContain("_provider", body);
        Assert.DoesNotContain("_ai", body);
    }

    [Fact]
    public void The_strategy_engine_writes_nothing()
    {
        var source = Source("GlunoChatService.cs");

        var start = source.IndexOf("private async Task<GlunoStrategyOutcome> ApplyStrategyAsync", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<GlunoTurnResult> AskSubQuestionAsync", StringComparison.Ordinal);
        var body = source[start..end];

        Assert.DoesNotContain("_db.TripActivities.Add", body);
        Assert.DoesNotContain("_db.TripActivities.Remove", body);
        Assert.DoesNotContain("SaveChangesAsync", body);
    }

    [Fact]
    public void GlunoDraftPlan_has_no_way_to_reach_the_database()
    {
        var source = Source("GlunoDraftPlan.cs");

        // It edits JSON documents and nothing else.
        Assert.DoesNotContain("AppDbContext", source);
        Assert.DoesNotContain("DbSet", source);
    }
}
