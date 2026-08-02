using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for switching Adventure from the Gluno header.
///
/// THE PRODUCTION FAILURE. Tapping the Adventure name and choosing a different
/// one made the screen come apart.
///
/// THE PROVEN CAUSE, traced through the real control flow rather than guessed
/// at. Choosing an Adventure calls router.replace on the SAME route, so the
/// param changes and the screen does not remount — every useState initialiser
/// keeps the previous Adventure's values. The cache-mirror effect has tripId in
/// its dependency array and is declared BEFORE the loader, so on that first
/// render it fired with the new key and the old state, writing one Adventure's
/// entire message list under another Adventure's cache key. That entry carries
/// loaded: true, which made the loader skip the fetch — so the new Adventure
/// showed the old one's chat under its own name, and kept showing it.
///
/// It was not a double-trigger, a remount or a modal problem. It was a write
/// with a new key and stale state, and it corrupted the cache permanently.
///
/// THE FIX is one authoritative scope plus a render-phase reset that runs
/// before any effect can mirror anything.
///
/// These read the mobile source directly. There is no test runner in that
/// project, and a source assertion is the honest instrument available — it
/// proves the guard exists, not that the screen renders.
/// </summary>
public class ScopeSwitchEvals
{
    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string Screen() => Mobile("app", "gluno.tsx");
    private static string Picker() => Mobile("components", "gluno", "GlunoScopePicker.tsx");
    private static string Cache() => Mobile("lib", "gluno-cache.ts");

    // ── 1-4. The picker itself ───────────────────────────────────────────

    [Fact]
    public void The_pill_opens_the_sheet_once_per_press()
    {
        var picker = Picker();

        // One press handler, one state flag. No parent Pressable wrapping it
        // and no backdrop handler that could open it again.
        Assert.Equal(1, picker.Split("onPress={() => setOpen(true)}").Length - 1);
        Assert.Contains("const [open, setOpen] = useState(false);", picker);
    }

    [Fact]
    public void Choosing_closes_the_sheet_and_never_reopens_it()
    {
        var picker = Picker();

        var start = picker.IndexOf("const choose = useCallback(", StringComparison.Ordinal);
        var body = picker[start..(start + 500)];

        Assert.True(start > 0);
        Assert.Contains("setOpen(false);", body);
        // The only effect keyed on `open` loads the list; nothing sets it true.
        Assert.Equal(1, picker.Split("setOpen(true)").Length - 1);
    }

    [Fact]
    public void The_same_scope_tapped_again_changes_nothing()
    {
        var picker = Picker();

        // Re-selecting would discard an in-flight turn for no reason, which
        // reads as the app losing the answer.
        Assert.Contains("if (choice.tripId !== tripId) onChange(choice);", picker);
    }

    [Fact]
    public void The_adventure_list_is_fetched_once_and_not_in_a_loop()
    {
        var picker = Picker();

        // Guarded on both the sheet being open AND the list being unfetched, so
        // the effect cannot re-enter when its own setState re-renders.
        Assert.Contains("if (!open || trips !== null) return;", picker);
        Assert.Contains("}, [open, trips]);", picker);
        // And a cancelled flag, so a slow response cannot land after close.
        Assert.Contains("cancelled = true;", picker);
    }

    // ── 5-8. One authoritative scope ─────────────────────────────────────

    [Fact]
    public void Scope_is_one_derived_identity()
    {
        var screen = Screen();

        // adventure:{tripId} or all_adventures. Everything else derives from
        // it; before this the route param, the cache entry, the conversation's
        // own tripId and the header label were four competing truths that moved
        // at different times.
        Assert.Contains("const scope = tripId ? `adventure:${tripId}` : 'all_adventures';", screen);
    }

    [Fact]
    public void Identity_is_the_trip_id_and_never_the_name()
    {
        var screen = Screen();
        var picker = Picker();

        // Two Adventures can share a title; only the id separates them.
        Assert.Contains("`adventure:${tripId}`", screen);
        Assert.Contains("if (choice.tripId !== tripId) onChange(choice);", picker);
        Assert.DoesNotContain("choice.title !== tripTitle", picker);
    }

    [Fact]
    public void The_header_label_is_never_a_source_of_logic()
    {
        var screen = Screen();

        // tripName feeds the label and the cache mirror, and nothing branches
        // on it.
        Assert.DoesNotContain("if (tripName ===", screen);
        Assert.DoesNotContain("tripName ? ", screen);
    }

    [Fact]
    public void All_adventures_does_not_carry_a_trip_id()
    {
        var screen = Screen();

        // The param is omitted entirely rather than sent empty, so the route
        // and the scope agree on what global means.
        Assert.Contains("...(choice.tripId ? { tripId: choice.tripId } : {}),", screen);
    }

    // ── 9-13. The cause, pinned ──────────────────────────────────────────

    [Fact]
    public void Switching_scope_resets_the_state_before_any_effect_runs()
    {
        var screen = Screen();

        // THE FIX. Render-phase, so it happens before the cache mirror can
        // write — and before the old messages paint under the new name.
        Assert.Contains("if (stateScope.current !== scope) {", screen);
        Assert.Contains("stateScope.current = scope;", screen);

        foreach (var reset in new[]
        {
            "setConversationId(entry?.conversationId ?? null);",
            "setMessages(entry?.messages ?? []);",
            "setScopeVerified(Boolean(entry?.loaded));",
            "setScopeLost(false);",
            "setLoadFailed(false);",
        })
        {
            Assert.Contains(reset, screen);
        }
    }

    [Fact]
    public void The_cache_mirror_refuses_to_write_across_scopes()
    {
        var screen = Screen();

        var start = screen.IndexOf("writeGlunoCache(userId, tripId, {", StringComparison.Ordinal);
        var before = screen[Math.Max(0, start - 700)..start];

        // THE BUG: this effect has tripId in its deps, so it fired on the
        // switch with the new key and the previous Adventure's state.
        Assert.Contains("if (stateScope.current !== scope) return;", before);
    }

    [Fact]
    public void A_stale_load_cannot_land_in_the_new_scope()
    {
        var screen = Screen();

        // The loader's cleanup is keyed on tripId, so changing scope cancels
        // the previous fetch before its result can be applied.
        Assert.Contains("if (cancelled) return;", screen);
        Assert.Contains("}, [tripId]);", screen);
    }

    [Fact]
    public void An_in_flight_turn_is_aborted_when_the_scope_changes()
    {
        var screen = Screen();

        var start = screen.IndexOf("onChange={(choice) => {", StringComparison.Ordinal);
        var body = screen[start..(start + 700)];

        Assert.True(start > 0);
        Assert.Contains("abortRef.current?.abort();", body);
    }

    [Fact]
    public void A_pending_place_action_stays_in_the_scope_it_started_in()
    {
        var screen = Screen();

        // An add started in one Adventure must never append its answer to
        // another's list.
        Assert.Equal(2, screen.Split("const startedIn = stateScope.current;").Length - 1);
        Assert.Equal(2, screen.Split("if (stateScope.current !== startedIn) return;").Length - 1);
    }

    // ── 14-17. Cache keys ────────────────────────────────────────────────

    [Fact]
    public void Global_and_adventure_scopes_have_different_keys()
    {
        var cache = Cache();

        Assert.Contains("`${userId}:adventure:${trip}`", cache);
        Assert.Contains("`${userId}:all_adventures`", cache);
    }

    [Fact]
    public void One_scope_can_never_produce_two_keys()
    {
        var cache = Cache();

        // null, undefined and an empty string all mean "all Adventures". An
        // empty string is not nullish, so it used to key its own entry and the
        // scope quietly split in two.
        Assert.Contains("typeof tripId === 'string' ? tripId.trim() : ''", cache);
        Assert.Contains("trip.length > 0", cache);
        Assert.DoesNotContain("tripId ?? 'global'", cache);
    }

    [Fact]
    public void A_trip_called_global_cannot_collide_with_the_global_chat()
    {
        var cache = Cache();

        // The prefixes are what make that impossible.
        Assert.Contains(":adventure:", cache);
        Assert.Contains(":all_adventures", cache);
    }

    [Fact]
    public void History_deduplicates_on_message_id()
    {
        var cache = Cache();

        Assert.Contains("byId.set(message.id, message);", cache);
        Assert.Contains("for (const message of incoming)", cache);
    }

    // ── 18-19. Failure states ────────────────────────────────────────────

    [Fact]
    public void A_failed_adventure_fetch_leaves_the_sheet_open()
    {
        var picker = Picker();

        // The sheet's visibility is driven only by `open`, which a fetch
        // failure does not touch — so it stays put and shows a retry instead of
        // closing and reopening.
        Assert.Contains("setFailed(true)", picker);
        Assert.Contains("<ModalSheet visible={open}", picker);

        var start = picker.IndexOf(".catch(", StringComparison.Ordinal);
        var body = picker[start..(start + 200)];

        Assert.DoesNotContain("setOpen", body);
    }

    [Fact]
    public void The_failure_state_offers_a_way_forward()
    {
        var picker = Picker();
        var translations = Mobile("components", "i18n-provider.tsx");

        // Already correct before this round: a retry that clears the failure
        // and re-arms the fetch, without touching the sheet's visibility.
        Assert.Contains("setFailed(false);", picker);
        Assert.Contains("setTrips(null);", picker);
        Assert.Contains("t('gluno.error.retry')", picker);
        Assert.Contains("'gluno.error.retry'", translations);
    }

    // ── 20-22. The list ──────────────────────────────────────────────────

    [Fact]
    public void List_rows_are_keyed_on_the_trip_id()
    {
        var picker = Picker();

        // Two Adventures with the same name must stay two rows.
        Assert.Contains("key={trip.id}", picker);
    }

    [Fact]
    public void The_sheet_has_a_fixed_height_so_the_list_can_scroll()
    {
        var picker = Picker();

        // A ScrollView inside a content-sized parent collapses to nothing —
        // the sheet opened and the list was invisible.
        Assert.Contains("height={SHEET_HEIGHT}", picker);
        Assert.DoesNotContain("autoHeight={", picker);
    }

    [Fact]
    public void The_current_scope_is_marked_in_the_list()
    {
        var picker = Picker();

        Assert.Contains("selected={tripId === null}", picker);
        Assert.Contains("selected={tripId === trip.id}", picker);
    }

    // ── 23. Diagnostics stay internal ────────────────────────────────────

    [Fact]
    public void The_response_origin_is_never_rendered()
    {
        var row = Mobile("components", "gluno", "GlunoMessageRow.tsx");
        var screen = Screen();

        Assert.DoesNotContain("responseOrigin", row);
        // Development console only.
        Assert.Contains("turn.responseOrigin ?? 'none'", screen);
        Assert.Contains("if (__DEV__) {", screen);
    }
}
