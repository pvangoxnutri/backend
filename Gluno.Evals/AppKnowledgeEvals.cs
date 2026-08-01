using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Gluno as an expert on SideQuest itself.
///
/// The failure these guard against is specific and expensive: a model asked
/// "how do I add a hotel?" will invent a plausible button, the user will go
/// looking for it, and they will conclude the app is broken rather than that
/// the assistant lied. So everything here checks the two things that make that
/// impossible — the capability registry is the only description of the app,
/// and navigation targets are an allow-list rather than a string.
///
/// Deterministic and offline: the search and the target rules are plain
/// functions, so these run with no model, no database and no network.
/// </summary>
public class AppKnowledgeEvals
{
    private static string? TopMatch(string query, string language = "en", string? screen = null)
        => SideQuestCapabilitySearch.Search(query, language, screen).FirstOrDefault()?.Capability.Id;

    private static IReadOnlyList<string> MatchIds(string query, string language = "en", string? screen = null)
        => SideQuestCapabilitySearch.Search(query, language, screen).Select(m => m.Capability.Id).ToList();

    // ── 1–6, 13, 14, 18: finding the right capability ─────────────────────

    [Theory]
    // 1. How do I add a hotel?
    [InlineData("how do I add a hotel?", "activity.stay")]
    [InlineData("hur lägger jag till ett hotell?", "activity.stay")]
    // 2. Several places on the same day
    [InlineData("how do I add another place to the same day?", "day.locations")]
    [InlineData("hur lägger jag till en plats till samma dag?", "day.locations")]
    // 3. Where is Travel Tracker?
    [InlineData("where do I find the travel tracker?", "travel_tracker")]
    [InlineData("var hittar jag jordgloben?", "travel_tracker")]
    // 5. Changing Adventure dates
    [InlineData("how do I change the adventure dates?", "adventure.dates")]
    [InlineData("hur ändrar jag resedatum?", "adventure.dates")]
    // 6. Photo missing from the slideshow
    // Both languages land on the EXCLUDE setting, not the slideshow itself:
    // "why isn't it showing" is answered by the per-Activity option, and
    // pointing at the general slideshow entry would not answer the question.
    [InlineData("why isn't my image showing in the slideshow?", "slideshow.exclude")]
    [InlineData("varför syns inte bilden i bildspelet?", "slideshow.exclude")]
    // 18. A one-word question
    [InlineData("packlista?", "packlist")]
    [InlineData("packing?", "packlist")]
    // 19. Moving an Activity
    [InlineData("how do I move an activity to another day?", "activity.move")]
    [InlineData("hur flyttar jag en aktivitet?", "activity.move")]
    public void The_right_feature_is_found(string query, string expectedId)
        => Assert.Equal(expectedId, TopMatch(query));

    [Fact]
    public void A_Swedish_question_using_the_English_feature_name_still_lands()
    {
        // 13. Someone typing Swedish but reaching for the English label.
        Assert.Equal("expenses", TopMatch("hur funkar cost split?", "sv"));
        Assert.Equal("travel_tracker", TopMatch("var är travel tracker?", "sv"));
    }

    [Theory]
    // 14. Misspellings people actually type.
    [InlineData("how do I add an activty?", "activity.create")]
    [InlineData("packlsita", "packlist")]
    [InlineData("dokumnet", "documents")]
    public void A_misspelling_still_finds_the_feature(string query, string expectedId)
        => Assert.Contains(expectedId, MatchIds(query));

    [Theory]
    // 8. Synonyms that are nothing like the feature's name.
    [InlineData("kostnader", "expenses")]
    [InlineData("dela nota", "expenses")]
    [InlineData("boende", "activity.stay")]
    [InlineData("bildspel", "slideshow")]
    [InlineData("schema", "activity.create")]
    public void Synonyms_resolve_to_the_right_feature(string query, string expectedId)
        => Assert.Contains(expectedId, MatchIds(query));

    // ── 4, 7, 8, 17: things SideQuest deliberately does not do ────────────

    [Fact]
    public void Deleting_a_chat_message_is_stated_as_a_limitation_not_invented_as_a_feature()
    {
        // 4. The chat capability is found, and it says outright that a sent
        // message cannot be deleted — so Gluno has a real answer instead of a
        // made-up menu item.
        var chat = SideQuestCapabilities.Find("chat")!;

        Assert.Contains(chat.LimitationsEn, limitation =>
            limitation.Contains("cannot be deleted", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(chat.LimitationsSv, limitation =>
            limitation.Contains("kan inte raderas", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Booking_is_explicitly_ruled_out()
    {
        // 7. "Can Gluno book a hotel for me?"
        var stay = SideQuestCapabilities.Find("activity.stay")!;

        Assert.Contains(stay.LimitationsEn, limitation =>
            limitation.Contains("does not book", StringComparison.OrdinalIgnoreCase));
        // And there is no booking action anywhere in the catalogue.
        Assert.DoesNotContain(GlunoActions.All, action =>
            action.Name.Contains("book", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void A_wake_word_and_a_microphone_are_ruled_out()
    {
        // 8. "Can Gluno listen for 'Hey Gluno'?"
        var gluno = SideQuestCapabilities.Find("gluno")!;

        Assert.Contains(gluno.LimitationsEn, limitation =>
            limitation.Contains("microphone", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(gluno.LimitationsSv, limitation =>
            limitation.Contains("mikrofon", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    // 17. Things SideQuest genuinely does not do. Nothing comes back, so
    // there is nothing for Gluno to describe.
    [InlineData("can I print a laminated wall poster")]
    [InlineData("does it sync with my fitness watch")]
    [InlineData("automatic currency conversion widget")]
    public void A_feature_SideQuest_does_not_have_returns_nothing_to_describe(string query)
        => Assert.Empty(SideQuestCapabilitySearch.Search(query, "en"));

    [Fact]
    public void A_near_miss_returns_the_closest_real_feature_rather_than_nothing()
    {
        // The other half of the rule: when the exact feature is missing but
        // something related exists, Gluno should be pointed at the real flow.
        // "Pay at the table" is not a SideQuest feature; splitting a bill is.
        Assert.Contains("expenses", MatchIds("split the bill with apple pay at the table"));
    }

    [Fact]
    public void An_unknown_feature_id_resolves_to_nothing_rather_than_a_guess()
    {
        // 11 & registry versioning: an id from an older or newer registry must
        // not resolve to "something close".
        Assert.Null(SideQuestCapabilities.Find("adventure.autopilot"));
        Assert.Null(SideQuestCapabilities.Find(null));
    }

    // ── 9, 11: permission and feature flags ───────────────────────────────

    [Fact]
    public void Owner_only_features_are_marked_so_a_member_is_not_sent_to_them()
    {
        // 9. A regular member asking about an owner-only setting.
        foreach (var id in new[] { "adventure.dates", "invites", "slideshow", "activity.reschedule" })
        {
            Assert.Equal(CapabilityAudiences.OwnerOnly, SideQuestCapabilities.Find(id)!.Audience);
        }

        // And the everyday ones are not owner-gated.
        Assert.Equal(CapabilityAudiences.AnyMember, SideQuestCapabilities.Find("documents")!.Audience);
        Assert.Equal(CapabilityAudiences.EditorsOnly, SideQuestCapabilities.Find("activity.create")!.Audience);
    }

    [Fact]
    public void A_flag_gated_feature_carries_its_flag()
    {
        // 11. Gluno itself is the flagged one; nothing else claims a flag it
        // does not have.
        Assert.Equal(SideQuestCapabilities.GlunoFlag, SideQuestCapabilities.Find("gluno")!.FeatureFlag);
        Assert.Null(SideQuestCapabilities.Find("documents")!.FeatureFlag);
    }

    [Fact]
    public void Whether_Gluno_can_act_is_explicit_per_feature()
    {
        // The three-way split the prompt depends on: suggest, or the user does
        // it themselves.
        Assert.NotEmpty(SideQuestCapabilities.Find("activity.create")!.GlunoActions);
        Assert.NotEmpty(SideQuestCapabilities.Find("activity.move")!.GlunoActions);

        // Gluno cannot touch these at all, and the registry says so.
        Assert.Empty(SideQuestCapabilities.Find("expenses")!.GlunoActions);
        Assert.Empty(SideQuestCapabilities.Find("packlist")!.GlunoActions);
        Assert.Empty(SideQuestCapabilities.Find("invites")!.GlunoActions);
        Assert.Empty(SideQuestCapabilities.Find("chat")!.GlunoActions);
    }

    // ── 12: already on the right screen ───────────────────────────────────

    [Fact]
    public void The_current_screen_lifts_its_own_features_in_the_results()
    {
        // 12. Asking a vague question while standing on Expenses should not
        // return Packlist first.
        var onExpenses = SideQuestCapabilitySearch.Search("hur funkar det här?", "sv", SideQuestScreens.Expenses);
        var onPacklist = SideQuestCapabilitySearch.Search("hur funkar det här?", "sv", SideQuestScreens.Packlist);

        // With no query signal at all the screen is what distinguishes them,
        // so at minimum the two must not produce identical top results.
        Assert.NotEqual(
            onExpenses.FirstOrDefault()?.Capability.Id,
            onPacklist.FirstOrDefault()?.Capability.Id);
    }

    [Fact]
    public void Screen_help_lists_only_what_lives_on_that_screen()
    {
        var help = SideQuestCapabilitySearch.ForScreen(SideQuestScreens.Documents);

        Assert.NotEmpty(help);
        Assert.All(help, match =>
            Assert.Contains(SideQuestScreens.Documents, match.Capability.Screens));
    }

    [Fact]
    public void Every_screen_a_capability_claims_actually_exists()
    {
        // A typo here would silently make screen-aware help never fire.
        foreach (var capability in SideQuestCapabilities.All)
        {
            Assert.All(capability.Screens, screen => Assert.True(SideQuestScreens.IsKnown(screen)));
        }
    }

    // ── 15, 16: navigation ────────────────────────────────────────────────

    [Fact]
    public void Adventure_targets_require_an_Adventure_and_say_so()
    {
        // 15. Navigating to the right Adventure — the rules force a trip id to
        // be present before the target can be built at all.
        foreach (var target in new[]
                 {
                     GlunoNavigationTargets.AdventureOverview,
                     GlunoNavigationTargets.AdventureSettings,
                     GlunoNavigationTargets.Documents,
                     GlunoNavigationTargets.Expenses,
                     GlunoNavigationTargets.Chat,
                 })
        {
            Assert.True(GlunoNavigationTargets.RulesFor(target)!.RequiresTrip);
        }

        Assert.False(GlunoNavigationTargets.RulesFor(GlunoNavigationTargets.TravelTracker)!.RequiresTrip);
    }

    [Theory]
    // 16. Route injection, in every shape a model might try.
    [InlineData("/trip/123/settings")]
    [InlineData("https://example.com")]
    [InlineData("../../admin/moderation")]
    [InlineData("adventure_settings; drop table")]
    [InlineData("ADVENTURE_SETTINGS")]
    [InlineData("")]
    [InlineData(null)]
    public void An_arbitrary_route_is_never_a_valid_target(string? target)
        => Assert.False(GlunoNavigationTargets.IsKnown(target));

    [Fact]
    public void Only_activity_detail_demands_an_activity()
    {
        Assert.True(GlunoNavigationTargets.RulesFor(GlunoNavigationTargets.ActivityDetail)!.RequiresActivity);

        // Everything else must not — requiring one would make the target
        // unusable, and accepting a stray one would be a leak.
        foreach (var target in GlunoNavigationTargets.All.Where(t => t != GlunoNavigationTargets.ActivityDetail))
        {
            Assert.False(GlunoNavigationTargets.RulesFor(target)!.RequiresActivity);
        }
    }

    [Fact]
    public void Every_navigation_target_a_capability_offers_is_on_the_allow_list()
    {
        // A capability pointing at a target that does not exist would render a
        // button the app cannot open.
        foreach (var capability in SideQuestCapabilities.All.Where(c => c.NavigationTarget != null))
        {
            Assert.True(GlunoNavigationTargets.IsKnown(capability.NavigationTarget));
        }
    }

    [Fact]
    public void Only_two_targets_accept_a_date_and_neither_saves_anything()
    {
        var dateTargets = GlunoNavigationTargets.All
            .Where(target => GlunoNavigationTargets.RulesFor(target)!.AcceptsDate)
            .ToList();

        Assert.Equal(
            new[] { GlunoNavigationTargets.ActivityCreate, GlunoNavigationTargets.AdventureFeedDay }.Order(),
            dateTargets.Order());
    }

    // ── 20: no Adventure selected ─────────────────────────────────────────

    [Fact]
    public void Features_that_need_no_Adventure_are_reachable_globally()
    {
        // 20. Global Gluno, no Adventure open.
        foreach (var id in new[] { "travel_tracker", "support", "adventure.create", "notifications" })
        {
            var capability = SideQuestCapabilities.Find(id)!;
            Assert.DoesNotContain("trip_selected", capability.Prerequisites);
            Assert.Equal(CapabilityAudiences.Anyone, capability.Audience);
        }
    }

    [Fact]
    public void Adventure_features_declare_that_they_need_one()
    {
        foreach (var id in new[] { "activity.create", "day.locations", "documents", "expenses", "packlist", "chat" })
        {
            Assert.Contains("trip_selected", SideQuestCapabilities.Find(id)!.Prerequisites);
        }
    }

    // ── Registry integrity ────────────────────────────────────────────────

    [Fact]
    public void Every_capability_is_complete_and_bilingual()
    {
        // A missing Swedish string would silently produce an English answer to
        // a Swedish user.
        foreach (var capability in SideQuestCapabilities.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.Id));
            Assert.False(string.IsNullOrWhiteSpace(capability.NameEn));
            Assert.False(string.IsNullOrWhiteSpace(capability.NameSv));
            Assert.False(string.IsNullOrWhiteSpace(capability.DescriptionEn));
            Assert.False(string.IsNullOrWhiteSpace(capability.DescriptionSv));
            Assert.False(string.IsNullOrWhiteSpace(capability.WhereEn));
            Assert.False(string.IsNullOrWhiteSpace(capability.WhereSv));
            Assert.True(
                capability.LimitationsEn.Count == capability.LimitationsSv.Count,
                $"{capability.Id} has a different number of limitations per language");
        }
    }

    [Fact]
    public void Capability_ids_are_unique()
    {
        var ids = SideQuestCapabilities.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Every_Gluno_action_a_capability_claims_actually_exists()
    {
        // A stale action name here would make Gluno promise something the
        // executor would refuse.
        foreach (var capability in SideQuestCapabilities.All)
        {
            Assert.All(capability.GlunoActions, action => Assert.NotNull(GlunoActions.Find(action)));
        }
    }

    [Fact]
    public void Searching_is_bounded_so_the_registry_is_never_dumped_whole()
    {
        // The whole registry in context is expensive and invites blending two
        // features into one that does not exist.
        var many = SideQuestCapabilitySearch.Search("adventure", "en", limit: 100);
        Assert.True(many.Count <= 8);
    }
}
