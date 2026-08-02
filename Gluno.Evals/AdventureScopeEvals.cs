using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for which Adventure Gluno is actually talking about.
///
/// THE BUG THESE EXIST FOR. Gluno told people "open Semester 2026 in the app
/// and I can see the days" — a flow that did not exist, because the only way
/// into Gluno from an Adventure was buried in its functions list. The sentence
/// was worse than unhelpful: it asked somebody to navigate away, come back,
/// and retype their question, to reach a button that was not there.
///
/// Two halves. The app has to offer a real Adventure entry point, and the scope
/// pill has to state what is TRUE rather than what the URL asked for. A pill
/// naming an Adventure over a globally-scoped conversation has already misled
/// the user about which plan Gluno can see.
///
/// Mobile source is read structurally — there is no test runner in that repo,
/// and the alternative is no coverage of the thing that actually broke.
/// </summary>
public class AdventureScopeEvals
{
    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }
            .Concat(parts).ToArray()));

    private static string Backend(string file) => File.ReadAllText(Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "Services", "Gluno", file));

    private static string AdventureScreen() => Mobile("app", "trip", "[id]", "index.tsx");
    private static string GlunoScreen() => Mobile("app", "gluno.tsx");
    private static string Translations() => Mobile("components", "i18n-provider.tsx");

    // ── 1. The entry point exists ────────────────────────────────────────

    [Fact]
    public void The_Adventure_header_has_a_Gluno_button()
    {
        var source = AdventureScreen();

        Assert.Contains("import GlunoButton from '@/components/gluno/GlunoButton'", source);
        Assert.Contains("<GlunoButton", source);
    }

    [Fact]
    public void The_Adventure_entry_uses_the_same_Gluno_identity_as_the_app_header()
    {
        // GlunoButton renders the mascot and hides itself behind the feature
        // flag. Hand-rolling an icon here would make Gluno look like two
        // features rather than one reachable from two places — and would ship
        // in a build where the flag is off.
        var button = Mobile("components", "gluno", "GlunoButton.tsx");

        Assert.Contains("GlunoMascot", button);
        Assert.Contains("if (!ENABLE_GLUNO_ASSISTANT) return null;", button);
    }

    [Fact]
    public void The_Adventure_entry_sits_beside_the_existing_header_actions()
    {
        var source = AdventureScreen();

        var glunoAt = source.IndexOf("<GlunoButton", StringComparison.Ordinal);
        var settingsAt = source.IndexOf("style={styles.settingsButton}", StringComparison.Ordinal);
        var backAt = source.IndexOf("style={styles.backButton}", StringComparison.Ordinal);

        Assert.True(glunoAt > 0);
        // Between the title and settings: added to the header rather than
        // displacing what was already there.
        Assert.True(backAt < glunoAt && glunoAt < settingsAt);
    }

    // ── 2. It carries the tripId ─────────────────────────────────────────

    [Fact]
    public void The_Adventure_entry_passes_this_Adventures_id()
    {
        var source = AdventureScreen();

        // Without it Gluno opens with no plan in front of it and has to ask
        // which Adventure — from inside the Adventure they were looking at.
        Assert.Contains("`/gluno?tripId=${encodeURIComponent(String(id))}", source);
    }

    [Fact]
    public void The_Adventure_entry_says_which_screen_it_came_from()
    {
        var source = AdventureScreen();

        // So app-help does not give directions to a screen they are on.
        Assert.Contains("GLUNO_SCREENS.adventureOverview", source);
    }

    [Fact]
    public void The_id_is_url_encoded()
    {
        var source = AdventureScreen();

        var index = source.IndexOf("`/gluno?tripId=", StringComparison.Ordinal);
        var line = source[index..(index + 120)];

        Assert.Contains("encodeURIComponent", line);
    }

    // ── 3. The right conversation, and only the right one ────────────────

    [Fact]
    public void The_conversation_lookup_is_scoped_exactly()
    {
        var source = File.ReadAllText(Path.Combine(
            AppContext.BaseDirectory, "..", "..", "..", "..",
            "Services", "Gluno", "GlunoConversationService.cs"));

        // `tripId == null ? c.TripId == null : c.TripId == tripId`. Not a
        // nullable comparison that would match a global row when a trip was
        // asked for, and not one that matches another Adventure's.
        Assert.Contains("tripId == null ? c.TripId == null : c.TripId == tripId", source);
    }

    [Fact]
    public void A_conversation_whose_scope_disagrees_with_the_route_is_not_used()
    {
        var source = GlunoScreen();

        // The server's answer is the fact; the URL is a request. A mismatch
        // means a cached id from another Adventure or a hand-edited link, and
        // the safe move is an empty chat that scopes correctly on the first
        // message.
        Assert.Contains("const serverTripId = detail.conversation.tripId ?? null;", source);
        Assert.Contains("if (serverTripId !== tripId)", source);
    }

    [Fact]
    public void The_cache_is_keyed_by_scope()
    {
        var source = GlunoScreen();

        // Same user, different Adventure, different cache entry. A shared key
        // would show one Adventure's turns under another's name before the
        // fetch even returned.
        Assert.Contains("readGlunoCache(userId, tripId)", source);
        Assert.Contains("writeGlunoCache(userId, tripId", source);
    }

    // ── 4. The pill tells the truth ──────────────────────────────────────

    [Fact]
    public void The_pill_is_not_driven_by_the_route_parameter_alone()
    {
        var source = GlunoScreen();

        // The old shape was `tripId ? name : global` — the URL deciding what
        // the user was told about scope. The pill is now a picker component,
        // and `scoped` is what it is handed.
        Assert.DoesNotContain("styles.scopePill, tripId &&", source);
        Assert.Contains("tripId={scoped ? tripId : null}", source);
    }

    [Fact]
    public void Scoped_requires_the_server_to_have_confirmed_it()
    {
        var source = GlunoScreen();

        Assert.Contains("const scoped = tripId != null && scopeVerified && !scopeLost;", source);
    }

    [Fact]
    public void The_pill_claims_nothing_while_the_check_is_running()
    {
        // Naming an Adventure and then turning out to be a global chat has
        // already told the user something untrue. The screen passes the
        // unverified state down; the picker renders "Checking…" for it.
        Assert.Contains(
            "checking={tripId != null && !scopeVerified && !scopeLost}", GlunoScreen());

        Assert.Contains(
            "gluno.scope.checking",
            Mobile("components", "gluno", "GlunoScopePicker.tsx"));
    }

    [Fact]
    public void The_word_Global_never_reaches_the_screen()
    {
        var translations = Translations();

        // A developer's word for "no trip selected". It told the user nothing
        // they wanted to know and implied a mode rather than a capability.
        foreach (var line in translations.Split('\n'))
        {
            if (!line.Contains("'gluno.scope.global'", StringComparison.Ordinal)) continue;

            Assert.DoesNotContain(": 'Global'", line);
            Assert.DoesNotContain(": 'Globalt", line);
        }

        Assert.Contains("'gluno.scope.global': 'All Adventures'", translations);
        Assert.Contains("'gluno.scope.global': 'Alla Adventures'", translations);
    }

    [Fact]
    public void The_scope_pill_is_a_real_chooser()
    {
        var picker = Mobile("components", "gluno", "GlunoScopePicker.tsx");

        // Not a label. Somebody looking at it had no way to act on what it
        // said, which is half of why the word was confusing.
        Assert.Contains("<TouchableOpacity", picker);
        Assert.Contains("<ModalSheet", picker);
        Assert.Contains("accessibilityRole=\"button\"", picker);
    }

    [Fact]
    public void The_chooser_always_offers_a_way_back_to_all_Adventures()
    {
        var picker = Mobile("components", "gluno", "GlunoScopePicker.tsx");

        var allAt = picker.IndexOf("t('gluno.scope.global')", StringComparison.Ordinal);
        var listAt = picker.IndexOf("ordered.map((trip)", StringComparison.Ordinal);

        Assert.True(allAt > 0 && listAt > 0);
        // First and always present. Somebody who scoped into a trip needs a
        // way out, and "all" is a real choice rather than the absence of one.
        Assert.True(allAt < listAt);
    }

    [Fact]
    public void Switching_scope_aborts_the_turn_being_left_behind()
    {
        var source = GlunoScreen();

        // Its answer would arrive into a conversation it was never about.
        var index = source.IndexOf("onChange={(choice) =>", StringComparison.Ordinal);
        var body = source[index..(index + 700)];

        Assert.Contains("abortRef.current?.abort()", body);
    }

    [Fact]
    public void Switching_scope_opens_the_other_conversation_rather_than_re_scoping_this_one()
    {
        var source = GlunoScreen();

        var index = source.IndexOf("onChange={(choice) =>", StringComparison.Ordinal);
        var body = source[index..(index + 700)];

        // A route change, so the screen reloads the conversation belonging to
        // that scope — its own history, its own cache key. Nothing mutates a
        // conversation from global to trip or between two Adventures.
        Assert.Contains("router.replace(", body);
        Assert.Contains("pathname: '/gluno'", body);
    }

    [Fact]
    public void The_chooser_shows_no_internal_ids()
    {
        var picker = Mobile("components", "gluno", "GlunoScopePicker.tsx");

        var start = picker.IndexOf("function describe(trip: Quest)", StringComparison.Ordinal);
        var body = picker[start..(start + 600)];

        Assert.True(start > 0);
        // A person recognises a trip by where and when, not by a guid.
        Assert.DoesNotContain("trip.id", body);
    }

    [Fact]
    public void A_sent_turn_also_settles_the_scope()
    {
        var source = GlunoScreen();

        var deliverAt = source.IndexOf("setTripName(turn.conversation.tripTitle", StringComparison.Ordinal);
        var body = source[deliverAt..(deliverAt + 400)];

        // A first message into an empty Adventure chat is exactly where the
        // pill goes from unverified to verified.
        Assert.Contains("setScopeVerified(true)", body);
    }

    // ── 5. Losing access is said out loud ────────────────────────────────

    [Fact]
    public void Lost_membership_is_reported_rather_than_silently_widened()
    {
        var source = GlunoScreen();

        Assert.Contains("error?.status === 403 || error?.status === 404", source);
        Assert.Contains("setScopeLost(true)", source);
        Assert.Contains("gluno.scope.lost", source);
    }

    [Fact]
    public void Lost_membership_closes_the_composer()
    {
        var source = GlunoScreen();

        // A message sent from there would be answered globally, which is the
        // widening this whole check exists to stop.
        Assert.Contains("unavailable || scopeLost ||", source);
    }

    [Fact]
    public void Lost_access_is_distinct_from_a_failed_load()
    {
        var source = GlunoScreen();

        // A retry fixes one and not the other, so they must not share a
        // message.
        Assert.Contains("scopeLost ? (", source);
        Assert.Contains("loadFailed ? (", source);
    }

    // ── 6. Back goes where they came from ────────────────────────────────

    [Fact]
    public void Gluno_returns_to_the_screen_that_opened_it()
    {
        var source = GlunoScreen();

        // router.back(), not a hardcoded destination — opened from an
        // Adventure it returns to that Adventure, opened from the tab header
        // it returns there.
        Assert.Contains("onPress={() => router.back()}", source);
    }

    [Fact]
    public void Gluno_is_a_pushed_screen_rather_than_a_sheet()
    {
        var layout = Mobile("app", "_layout.tsx");

        // Which is what gives it its own back stack entry.
        Assert.Contains("<Stack.Screen name=\"gluno\"", layout);
    }

    // ── 7. The language Gluno is allowed to use ──────────────────────────

    [Fact]
    public void The_prompt_forbids_telling_the_user_to_go_and_open_an_Adventure()
    {
        var source = Backend("GlunoSystemPrompt.cs");

        Assert.Contains("NEVER tell the user to go and open an Adventure", source);
    }

    [Fact]
    public void The_prompt_gives_the_wrong_and_right_phrasing_side_by_side()
    {
        var source = Backend("GlunoSystemPrompt.cs");

        // A rule with an example of the exact sentence that broke is far
        // harder to drift from than a rule alone.
        Assert.Contains("Open Semester 2026 in the app and I can see the days.", source);
        Assert.Contains("Which Adventure do you mean?", source);
    }

    [Fact]
    public void The_prompt_points_at_the_clarification_card_as_the_answer()
    {
        var source = Backend("GlunoSystemPrompt.cs");

        // Asking resolves the scope in one tap and keeps them in the
        // conversation. Telling them to navigate is asking them to do work the
        // app already does.
        Assert.Contains("tappable Adventure choices", source);
    }

    [Fact]
    public void A_scoped_conversation_is_told_to_use_the_days_it_already_has()
    {
        var source = Backend("GlunoSystemPrompt.cs");

        Assert.Contains("When the conversation IS scoped to an Adventure you already have its", source);
        Assert.Contains("do not mention opening anything", source);
    }

    [Fact]
    public void Inventing_a_button_is_still_forbidden_generally()
    {
        var source = Backend("GlunoSystemPrompt.cs");

        // The specific rule above sits on top of the general one; neither
        // replaces the other.
        Assert.Contains("Never invent a button, a menu name, a setting, a tab", source);
        Assert.Contains("answer from the capability registry — never from memory", source);
    }

    // ── 8. The capability registry matches the real app ──────────────────

    [Fact]
    public void The_registry_describes_both_real_Gluno_entry_points()
    {
        var gluno = SideQuestCapabilities.Find("gluno");

        Assert.NotNull(gluno);

        // The app header, and an Adventure's own header. Both exist; nothing
        // else is claimed.
        Assert.Contains("app header", gluno!.WhereEn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Adventure's header", gluno.WhereEn, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_registry_says_the_Adventure_entry_carries_the_Adventure()
    {
        var gluno = SideQuestCapabilities.Find("gluno");

        // This is the sentence that replaces "go and open it": the entry point
        // already knows which Adventure, so Gluno never has to ask.
        Assert.Contains("already knowing which Adventure", gluno!.WhereEn, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("redan valt", gluno.WhereSv, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_registry_entry_exists_in_both_languages()
    {
        var gluno = SideQuestCapabilities.Find("gluno");

        Assert.False(string.IsNullOrWhiteSpace(gluno!.WhereEn));
        Assert.False(string.IsNullOrWhiteSpace(gluno.WhereSv));
        Assert.NotEqual(gluno.WhereEn, gluno.WhereSv);
    }

    [Fact]
    public void The_registry_still_states_what_Gluno_cannot_do()
    {
        var gluno = SideQuestCapabilities.Find("gluno");

        // Booking, payment, wake word, microphone. Adding an entry point must
        // not quietly widen what the registry claims.
        Assert.Contains(gluno!.LimitationsEn, limitation =>
            limitation.Contains("cannot book", StringComparison.OrdinalIgnoreCase));
    }

    // ── 9. Both languages ────────────────────────────────────────────────

    [Fact]
    public void The_new_scope_strings_exist_in_English_and_Swedish()
    {
        var source = Translations();

        foreach (var key in new[] { "gluno.scope.checking", "gluno.scope.lost" })
        {
            // Twice: once in each dictionary. A key present only in `en`
            // renders English text inside a Swedish app.
            Assert.True(
                source.Split($"'{key}'").Length - 1 == 2,
                $"{key} is not defined in both dictionaries");
        }
    }

    [Fact]
    public void The_lost_access_message_names_a_real_way_forward()
    {
        var source = Translations();

        // It points at the app header, which exists. The whole bug this file
        // is about was a message pointing at something that did not.
        Assert.Contains("Open Gluno from the app header", source);
        Assert.Contains("Öppna Gluno från appens header", source);
    }

    [Fact]
    public void The_scope_labels_still_exist_in_both_languages()
    {
        var source = Translations();

        Assert.Equal(2, source.Split("'gluno.scope.global'").Length - 1);
        Assert.Equal(2, source.Split("'gluno.scope.adventure'").Length - 1);
    }

    // ── 10. Global Gluno is untouched ────────────────────────────────────

    [Fact]
    public void The_app_header_entry_still_opens_Gluno_globally()
    {
        var header = Mobile("components", "tab-header.tsx");

        // No tripId unless the header was given one, so the global entry stays
        // global.
        Assert.Contains("if (glunoTripId) query.set('tripId', glunoTripId);", header);
    }

    [Fact]
    public void A_global_conversation_is_still_reachable_and_separate()
    {
        var source = GlunoScreen();

        // tripId null means the global scope, and the lookup, the cache key
        // and the pill all follow from that one value.
        Assert.Contains(
            "const tripId = typeof params.tripId === 'string' && params.tripId.length > 0 ? params.tripId : null;",
            source);
    }
}
