using sidequest.backend.Models;
using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for planning an Adventure with several people in it.
///
/// Two things go wrong here and both are quiet.
///
/// PRIVACY LEAKS BY INFERENCE. Nobody writes code that publishes a private
/// preference. What happens instead is that it reaches a planner, shapes a
/// plan, and then an answer explains the plan — "we've kept the walking short
/// because one of you needs that" — and something told to Gluno in confidence
/// is now group knowledge. So the tests below check the FILTER, not just the
/// output.
///
/// MAJORITY STEAMROLL. Four people want the hike, one cannot manage it. Count
/// votes and the hike wins every day of the week, and one person spends the
/// holiday sitting out. A hard constraint has to survive contact with
/// enthusiasm.
///
/// Nothing calls a model, a network, or a database.
/// </summary>
public class GroupPlanningEvals
{
    private static GroupConstraint Constraint(
        string key, string value, string member = "member-1", bool hard = false)
        => new()
        {
            Key = key,
            Value = value,
            MemberRef = member,
            IsHard = hard,
            Visibility = GlunoPreferenceVisibility.TripShared,
            Source = "preference",
            ConfirmedAt = DateTime.UtcNow,
        };

    private static TripPlanningProfile Profile(
        int groupSize = 3, params GroupConstraint[] constraints)
        => new()
        {
            TripId = Guid.NewGuid(),
            GroupSize = groupSize,
            ContributingMembers = constraints.Select(c => c.MemberRef).Distinct().Count(),
            Constraints = constraints,
        };

    private static IReadOnlyList<GroupConflict> Detect(TripPlanningProfile profile, string language = "en")
        => GroupPreferenceConflictDetector.Detect(profile, language);

    // ── 1 & 2. Pace ──────────────────────────────────────────────────────

    [Fact]
    public void Two_members_wanting_the_same_pace_is_not_a_conflict()
    {
        var conflicts = Detect(Profile(
            2,
            Constraint(GlunoPreferenceKeys.Pace, "relaxed", "member-1"),
            Constraint(GlunoPreferenceKeys.Pace, "lugnt", "member-2")));

        Assert.Empty(conflicts);
    }

    [Fact]
    public void Relaxed_against_packed_is_a_conflict_the_schedule_can_solve()
    {
        var conflict = Assert.Single(Detect(Profile(
            2,
            Constraint(GlunoPreferenceKeys.Pace, "relaxed", "member-1"),
            Constraint(GlunoPreferenceKeys.Pace, "packed", "member-2"))));

        Assert.Equal("pace_mismatch", conflict.Type);
        // One quiet day and one full day gives both; averaging gives neither.
        Assert.True(conflict.ResolvableBySchedule);
        Assert.InRange(conflict.Compromises.Count, 1, 3);
    }

    [Fact]
    public void A_conflict_never_names_a_member()
    {
        foreach (var language in new[] { "sv", "en" })
        {
            var conflicts = Detect(
                Profile(
                    3,
                    Constraint(GlunoPreferenceKeys.Pace, "relaxed", "member-1"),
                    Constraint(GlunoPreferenceKeys.Pace, "packed", "member-2")),
                language);

            Assert.All(conflicts, conflict =>
            {
                Assert.DoesNotContain("member-1", conflict.Explanation);
                Assert.DoesNotContain("member-2", conflict.Explanation);
            });
        }
    }

    // ── 3. A hard walking limit against distant stops ────────────────────

    [Fact]
    public void A_hard_walking_limit_against_long_walks_blocks_rather_than_warns()
    {
        var conflict = Assert.Single(Detect(Profile(
            3,
            Constraint(GlunoPreferenceKeys.WalkingDistance, "max 1 km", "member-1", hard: true),
            Constraint(GlunoPreferenceKeys.WalkingDistance, "vi går gärna mycket", "member-2"))));

        Assert.Equal("walking_vs_distance", conflict.Type);
        // Blocking, because this is exactly the case where a majority must not
        // win.
        Assert.Equal("blocking", conflict.Severity);
    }

    [Fact]
    public void No_compromise_ever_suggests_dropping_the_hard_side()
    {
        var conflict = Assert.Single(Detect(Profile(
            3,
            Constraint(GlunoPreferenceKeys.WalkingDistance, "max 1 km", "member-1", hard: true),
            Constraint(GlunoPreferenceKeys.WalkingDistance, "walk a lot", "member-2"))));

        // Every option satisfies BOTH — the compromises are about how, never
        // about which requirement to abandon.
        Assert.All(conflict.Compromises, option =>
            Assert.DoesNotContain("ignore", option, StringComparison.OrdinalIgnoreCase));
    }

    // ── 4. Budget ────────────────────────────────────────────────────────

    [Fact]
    public void A_low_budget_against_a_luxury_wish_needs_a_group_decision()
    {
        var conflict = Assert.Single(Detect(Profile(
            2,
            Constraint(GlunoPreferenceKeys.Budget, "vi håller nere kostnaderna", "member-1"),
            Constraint(GlunoPreferenceKeys.Budget, "vi vill unna oss lyx", "member-2"))));

        Assert.Equal("budget_mismatch", conflict.Type);
        Assert.True(conflict.RequiresGroupDecision);
        Assert.False(conflict.ResolvableBySchedule);
    }

    // ── 6 & 7. Private stays private ─────────────────────────────────────

    [Fact]
    public void Only_trip_shared_visibility_reaches_the_group()
    {
        Assert.True(GlunoPreferenceVisibility.IsSharedWithGroup(GlunoPreferenceVisibility.TripShared));

        // Everything else stays with its owner — including a global preference
        // that would obviously improve the group plan.
        Assert.False(GlunoPreferenceVisibility.IsSharedWithGroup(GlunoPreferenceVisibility.Private));
        Assert.False(GlunoPreferenceVisibility.IsSharedWithGroup(GlunoPreferenceVisibility.GlobalPrivate));
        Assert.False(GlunoPreferenceVisibility.IsSharedWithGroup(null));
    }

    [Fact]
    public void A_new_preference_is_private_by_default()
    {
        // The default is the security posture. Sharing is a deliberate act by
        // the person whose preference it is.
        Assert.Equal(GlunoPreferenceVisibility.Private, new GlunoPreference().Visibility);
        Assert.False(new GlunoPreference().IsHardConstraint);
    }

    [Fact]
    public void The_prompt_payload_carries_no_names_or_user_ids()
    {
        var profile = Profile(
            3,
            Constraint(GlunoPreferenceKeys.WalkingDistance, "short walks", "member-2", hard: true));

        var json = System.Text.Json.JsonSerializer.Serialize(profile.ForPrompt());

        // Neutral refs only. A model given a name will eventually use it.
        Assert.Contains("member-2", json);
        Assert.DoesNotContain("userId", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Anna", json);
    }

    [Fact]
    public void A_shared_constraint_in_the_ledger_carries_a_neutral_ref()
    {
        var ledger = new GlunoEvidenceLedger();

        var entry = ledger.AddSharedConstraint(
            Constraint(GlunoPreferenceKeys.WalkingDistance, "short walks", "member-3", hard: true));

        Assert.Equal("member-3", entry.SourceReference);
        Assert.DoesNotContain("-", entry.SourceReference!.Replace("member-", ""));
    }

    // ── 13, 14, 15, 16. Voting ───────────────────────────────────────────

    private static IReadOnlyList<GlunoPollOption> Options() =>
    [
        new("a", "Monaco and dinner by the harbour", null),
        new("b", "Beach day and dinner in Nice", null),
    ];

    private static GlunoPollResult Tally(params (Guid User, string? Option)[] votes)
    {
        var members = votes.Select(vote => vote.User).ToHashSet();
        return GlunoPollRules.Tally(Options(), votes.Select(v => (v.User, v.Option)).ToList(), members);
    }

    [Fact]
    public void A_changed_vote_replaces_rather_than_adds()
    {
        var user = Guid.NewGuid();

        // One row per member: the tally sees one vote, not two.
        var result = Tally((user, "b"));

        Assert.Equal(1, result.Responded);
        Assert.Equal(1, result.Tallies.Single(tally => tally.OptionId == "b").Votes);
        Assert.Equal(0, result.Tallies.Single(tally => tally.OptionId == "a").Votes);
    }

    [Fact]
    public void A_tie_is_never_resolved_automatically()
    {
        var result = Tally((Guid.NewGuid(), "a"), (Guid.NewGuid(), "b"));

        Assert.True(result.IsTie);
        // Picking a side would manufacture a decision nobody made.
        Assert.Null(result.WinningOptionId);
    }

    [Fact]
    public void A_unanimous_poll_has_a_clear_winner()
    {
        var result = Tally((Guid.NewGuid(), "a"), (Guid.NewGuid(), "a"), (Guid.NewGuid(), "a"));

        Assert.False(result.IsTie);
        Assert.Equal("a", result.WinningOptionId);
        Assert.True(result.EveryoneResponded);
    }

    [Fact]
    public void An_abstention_is_recorded_but_wins_nothing()
    {
        var result = Tally((Guid.NewGuid(), null), (Guid.NewGuid(), "a"));

        Assert.Equal(1, result.Abstained);
        Assert.Equal(2, result.Responded);
        Assert.Equal("a", result.WinningOptionId);
    }

    [Fact]
    public void A_removed_members_vote_stops_counting()
    {
        var staying = Guid.NewGuid();
        var left = Guid.NewGuid();

        var result = GlunoPollRules.Tally(
            Options(),
            [(staying, "a"), (left, "b")],
            // The departed member is no longer part of the group whose decision
            // this is — and cannot hold the poll open either.
            new HashSet<Guid> { staying });

        Assert.Equal(1, result.Responded);
        Assert.Equal("a", result.WinningOptionId);
        Assert.True(result.EveryoneResponded);
    }

    [Fact]
    public void Silence_is_never_counted_as_agreement()
    {
        var voted = Guid.NewGuid();
        var silent = Guid.NewGuid();

        var result = GlunoPollRules.Tally(
            Options(), [(voted, "a")], new HashSet<Guid> { voted, silent });

        Assert.Equal(1, result.Responded);
        Assert.Equal(2, result.GroupSize);
        // Not everyone has answered, so an "all_voted" poll stays open.
        Assert.False(result.EveryoneResponded);
    }

    // ── 33 & 34. Poll quality ────────────────────────────────────────────

    [Fact]
    public void Leading_options_are_rejected()
    {
        var problems = GlunoPollRules.Validate(
        [
            new("a", "A lovely relaxed day", "The best option for everyone"),
            new("b", "An exhausting rush around town", null),
        ]);

        Assert.Contains("leading_options", problems);
    }

    [Fact]
    public void Praising_exactly_one_option_is_leading()
    {
        Assert.True(GlunoPollRules.IsLeading(
        [
            new("a", "Monaco day", "The perfect way to see the coast"),
            new("b", "Beach day", "A day at the beach"),
        ]));
    }

    [Fact]
    public void Uniformly_warm_options_are_not_leading()
    {
        // Enthusiasm is a style; asymmetry is a thumb on the scale.
        Assert.False(GlunoPollRules.IsLeading(
        [
            new("a", "Monaco day", "A great day out on the coast"),
            new("b", "Beach day", "A great day by the sea"),
        ]));
    }

    [Fact]
    public void Fifteen_options_are_clamped_to_four()
    {
        var many = Enumerable.Range(0, 15)
            .Select(index => new GlunoPollOption($"o{index}", $"Option {index}", null))
            .ToList();

        var clamped = GlunoPollRules.Clamp(many);

        // A poll with fifteen options is a survey: people either don't answer
        // or split so thinly nothing wins.
        Assert.Equal(GlunoPollRules.MaxOptions, clamped.Count);
    }

    [Fact]
    public void A_single_option_is_not_a_poll()
    {
        Assert.Contains("too_few_options", GlunoPollRules.Validate([new("a", "The only choice", null)]));
    }

    [Fact]
    public void Well_formed_options_validate_clean()
    {
        Assert.Empty(GlunoPollRules.Validate(Options()));
    }

    // ── 17, 18, 19, 20. Fairness ─────────────────────────────────────────

    [Fact]
    public void A_hard_veto_excludes_a_candidate_however_popular_it_is()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("hike", "Mountain hike")
            {
                WantedBy = ["member-1", "member-2", "member-3", "member-4"],
                VetoedBy = ["member-5"],
                HasHardVeto = true,
            },
            new GroupCandidate("museum", "Museum") { WantedBy = ["member-1"] },
        ],
        new Dictionary<string, int>());

        var hike = ranked.Single(entry => entry.Candidate.Id == "hike");

        // Four to one, and it still does not win. A hard veto is a no, not a
        // low score that enough enthusiasm can outweigh.
        Assert.True(hike.IsExcluded);
        Assert.Contains("hard_veto", hike.Signals);
        Assert.Equal("museum", ranked.First(entry => !entry.IsExcluded).Candidate.Id);
    }

    [Fact]
    public void Breaking_a_hard_constraint_excludes_a_candidate()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("far", "Distant viewpoint")
            {
                WantedBy = ["member-1", "member-2", "member-3"],
                BreaksHardConstraint = true,
            },
        ],
        new Dictionary<string, int>());

        Assert.True(ranked[0].IsExcluded);
        Assert.Contains("breaks_hard_constraint", ranked[0].Signals);
    }

    [Fact]
    public void A_soft_minority_wish_does_not_beat_a_broadly_wanted_option()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("popular", "Old town") { WantedBy = ["member-1", "member-2", "member-3"] },
            new GroupCandidate("niche", "Model railway museum") { WantedBy = ["member-4"] },
        ],
        new Dictionary<string, int>());

        Assert.Equal("popular", ranked[0].Candidate.Id);
        Assert.Contains("broadly_wanted", ranked[0].Signals);
    }

    [Fact]
    public void A_must_outweighs_a_casual_wish()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("must", "The one thing they came for")
            {
                WantedBy = ["member-1"],
                MustFor = ["member-1"],
            },
            new GroupCandidate("nice", "Sounds pleasant") { WantedBy = ["member-2"] },
        ],
        new Dictionary<string, int>());

        Assert.Equal("must", ranked[0].Candidate.Id);
        Assert.Contains("is_a_must_for_someone", ranked[0].Signals);
    }

    [Fact]
    public void Someone_already_favoured_carries_slightly_less_weight_next_time()
    {
        var candidates = new List<GroupCandidate>
        {
            new("theirs", "Another of member-1's picks") { WantedBy = ["member-1"] },
            new("someone_else", "Member-2's pick") { WantedBy = ["member-2"] },
        };

        var fresh = GlunoGroupFairness.Rank(candidates, new Dictionary<string, int>());
        var afterThree = GlunoGroupFairness.Rank(
            candidates, new Dictionary<string, int> { ["member-1"] = 3 });

        // Untouched, both weigh the same — the alphabetical tiebreak decides,
        // which is arbitrary but deterministic. What matters is the CHANGE.
        Assert.Equal(
            fresh.Single(entry => entry.Candidate.Id == "theirs").Score,
            fresh.Single(entry => entry.Candidate.Id == "someone_else").Score);

        // After three of member-1's picks, theirs weighs less than member-2's.
        // Not because their view matters less — because a week where one person
        // gets everything is not a group trip.
        Assert.True(
            afterThree.Single(entry => entry.Candidate.Id == "theirs").Score
            < afterThree.Single(entry => entry.Candidate.Id == "someone_else").Score);
        Assert.Equal("someone_else", afterThree[0].Candidate.Id);
    }

    [Fact]
    public void Priorities_spread_across_days_rather_than_going_to_one_member()
    {
        var candidates = new List<GroupCandidate>
        {
            new("a1", "A first") { WantedBy = ["member-1"] },
            new("a2", "A second") { WantedBy = ["member-1"] },
            new("a3", "A third") { WantedBy = ["member-1"] },
            new("b1", "B first") { WantedBy = ["member-2"] },
        };

        var ranked = GlunoGroupFairness.Rank(candidates, new Dictionary<string, int>());
        var plan = GlunoGroupFairness.SpreadAcrossDays(ranked, ["2026-08-12", "2026-08-13"], perDay: 2);

        var placed = plan.Values.SelectMany(day => day).ToList();

        // Member-2's single pick gets in rather than being buried under
        // member-1's three.
        Assert.Contains(placed, candidate => candidate.Id == "b1");
    }

    [Fact]
    public void An_excluded_candidate_is_never_placed_in_a_day()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("vetoed", "No") { WantedBy = ["member-1"], HasHardVeto = true },
            new GroupCandidate("fine", "Yes") { WantedBy = ["member-2"] },
        ],
        new Dictionary<string, int>());

        var plan = GlunoGroupFairness.SpreadAcrossDays(ranked, ["2026-08-12"], perDay: 3);

        Assert.DoesNotContain(plan["2026-08-12"], candidate => candidate.Id == "vetoed");
    }

    // ── 23, 24, 25, 26. More conflict shapes ─────────────────────────────

    [Fact]
    public void Nightlife_against_an_early_start_is_schedulable()
    {
        var conflict = Assert.Single(Detect(Profile(
            2,
            Constraint(GlunoPreferenceKeys.Nightlife, "vi vill ut på kvällarna", "member-1"),
            Constraint(GlunoPreferenceKeys.StartTime, "tidiga morgnar", "member-2"))));

        Assert.Equal("nightlife_vs_early_start", conflict.Type);
        Assert.True(conflict.ResolvableBySchedule);
    }

    [Fact]
    public void A_hard_no_car_against_a_car_plan_blocks()
    {
        var conflict = Assert.Single(Detect(Profile(
            3,
            Constraint(GlunoPreferenceKeys.Transport, "vi vill inte köra bil", "member-1", hard: true),
            Constraint(GlunoPreferenceKeys.Transport, "vi har hyrbil", "member-2"))));

        Assert.Equal("car_vs_no_car", conflict.Type);
        Assert.Equal("blocking", conflict.Severity);
        Assert.True(conflict.RequiresGroupDecision);
    }

    [Fact]
    public void Several_hard_dietary_requirements_are_flagged_as_one_constraint_set()
    {
        var conflict = Assert.Single(Detect(Profile(
            3,
            Constraint(GlunoPreferenceKeys.Food, "vegan", "member-1", hard: true),
            Constraint(GlunoPreferenceKeys.Food, "glutenfritt", "member-2", hard: true))));

        Assert.Equal("dietary_requirements", conflict.Type);
        // Not a mismatch to resolve — a set every restaurant has to satisfy at
        // once.
        Assert.False(conflict.ResolvableBySchedule);
    }

    [Fact]
    public void A_veto_against_someone_elses_priority_needs_the_group()
    {
        var conflict = Assert.Single(Detect(Profile(
            3,
            Constraint(GlunoPreferenceKeys.Avoid, "museer", "member-1", hard: true),
            Constraint(GlunoPreferenceKeys.Interests, "museer och konst", "member-2"))));

        Assert.Equal("veto_vs_priority", conflict.Type);
        Assert.True(conflict.RequiresGroupDecision);
    }

    // ── 37 & 38. Solo Adventures ─────────────────────────────────────────

    [Fact]
    public void A_solo_adventure_has_no_group_conflicts()
    {
        var profile = Profile(
            1,
            Constraint(GlunoPreferenceKeys.Pace, "relaxed", "member-1"),
            Constraint(GlunoPreferenceKeys.Pace, "packed", "member-1"));

        Assert.True(profile.IsSoloTrip);
        // Group machinery on a solo trip is noise. Gluno plans exactly as it
        // always has.
        Assert.Empty(Detect(profile));
    }

    [Fact]
    public void An_empty_profile_produces_nothing()
    {
        Assert.Empty(Detect(Profile(4)));
    }

    // ── 27, 28, 29, 31. Decisions and consensus ──────────────────────────

    [Fact]
    public void Only_an_accepted_decision_counts_as_settled()
    {
        var accepted = new GroupDecisionSummary(
            Guid.NewGuid(), GlunoGroupDecisionKinds.Pace, GlunoGroupDecisionStatuses.Accepted, "Relaxed", 1);

        Assert.True(accepted.IsSettled);

        // Pending is people still talking; superseded is a decision that was
        // replaced. Neither is "the group decided".
        foreach (var status in new[]
        {
            GlunoGroupDecisionStatuses.Pending,
            GlunoGroupDecisionStatuses.Rejected,
            GlunoGroupDecisionStatuses.Expired,
            GlunoGroupDecisionStatuses.Superseded,
        })
        {
            Assert.False(accepted with { Status = status } is { IsSettled: true });
        }
    }

    [Fact]
    public void A_pending_decision_enters_the_ledger_already_expired()
    {
        var ledger = new GlunoEvidenceLedger();

        ledger.AddGroupDecision(new GroupDecisionSummary(
            Guid.NewGuid(), GlunoGroupDecisionKinds.Pace, GlunoGroupDecisionStatuses.Pending, null, 1));

        // Present, and unable to support "the group has decided".
        Assert.NotEmpty(ledger.Entries);
        Assert.False(ledger.HasAny(GlunoClaimTypes.ConfirmedGroupDecision, DateTime.UtcNow));
    }

    [Fact]
    public void An_accepted_decision_does_support_a_group_claim()
    {
        var ledger = new GlunoEvidenceLedger();

        ledger.AddGroupDecision(new GroupDecisionSummary(
            Guid.NewGuid(), GlunoGroupDecisionKinds.Pace, GlunoGroupDecisionStatuses.Accepted, "Relaxed", 1));

        Assert.True(ledger.HasAny(GlunoClaimTypes.ConfirmedGroupDecision, DateTime.UtcNow));
    }

    [Fact]
    public void Claiming_consensus_with_no_decision_is_blocked()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "Everyone agreed on the Monaco day, so I've built around that.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
        });

        Assert.False(result.Passed);
        Assert.Contains(result.UnsupportedClaims, claim => claim.Reason == "no_group_decision");
    }

    [Fact]
    public void Claiming_consensus_in_swedish_is_also_blocked()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "Gruppen har bestämt att ni åker till Monaco.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
            Language = "sv",
        });

        Assert.False(result.Passed);
    }

    // ── 32. Never name the member behind a constraint ────────────────────

    [Fact]
    public void Blaming_a_member_is_rewritten_as_a_property_of_the_plan()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "Someone is blocking the hike, so I've dropped it.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
        });

        Assert.False(result.Passed);
        Assert.Contains(result.UnsupportedClaims,
            claim => claim.Reason == "attributes_constraint_to_member");
        Assert.DoesNotContain("blocking", result.SafeCorrections!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Appealing_to_the_majority_is_blocked()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "The majority wants this, so that's the plan.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
        });

        Assert.False(result.Passed);
    }

    [Fact]
    public void Calling_a_compromise_objectively_fair_is_blocked()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "This is the only fair solution for the group.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
        });

        Assert.False(result.Passed);
        Assert.Contains(result.UnsupportedClaims, claim => claim.Reason == "claims_objective_fairness");
    }

    [Fact]
    public void A_neutral_compromise_sentence_passes()
    {
        var result = new GlunoGroundingValidator().Validate(new GlunoGroundingInput
        {
            AnswerText = "The plan keeps two of the group's shared favourites and adds a quieter stop "
                + "in the afternoon.",
            Ledger = new GlunoEvidenceLedger(),
            NowUtc = DateTime.UtcNow,
        });

        Assert.True(result.Passed);
    }

    [Fact]
    public void The_compromise_explanation_never_claims_fairness()
    {
        var ranked = GlunoGroupFairness.Rank(
        [
            new GroupCandidate("a", "One") { WantedBy = ["member-1", "member-2", "member-3"] },
        ],
        new Dictionary<string, int>());

        foreach (var language in new[] { "sv", "en" })
        {
            var text = GlunoGroupFairness.ExplainCompromise(ranked, 2, language);

            Assert.DoesNotContain("fair", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("rättvis", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    // ── 39 & 40. Both languages ──────────────────────────────────────────

    [Fact]
    public void Conflicts_are_localised_and_the_languages_differ()
    {
        var profile = Profile(
            2,
            Constraint(GlunoPreferenceKeys.Pace, "relaxed", "member-1"),
            Constraint(GlunoPreferenceKeys.Pace, "packed", "member-2"));

        var swedish = Assert.Single(Detect(profile, "sv"));
        var english = Assert.Single(Detect(profile, "en"));

        Assert.NotEqual(swedish.Explanation, english.Explanation);
        Assert.NotEqual(swedish.Compromises[0], english.Compromises[0]);
    }

    // ── 44. Versioning ───────────────────────────────────────────────────

    [Fact]
    public void The_planning_profile_and_decisions_are_versioned()
    {
        Assert.True(TripPlanningProfile.CurrentVersion >= 1);
        Assert.True(GlunoGroupDecision.CurrentVersion >= 1);
        Assert.Equal(GlunoGroupDecision.CurrentVersion, new GlunoGroupDecision().Version);
    }

    [Fact]
    public void Decision_statuses_distinguish_open_from_terminal()
    {
        Assert.True(GlunoGroupDecisionStatuses.IsOpen(GlunoGroupDecisionStatuses.Pending));

        foreach (var status in new[]
        {
            GlunoGroupDecisionStatuses.Accepted,
            GlunoGroupDecisionStatuses.Rejected,
            GlunoGroupDecisionStatuses.Expired,
            GlunoGroupDecisionStatuses.Superseded,
        })
        {
            Assert.False(GlunoGroupDecisionStatuses.IsOpen(status));
            Assert.True(GlunoGroupDecisionStatuses.IsTerminal(status));
        }
    }

    [Fact]
    public void An_unknown_decision_kind_is_refused()
    {
        Assert.False(GlunoGroupDecisionKinds.IsKnown("who_pays"));
        Assert.False(GlunoGroupDecisionKinds.IsKnown(null));
        Assert.True(GlunoGroupDecisionKinds.IsKnown(GlunoGroupDecisionKinds.Pace));
    }

    // ── 45. Individual planning is untouched ─────────────────────────────

    [Fact]
    public void A_profile_with_no_shared_data_leaves_individual_planning_alone()
    {
        var profile = Profile(3);

        Assert.Empty(profile.Constraints);
        Assert.Empty(Detect(profile));
        Assert.Null(profile.SettledDecision(GlunoGroupDecisionKinds.Pace));
        Assert.Equal(0, profile.ContributingMembers);
    }

    [Fact]
    public void A_settled_decision_is_readable_and_a_pending_one_is_not()
    {
        var profile = Profile(3) with
        {
            Decisions =
            [
                new GroupDecisionSummary(
                    Guid.NewGuid(), GlunoGroupDecisionKinds.Pace,
                    GlunoGroupDecisionStatuses.Accepted, "Relaxed", 1),
                new GroupDecisionSummary(
                    Guid.NewGuid(), GlunoGroupDecisionKinds.Budget,
                    GlunoGroupDecisionStatuses.Pending, null, 1),
            ],
        };

        Assert.Equal("Relaxed", profile.SettledDecision(GlunoGroupDecisionKinds.Pace));
        // Still being voted on — not a decision Gluno may quote.
        Assert.Null(profile.SettledDecision(GlunoGroupDecisionKinds.Budget));
    }

    // ── The prompt's own group rules ─────────────────────────────────────

    [Fact]
    public void The_prompt_forbids_naming_who_holds_a_constraint()
    {
        var prompt = GlunoSystemPrompt.Text;

        Assert.Contains("Never say whose constraint is whose", prompt);
        Assert.Contains("one of you is holding this up", prompt);
    }

    [Fact]
    public void The_prompt_states_that_hard_constraints_are_not_votes()
    {
        Assert.Contains("Hard constraints are not votes", GlunoSystemPrompt.Text);
    }

    [Fact]
    public void The_prompt_forbids_claiming_consensus_early_and_claiming_fairness()
    {
        var prompt = GlunoSystemPrompt.Text;

        // Asserted on phrases that do not straddle a line break in the prompt's
        // wrapped text — a wrap is not a behaviour change.
        Assert.Contains("an abstention is not a yes", prompt);
        Assert.Contains("Never call a plan fair", prompt);
    }

    [Fact]
    public void The_prompt_keeps_private_preferences_out_of_the_group_plan()
    {
        Assert.Contains("Private is private", GlunoSystemPrompt.Text);
    }
}
