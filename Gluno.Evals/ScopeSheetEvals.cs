using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the scope sheet actually rendering its list.
///
/// THE BUG. Tapping "All Adventures" opened the sheet, the title rendered, and
/// the list was invisible. Not empty — invisible: the fetch ran, the rows were
/// built, and they had nowhere to appear.
///
/// The sheet was opened with `autoHeight`, which sizes it to its content. A
/// ScrollView inside a content-sized parent collapses to zero: the parent asks
/// the child how tall it wants to be, and a ScrollView answers "however much
/// you give me". With nothing given, that is nothing.
///
/// So the fix is a fixed height, which gives the ScrollView something to fill
/// instead of something to dictate.
///
/// These read the component source, which is the honest limit of what this
/// suite can do for a React Native layout — there is no test runner in that
/// repo. They check the specific mistake and the states around it rather than
/// claiming the list is visible.
/// </summary>
public class ScopeSheetEvals
{
    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }
            .Concat(parts).ToArray()));

    private static string Picker() => Mobile("components", "gluno", "GlunoScopePicker.tsx");

    // ── The layout mistake ───────────────────────────────────────────────

    [Fact]
    public void The_sheet_has_a_fixed_height_rather_than_sizing_to_content()
    {
        var picker = Picker();

        // autoHeight + ScrollView is the whole bug: a scroller inside a
        // content-sized parent collapses to zero.
        //
        // Matched on the PROP rather than the word — the comment above the
        // ModalSheet names autoHeight to explain why it is not used, and a
        // bare substring check would fail on the explanation.
        Assert.DoesNotContain("autoHeight>", picker);
        Assert.DoesNotContain("autoHeight={true}", picker);
        Assert.Contains("height={SHEET_HEIGHT}", picker);
    }

    [Fact]
    public void The_list_fills_the_sheet_rather_than_capping_itself()
    {
        var picker = Picker();

        var start = picker.IndexOf("sheetList: {", StringComparison.Ordinal);
        var body = picker[start..(start + 260)];

        Assert.True(start > 0);
        // A maxHeight on a scroller inside a content-sized parent still
        // measures as zero; flex against a fixed parent does not.
        Assert.Contains("flex: 1", body);
        Assert.DoesNotContain("maxHeight", body);
    }

    [Fact]
    public void The_sheet_is_tall_enough_for_several_Adventures()
    {
        var picker = Picker();

        var start = picker.IndexOf("const SHEET_HEIGHT =", StringComparison.Ordinal);
        var line = picker[start..(start + 40)];

        Assert.True(start > 0);
        // Enough for the "all" row plus several trips without becoming a
        // full-screen takeover.
        Assert.Matches(@"const SHEET_HEIGHT = [3-9]\d\d;", line);
    }

    [Fact]
    public void A_scroll_gesture_on_a_row_is_not_swallowed()
    {
        Assert.Contains("keyboardShouldPersistTaps=\"handled\"", Picker());
    }

    // ── The three states ─────────────────────────────────────────────────

    [Fact]
    public void The_fetch_runs_when_the_sheet_opens()
    {
        var picker = Picker();

        // Not on mount: the pill is on screen for every turn and most people
        // never tap it.
        Assert.Contains("if (!open || trips !== null) return;", picker);
        Assert.Contains("apiJson<Quest[]>('/api/trips')", picker);
    }

    [Fact]
    public void Loading_is_distinct_from_empty()
    {
        var picker = Picker();

        // An empty list shown while the fetch is still running reads as "you
        // have no Adventures", which is a different and alarming claim.
        Assert.Contains("trips === null && !failed ?", picker);
        Assert.Contains("<ActivityIndicator", picker);
    }

    [Fact]
    public void Empty_shows_only_when_the_API_returned_nothing()
    {
        var picker = Picker();

        Assert.Contains("trips !== null && ordered.length === 0 ?", picker);
        Assert.Contains("t('gluno.scope.empty')", picker);
    }

    [Fact]
    public void A_failed_fetch_offers_a_retry_rather_than_an_empty_list()
    {
        var picker = Picker();

        // The difference between "you have none" and "I couldn't ask" matters
        // to somebody who knows they have four.
        Assert.Contains("{failed ? (", picker);
        Assert.Contains("setFailed(false);", picker);
        Assert.Contains("setTrips(null);", picker);
    }

    [Fact]
    public void The_fetch_error_is_not_swallowed_silently()
    {
        var picker = Picker();

        var start = picker.IndexOf(".catch(() => {", StringComparison.Ordinal);
        var body = picker[start..(start + 160)];

        Assert.True(start > 0);
        Assert.Contains("setFailed(true)", body);
    }

    // ── What the list shows ──────────────────────────────────────────────

    [Fact]
    public void All_Adventures_is_always_first()
    {
        var picker = Picker();

        var allAt = picker.IndexOf("t('gluno.scope.global')", StringComparison.Ordinal);
        var listAt = picker.IndexOf("ordered.map((trip)", StringComparison.Ordinal);

        Assert.True(allAt > 0 && listAt > 0 && allAt < listAt);
    }

    [Fact]
    public void Ongoing_comes_before_upcoming_before_past()
    {
        var picker = Picker();

        var start = picker.IndexOf("const rank = (trip: Quest)", StringComparison.Ordinal);
        var body = picker[start..(start + 420)];

        Assert.True(start > 0);
        Assert.Contains("return 0;", body);
        Assert.Contains("return start > today ? 1 : 2;", body);
    }

    [Fact]
    public void No_internal_id_is_rendered()
    {
        var picker = Picker();

        var start = picker.IndexOf("function describe(trip: Quest)", StringComparison.Ordinal);
        var body = picker[start..(start + 600)];

        Assert.True(start > 0);
        Assert.DoesNotContain("trip.id", body);
    }

    [Fact]
    public void The_selected_scope_is_marked()
    {
        var picker = Picker();

        Assert.Contains("selected={tripId === null}", picker);
        Assert.Contains("selected={tripId === trip.id}", picker);
        Assert.Contains("name=\"checkmark\"", picker);
    }

    // ── The removed button ───────────────────────────────────────────────

    [Fact]
    public void The_preferences_door_is_gone_from_the_chat_header()
    {
        var screen = Mobile("app", "gluno.tsx");

        Assert.DoesNotContain("gluno.knows.entry", screen);
        Assert.DoesNotContain("pathname: '/gluno-preferences'", screen);
    }

    [Fact]
    public void Its_label_is_gone_from_both_dictionaries()
    {
        var translations = Mobile("components", "i18n-provider.tsx");

        // The BUTTON's label. `gluno.knows.title` is a different key — the
        // preferences screen's own heading — and it stays, because that screen
        // stays.
        Assert.DoesNotContain("'gluno.knows.entry'", translations);
    }

    [Fact]
    public void The_preferences_screen_keeps_its_own_heading()
    {
        var translations = Mobile("components", "i18n-provider.tsx");
        var screen = Mobile("app", "gluno-preferences.tsx");

        // Deleting this would leave the screen with no title. Only the way in
        // from the chat was removed.
        Assert.Contains("'gluno.knows.title'", translations);
        Assert.Contains("t('gluno.knows.title')", screen);
    }

    [Fact]
    public void The_preferences_screen_and_its_other_copy_survive()
    {
        var translations = Mobile("components", "i18n-provider.tsx");

        // Only the visible entry point goes. The screen behind it, the
        // preference rows and the backend's memory features are untouched.
        Assert.Contains("'gluno.knows.forget'", translations);
        Assert.Contains("'gluno.knows.share'", translations);
    }

    [Fact]
    public void The_header_still_carries_the_scope_picker()
    {
        var screen = Mobile("app", "gluno.tsx");

        // Removing the door must not have taken the picker with it.
        Assert.Contains("<GlunoScopePicker", screen);
    }
}
