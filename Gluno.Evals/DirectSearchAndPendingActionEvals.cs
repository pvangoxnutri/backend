using sidequest.backend.Services.Gluno;
using Xunit;

namespace Gluno.Evals;

/// <summary>
/// Evals for the three control-flow fixes proven by the production debug
/// export of 2026-08-02 (ScopeKey: all_adventures).
///
/// WHAT THE EXPORT SHOWED. "Platser i Sevilla" became free model prose with no
/// structured places and no option keys; "Skapa real aöcazar" got advice to
/// type "lägg till" later; "Ja det blir bra" after "Vill du ha den som
/// dagsplan?" went back to the model, which refused because the conversation
/// was not scoped to an Adventure.
///
/// THE THREE FIXES PINNED HERE: the model-free place list
/// (GlunoDirectPlaceSearch), the deterministic name recovery
/// (GlunoPlaceNameRecovery + RecoverNamedPlaceAsync), and the server-owned
/// pending action state (GlunoPendingAction + the follow-up resolver).
///
/// Behavioural tests run the REAL classifiers with the REAL production
/// messages. Source assertions cover the service wiring, which has no test
/// harness of its own — they prove the call exists, not that it runs, and are
/// labelled as such.
/// </summary>
public class DirectSearchAndPendingActionEvals
{
    private static string Source(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", ".." }.Concat(parts).ToArray()));

    private static string Mobile(params string[] parts) => File.ReadAllText(Path.Combine(
        new[] { AppContext.BaseDirectory, "..", "..", "..", "..", "..", "mobile" }.Concat(parts).ToArray()));

    private static string ChatService() => Source("Services", "Gluno", "GlunoChatService.cs");

    /// The body of one method in the chat service, sliced between its opening
    /// declaration and the next method declaration, so an assertion about one
    /// path cannot accidentally pass on code from another.
    private static string ServiceMethod(string declaration)
    {
        var source = ChatService();
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"method not found: {declaration}");

        var end = source.IndexOf("\n    private ", start + declaration.Length, StringComparison.Ordinal);
        var endPublic = source.IndexOf("\n    public ", start + declaration.Length, StringComparison.Ordinal);
        if (endPublic >= 0 && (end < 0 || endPublic < end)) end = endPublic;

        return end > start ? source[start..end] : source[start..];
    }

    // ── 1–4. The pure place question goes model-free ─────────────────────

    [Fact]
    public void Platser_i_Sevilla_is_recognised_as_a_pure_place_question()
    {
        // THE PRODUCTION MESSAGE, verbatim. The intent router classified it
        // Unclear — none of its category markers match — which is exactly why
        // the direct path gates on the text, not on the router.
        var query = GlunoDirectPlaceSearch.Parse("Platser i Sevilla");

        Assert.NotNull(query);
        Assert.Equal("Sevilla", query!.Destination);
    }

    [Fact]
    public void Vad_borde_vi_se_i_Sevilla_is_recognised_too()
    {
        var query = GlunoDirectPlaceSearch.Parse("Vad borde vi se i Sevilla?");

        Assert.NotNull(query);
        Assert.Equal("Sevilla", query!.Destination);
    }

    [Fact]
    public void The_other_stated_examples_parse_with_their_destinations()
    {
        Assert.Equal("Sevilla", GlunoDirectPlaceSearch.Parse("Visa sevärdheter i Sevilla")?.Destination);
        Assert.Equal("Ronda", GlunoDirectPlaceSearch.Parse("Vad ska vi göra i Ronda?")?.Destination);
    }

    [Fact]
    public void Complex_and_change_questions_still_belong_to_the_model()
    {
        // Comparison, judgement, planning and adding are not lists.
        Assert.Null(GlunoDirectPlaceSearch.Parse("Jämför Sevilla och Ronda"));
        Assert.Null(GlunoDirectPlaceSearch.Parse("Vilken av restaurangerna passar oss bäst?"));
        Assert.Null(GlunoDirectPlaceSearch.Parse("Lägg till Casa de Pilatos"));
        Assert.Null(GlunoDirectPlaceSearch.Parse("Planera lördagen i Sevilla"));
        Assert.Null(GlunoDirectPlaceSearch.Parse(
            "Vi är två vuxna och två barn som gillar konst men ogillar folkmassor, " +
            "vad borde vi se i Sevilla och i vilken ordning?"));
    }

    [Fact]
    public void The_direct_search_makes_exactly_one_provider_call_and_zero_model_calls()
    {
        // The search core moved into RunDirectPlaceSearchAsync when the
        // discovery follow-ups arrived; the invariant did not.
        var resolver = ServiceMethod("private async Task<GlunoTurnResult?> DirectPlaceSearchAsync(");
        var core = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchCoreAsync(");

        // Exactly one search call in the whole path — the resolver never
        // searches, the core searches once.
        Assert.Equal(0, resolver.Split("SearchAllAsync").Length - 1);
        Assert.Equal(1, core.Split("SearchAllAsync").Length - 1);

        // And no model anywhere in either.
        Assert.DoesNotContain("_ai.", resolver);
        Assert.DoesNotContain("_ai.", core);
        Assert.Contains("telemetry.ModelSkipped = true;", core);

        // No parallel legacy call is possible either: the registry keeps one
        // provider per id namespace, and Terra outranks the Content API.
        var registry = Source("Services", "Gluno", "TravelDataRegistry.cs");
        Assert.Contains(".GroupBy(provider => provider.Provider, StringComparer.Ordinal)", registry);
    }

    [Fact]
    public void The_direct_search_runs_before_the_turn_plan_and_the_model()
    {
        var source = ChatService();

        var direct = source.IndexOf("GlunoDirectPlaceSearch.Parse(text)", StringComparison.Ordinal);
        var plan = source.IndexOf("_planner.Build(", StringComparison.Ordinal);
        var model = source.IndexOf("_ai.RunTurnAsync(", StringComparison.Ordinal);

        Assert.True(direct > 0 && plan > 0 && model > 0);
        Assert.True(direct < plan, "the direct search must run before the turn plan");
        Assert.True(direct < model, "the direct search must run before the model");
    }

    // ── 5–7. Structured places, real keys, retention rules ───────────────

    [Fact]
    public void The_direct_answer_carries_structured_places_and_the_ordinary_retention()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchCoreAsync(");

        // SideQuest's ranking, the per-turn cap, the sanitiser.
        Assert.Contains("TravelPlaceRanker.Rank(fresh, query)", method);
        Assert.Contains(".Take(Math.Min(limit, MaxPlaceCardsPerTurn))", method);
        Assert.Contains("SanitizePlace(", method);

        // The same retention decision as every other place turn — cards or
        // identity-only references, never a third rule for this path.
        Assert.Contains("GlunoPlaceRetention.Decide(places, search)", method);
        Assert.Contains("Places = retention.Places.ToList()", method);
        Assert.Contains("PlaceRefs = retention.References.ToList()", method);
        Assert.Contains("Places = places,", method);

        // A neutral stored heading when content may not be kept.
        Assert.Contains("retention.Reduced ? GlunoNeutralText.PlaceAnswer(language) : liveText", method);
    }

    [Fact]
    public void Real_option_keys_come_from_the_same_generator_as_every_other_list()
    {
        // The references the direct search stores carry the shared positional
        // key — the add endpoint resolves the same "place-N" it always has.
        var retention = Source("Services", "Gluno", "GlunoPlaceRetention.cs");
        Assert.Contains("OptionKey = GlunoPlaceOptions.KeyFor(index)", retention);

        Assert.Equal("place-3", GlunoPlaceOptions.KeyFor(3));
        Assert.Equal(3, GlunoPlaceOptions.IndexOf("place-3"));
    }

    [Fact]
    public void A_provider_failure_returns_the_structured_error_contract()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchCoreAsync(");

        Assert.Contains("Error = GlunoTurnError.ProviderFailed", method);
        Assert.Contains("FailureCode = GlunoFailureCodes.TripadvisorUnavailable", method);

        // And that code is honest about retrying.
        Assert.True(GlunoFailureCodes.IsRetryable(GlunoFailureCodes.TripadvisorUnavailable));
    }

    // ── 8–10. Global scope never blocks an action ────────────────────────

    [Fact]
    public void A_global_add_asks_which_Adventure_with_tappable_rows()
    {
        // The add flow's global branch builds the Adventure clarification —
        // never a sentence about switching scope.
        var method = ServiceMethod("private async Task<GlunoTurnResult> AddResolvedPlaceAsync(");

        Assert.Contains("if (tripId == null)", method);
        Assert.Contains("AskWhichAdventureAsync(", method);
    }

    [Fact]
    public void A_global_day_plan_resume_asks_which_Adventure_with_tappable_rows()
    {
        var method = ServiceMethod("private async Task<PendingActionResume> ResumePendingActionAsync(");

        Assert.Contains("AskWhichAdventureAsync(", method);
        // One obvious match resolves silently under the existing safety rule.
        Assert.Contains("GlunoClarificationBuilder.ResolveSingle(", method);
        // Membership is checked NOW, not assumed from the offer.
        Assert.Contains("_db.TripMembers.AnyAsync(", method);
    }

    [Fact]
    public void The_actions_Adventure_choice_never_mutates_the_conversations_scope()
    {
        var method = ServiceMethod("private async Task<PendingActionResume> ResumePendingActionAsync(");

        // The resolution lands on the ACTION.
        Assert.Contains("action.AdventureId = tripId;", method);
        // And nothing in the resolver writes the conversation's own scope.
        Assert.DoesNotContain("conversation.TripId =", method);
    }

    // ── 11, 22–23. Promises must be true ─────────────────────────────────

    [Fact]
    public void The_day_plan_offer_sentence_is_recognised_as_an_offer()
    {
        Assert.True(GlunoActionOffer.ContainsOffer("Vill du ha den som dagsplan?"));
        Assert.True(GlunoActionOffer.IsDayPlanOffer("Vill du ha den som dagsplan?"));

        Assert.True(GlunoActionOffer.ContainsOffer("Vill du att jag lägger in det?"));
        Assert.True(GlunoActionOffer.ContainsOffer("Säg till så gör jag ett förslag."));
        Assert.True(GlunoActionOffer.ContainsOffer("Would you like me to set that up?"));

        // Advice is not an offer — stripping it would maim ordinary answers.
        Assert.False(GlunoActionOffer.ContainsOffer(
            "Real Alcázar är som bäst tidig morgon, och ni kan gå dit från hotellet."));
    }

    [Fact]
    public void An_offer_the_turn_cannot_back_is_removed_in_response_building()
    {
        var source = ChatService();

        // The check sits in response building, after the model — not in the
        // prompt — and either backs the offer with a pending action or strips
        // the sentence.
        Assert.Contains("GlunoActionOffer.ContainsOffer(assistantText)", source);
        Assert.Contains("GlunoActionOffer.IsDayPlanOffer(assistantText)", source);
        Assert.Contains("assistantText = GlunoActionOffer.Strip(assistantText, context.User.Language);", source);

        // Only a turn that already produced a proposal may keep an offer
        // without creating state.
        Assert.Contains("proposals.Count == 0 && GlunoActionOffer.ContainsOffer(assistantText)", source);

        // And the backed offer becomes a REAL pending action, from
        // server-derived facts only.
        Assert.Contains("Type = GlunoPendingActionTypes.PlanDay,", source);
        Assert.Contains("OriginMessageId = assistantMessage.Id,", source);
        Assert.Contains("AdventureId = resolvedTripId,", source);
    }

    [Fact]
    public void The_stripper_removes_the_offer_sentence_and_keeps_the_answer()
    {
        var stripped = GlunoActionOffer.Strip(
            "Sevilla är fantastiskt i augusti. Vill du ha den som dagsplan?", "sv");

        Assert.Equal("Sevilla är fantastiskt i augusti.", stripped);

        // An answer that was ONLY the offer becomes a short neutral line, not
        // an empty bubble.
        var replaced = GlunoActionOffer.Strip("Vill du ha den som dagsplan?", "sv");
        Assert.False(string.IsNullOrWhiteSpace(replaced));
        Assert.False(GlunoActionOffer.ContainsOffer(replaced));
    }

    [Fact]
    public void The_production_scope_refusal_sentence_is_blocked()
    {
        // Verbatim from the debug export. Caught as plumbing-talk, which the
        // response builder replaces with the Adventure question.
        Assert.True(GlunoUiPromise.ExplainsItsOwnPlumbing(
            "Förslag som ändrar planen kan jag bara förbereda när samtalet ligger på " +
            "Semester 2026 — och just nu gör det inte det."));

        Assert.True(GlunoUiPromise.ExplainsItsOwnPlumbing("Byt till Adventuret så fixar jag det."));
        Assert.True(GlunoUiPromise.ExplainsItsOwnPlumbing(
            "I can only prepare that when the conversation is on the Adventure — switch to the Adventure first."));

        // An ordinary answer about the trip is untouched.
        Assert.False(GlunoUiPromise.ExplainsItsOwnPlumbing(
            "Semester 2026 har tre lediga dagar i Sevilla."));
    }

    // ── 12–15. The follow-up resolver ────────────────────────────────────

    [Fact]
    public void The_production_yes_is_resumptive()
    {
        // Verbatim from the debug export.
        Assert.True(GlunoFollowUps.IsResumptive("Ja det blir bra"));
    }

    [Fact]
    public void The_short_confirmations_are_resumptive_and_the_traps_are_not()
    {
        foreach (var yes in new[] { "Ja", "Ja tack", "Det blir bra", "Gör det", "Kör", "Nu", "Nudå?", "Lägg in det" })
        {
            Assert.True(GlunoFollowUps.IsResumptive(yes), $"should resume: {yes}");
        }

        // A question is a question, a no is a no, and a new request is a new
        // request — none of them may resume an old offer.
        foreach (var not in new[]
        {
            "Nej", "Nej tack", "Ja, vad kostar det?", "Hur långt är det dit?",
            "Ja fast kan vi byta hotell och äta middag tidigt imorgon istället",
        })
        {
            Assert.False(GlunoFollowUps.IsResumptive(not), $"must not resume: {not}");
        }
    }

    [Fact]
    public void A_yes_checks_the_pending_action_before_everything_else()
    {
        var source = ChatService();

        var resolver = source.IndexOf("GlunoFollowUps.IsResumptive(text)", StringComparison.Ordinal);
        var detector = source.IndexOf("GlunoClarificationDetector.Detect(new GlunoDetectionInput", StringComparison.Ordinal);
        var addBlock = source.IndexOf("GlunoPlaceOptions.IsAddRequest(text)", StringComparison.Ordinal);
        var plan = source.IndexOf("_planner.Build(", StringComparison.Ordinal);
        var model = source.IndexOf("_ai.RunTurnAsync(", StringComparison.Ordinal);

        Assert.True(resolver > 0);
        // Before ordinary routing in every form it takes.
        Assert.True(resolver < detector, "the resolver must run before the clarification detector");
        Assert.True(resolver < addBlock, "the resolver must run before the add block");
        Assert.True(resolver < plan, "the resolver must run before the turn plan");
        Assert.True(resolver < model, "the resolver must run before the model");

        // And the priority order inside it: pending action, then the open
        // clarification / waiting proposal fallback.
        Assert.Contains("GlunoPendingActions.Usable(", source);
        Assert.Contains("ReshowPendingWorkAsync(", source);
    }

    [Fact]
    public void An_expired_action_no_longer_captures_a_yes()
    {
        var action = new GlunoPendingAction
        {
            Type = GlunoPendingActionTypes.PlanDay,
            ExpiresAtUtc = new DateTime(2026, 8, 2, 12, 0, 0, DateTimeKind.Utc),
        };

        Assert.NotNull(GlunoPendingActions.Usable(action, new DateTime(2026, 8, 2, 11, 59, 0, DateTimeKind.Utc)));
        Assert.Null(GlunoPendingActions.Usable(action, new DateTime(2026, 8, 2, 12, 1, 0, DateTimeKind.Utc)));
        Assert.Null(GlunoPendingActions.Usable(null, DateTime.UtcNow));
    }

    [Fact]
    public void Nuda_after_a_retryable_failure_resumes_the_retry_not_the_model()
    {
        // The failure writes the action's durable twin…
        var stopped = ServiceMethod("private async Task<GlunoTurnResult> PlaceAddStoppedAsync(");
        Assert.Contains("state.PendingAction = action == null", stopped);
        Assert.Contains("Type = action.Type,", stopped);
        Assert.Contains("IdempotencyKey = action.IdempotencyKey,", stopped);

        // …and the resolver resumes it from ids alone: same message, same
        // card, same idempotency key, no model anywhere in the method.
        var resume = ServiceMethod("private async Task<PendingActionResume> ResumePendingActionAsync(");
        Assert.Contains("case GlunoPendingActionTypes.RetryPlaceAdd", resume);
        Assert.Contains("AddRecommendedPlaceAsync(", resume);
        Assert.Contains("case GlunoPendingActionTypes.ShowNewPlaceSuggestions", resume);
        Assert.Contains("RefreshPlaceSuggestionsAsync(", resume);
        Assert.DoesNotContain("_ai.", resume);
    }

    [Fact]
    public void The_day_plan_resume_walks_adventure_then_day_then_the_pipeline()
    {
        var resume = ServiceMethod("private async Task<PendingActionResume> ResumePendingActionAsync(");

        Assert.Contains("case GlunoPendingActionTypes.PlanDay", resume);
        // The day question replays the same yes with the day attached.
        Assert.Contains("AskPlanDayAsync(", resume);
        // A settled resume dispatches a STATED intent — the model executes,
        // it does not reinterpret.
        Assert.Contains("PlanDayIntent(date)", resume);

        var intent = ChatService();
        Assert.Contains("PrimaryIntent = GlunoIntent.PlanEmptyDay,", intent);
    }

    // ── 16–21. Deterministic recovery for a bare name ────────────────────

    [Fact]
    public void Skapa_is_an_add_request_now()
    {
        // "Skapa real aöcazar" is the production message. AddWords did not
        // contain "skapa", so the whole add path was skipped and the model
        // answered with advice about typing "lägg till".
        Assert.True(GlunoPlaceOptions.IsAddRequest("Skapa Real Alcázar"));
        Assert.True(GlunoPlaceOptions.IsAddRequest("Skapa real aöcazar"));
        Assert.True(GlunoPlaceOptions.IsAddRequest("Create Real Alcázar"));
    }

    [Fact]
    public void The_name_candidate_is_extracted_from_the_production_message()
    {
        // Lower case and misspelled, exactly as typed. The words are a QUERY
        // for the provider — never an identity.
        Assert.Equal("real aöcazar", GlunoPlaceNameRecovery.ExtractCandidate("Skapa real aöcazar"));
        Assert.Equal("Real Alcázar", GlunoPlaceNameRecovery.ExtractCandidate("Skapa Real Alcázar"));
        Assert.Equal("Casa de Pilatos", GlunoPlaceNameRecovery.ExtractCandidate("Lägg till Casa de Pilatos"));
    }

    [Fact]
    public void Itinerary_requests_produce_no_candidate_and_stay_with_the_model()
    {
        Assert.Null(GlunoPlaceNameRecovery.ExtractCandidate("Lägg till en vilodag"));
        Assert.Null(GlunoPlaceNameRecovery.ExtractCandidate("Boka in middag på torsdag"));
        Assert.Null(GlunoPlaceNameRecovery.ExtractCandidate("Lägg till den där"));
        Assert.Null(GlunoPlaceNameRecovery.ExtractCandidate("Lägg till den andra"));
        Assert.Null(GlunoPlaceNameRecovery.ExtractCandidate("Hur mycket kostar det?"));
    }

    [Fact]
    public void A_named_day_survives_extraction_and_into_the_add_flow()
    {
        // The day is the add flow's business, not the search's.
        Assert.Equal("Casa de Pilatos",
            GlunoPlaceNameRecovery.ExtractCandidate("Lägg till Casa de Pilatos på torsdag"));

        // And the recovery hands the router's resolved date through.
        var recovery = ServiceMethod("private async Task<GlunoTurnResult?> RecoverNamedPlaceAsync(");
        Assert.Contains("ParseIsoDate(intent.ReferencedDate)", recovery);
    }

    [Fact]
    public void The_recovery_searches_the_provider_and_never_the_model()
    {
        var recovery = ServiceMethod("private async Task<GlunoTurnResult?> RecoverNamedPlaceAsync(");

        Assert.Contains("SearchAllAsync(query, ct)", recovery);
        Assert.DoesNotContain("_ai.", recovery);

        // And it runs inside the add block, before the model could ever see
        // the message.
        var source = ChatService();
        var recoveryCall = source.IndexOf("RecoverNamedPlaceAsync(\n", StringComparison.Ordinal);
        if (recoveryCall < 0) recoveryCall = source.IndexOf("await RecoverNamedPlaceAsync(", StringComparison.Ordinal);
        var model = source.IndexOf("_ai.RunTurnAsync(", StringComparison.Ordinal);
        Assert.True(recoveryCall > 0 && recoveryCall < model);
    }

    [Fact]
    public void One_verified_match_continues_several_ask_and_none_shows_the_list()
    {
        var recovery = ServiceMethod("private async Task<GlunoTurnResult?> RecoverNamedPlaceAsync(");

        // The SAME matcher as the shown-list path, with ordinals off — they
        // are meaningless against a list the user has not seen.
        Assert.Contains("GlunoPlaceOptions.Match(places, text, allowOrdinals: false)", recovery);

        Assert.Contains("if (matches.Count == 1)", recovery);
        Assert.Contains("AddResolvedPlaceAsync(", recovery);

        Assert.Contains("if (matches.Count > 1)", recovery);
        Assert.Contains("AskWhichPlaceAsync(", recovery);

        // No match: the verified list is the answer — structured suggestions,
        // never a model apology. Provider failures use the fixed sentences.
        Assert.Contains("GlunoNeutralText.NoExactMatch(near, language)", recovery);
        Assert.Contains("GlunoPlaceFailureText.For(", recovery);
    }

    [Fact]
    public void The_shortlist_is_persisted_before_anything_acts_on_it()
    {
        var recovery = ServiceMethod("private async Task<GlunoTurnResult?> RecoverNamedPlaceAsync(");

        // Retention first, then the stored turn, then the match — so the day
        // question, the proposal identity and any retry hang off real stored
        // references like every other list.
        var retention = recovery.IndexOf("GlunoPlaceRetention.Decide(places, search)", StringComparison.Ordinal);
        var persisted = recovery.IndexOf("var listMessage = await _conversations.AppendAsync(", StringComparison.Ordinal);
        var match = recovery.IndexOf("GlunoPlaceOptions.Match(places, text", StringComparison.Ordinal);

        Assert.True(retention > 0 && persisted > retention && match > persisted);
    }

    [Fact]
    public void Model_text_is_never_used_as_a_place_identity()
    {
        var recovery = ServiceMethod("private async Task<GlunoTurnResult?> RecoverNamedPlaceAsync(");

        // The recovery never reads the conversation's messages at all — the
        // candidate words came from the user's own sentence, and the identity
        // comes from the provider result that matched them.
        Assert.DoesNotContain("GlunoMessages", recovery);
        Assert.DoesNotContain("GetHistoryTurnsAsync", recovery);

        // The proposal is built from the verified card, exact id equality —
        // the existing add flow's own belt and braces.
        var addFromKey = ServiceMethod("private async Task<GlunoTurnResult> AddPlaceFromKeyAsync(");
        Assert.Contains("fresh.ProviderPlaceId, reference.LocationId", addFromKey);
    }

    // ── 24–27. responseOrigin ────────────────────────────────────────────

    [Fact]
    public void The_new_origins_exist_and_are_fixed_vocabulary()
    {
        Assert.Equal("direct_place_search", GlunoResponseOrigins.DirectPlaceSearch);
        Assert.Equal("adventure_clarification", GlunoResponseOrigins.AdventureClarification);
        Assert.Equal("pending_action_resume", GlunoResponseOrigins.PendingActionResume);

        Assert.Contains(GlunoResponseOrigins.DirectPlaceSearch, GlunoResponseOrigins.All);
        Assert.Contains(GlunoResponseOrigins.AdventureClarification, GlunoResponseOrigins.All);
        Assert.Contains(GlunoResponseOrigins.PendingActionResume, GlunoResponseOrigins.All);
    }

    [Fact]
    public void Direct_place_search_stamps_its_origin_on_message_and_result()
    {
        // The origin travels as a parameter now — the same core serves the
        // fresh search and the follow-up, each under its own name.
        var resolver = ServiceMethod("private async Task<GlunoTurnResult?> DirectPlaceSearchAsync(");
        Assert.Contains("origin: GlunoResponseOrigins.DirectPlaceSearch", resolver);

        var core = ServiceMethod("private async Task<GlunoTurnResult> RunDirectPlaceSearchCoreAsync(");
        // Stored row and live result both carry whichever origin ran.
        Assert.Contains("ResponseOrigin = origin,", core);
        Assert.True(core.Split("ResponseOrigin = origin,").Length - 1 >= 3);
    }

    [Fact]
    public void A_resumed_action_reports_pending_action_resume()
    {
        var source = ChatService();

        // The dispatch path overrides the model turn's own origin…
        Assert.Contains("responseOriginOverride = GlunoResponseOrigins.PendingActionResume;", source);
        Assert.Contains("ResponseOrigin = responseOriginOverride ?? GlunoResponseOrigins.ModelTurn,", source);

        // …and the direct resume paths stamp their results.
        var resume = ServiceMethod("private async Task<PendingActionResume> ResumePendingActionAsync(");
        Assert.Contains("result.ResponseOrigin = GlunoResponseOrigins.PendingActionResume;", resume);
    }

    [Fact]
    public void The_adventure_question_reports_adventure_clarification()
    {
        var method = ServiceMethod("private async Task<GlunoTurnResult> AskWhichAdventureAsync(");

        Assert.Contains("ResponseOrigin = GlunoResponseOrigins.AdventureClarification,", method);
    }

    [Fact]
    public void The_origin_is_persisted_and_survives_into_history()
    {
        // The column on the row…
        var model = Source("Models", "GlunoMessage.cs");
        Assert.Contains("public string? ResponseOrigin { get; set; }", model);

        // …the additive migration…
        var migrations = Directory.GetFiles(
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Migrations"),
            "*GlunoMessageResponseOrigin.cs");
        Assert.Single(migrations);
        var migration = File.ReadAllText(migrations[0]);
        Assert.Contains("AddColumn<string>", migration);
        Assert.Contains("nullable: true", migration);

        // …the DTO the history endpoint returns…
        var controller = Source("Controllers", "GlunoController.cs");
        Assert.Contains("ResponseOrigin = m.ResponseOrigin,", controller);

        // …and the app keeps it when mapping history rows.
        var screen = Mobile("app", "gluno.tsx");
        Assert.Contains("responseOrigin: message.responseOrigin ?? undefined,", screen);
    }

    [Fact]
    public void The_model_turn_and_the_deterministic_paths_stamp_their_rows()
    {
        var source = ChatService();

        Assert.Contains("ResponseOrigin = responseOriginOverride ?? GlunoResponseOrigins.ModelTurn,", source);
        Assert.Contains("ResponseOrigin = GlunoResponseOrigins.PlaceRefresh,", source);
        Assert.Contains("ResponseOrigin = GlunoResponseOrigins.Proposal,", source);
        Assert.Contains("ResponseOrigin = GlunoResponseOrigins.Clarification,", source);
        Assert.Contains("ResponseOrigin = GlunoResponseOrigins.Direct,", source);
    }

    [Fact]
    public void Transport_source_and_response_origin_are_kept_apart_in_the_export()
    {
        var export = Mobile("lib", "gluno-debug-export.ts");

        // source() answers WHICH TRANSPORT delivered the row — the live flag,
        // never the origin. Reading origin as liveness would relabel every
        // reloaded row as a live response now that history carries origin.
        //
        // The live flag WINS over the local row id: a failed turn keeps its
        // optimistic id, but when a server response actually arrived the
        // transport fact is live_response — that is what separates "the
        // backend answered with an error" from "no answer ever came".
        Assert.Contains("if (message.live) return 'live_response';", export);
        Assert.Contains("if (message.id.startsWith('local-')) return 'local_optimistic';", export);
        Assert.Contains("return 'history_or_cache';", export);
        Assert.DoesNotContain("message.responseOrigin ? 'live_response'", export);

        // The origin is still printed, separately.
        Assert.Contains("responseOrigin=${message.responseOrigin}", export);

        // And the live flag is only ever set by the handlers that applied a
        // turn response in this session.
        var screen = Mobile("app", "gluno.tsx");
        Assert.Contains("function toChatMessages(messages: GlunoApiMessage[], live = false)", screen);
        Assert.Contains("toChatMessages([turn.userMessage, turn.assistantMessage], true)", screen);
    }

    // ── The pending action's own shape ───────────────────────────────────

    [Fact]
    public void The_pending_action_state_carries_what_the_spec_requires()
    {
        var state = Source("Services", "Gluno", "GlunoPendingAction.cs");

        // actionType, originMessageId, resolved destination, resolved
        // Adventure, day, place reference, expiry. conversationId is the
        // working-state row's own key.
        foreach (var field in new[]
        {
            "public string Type", "public Guid? OriginMessageId",
            "public Guid? AdventureId", "public string? Destination",
            "public string? Date", "public string? OptionKey",
            "public string? IdempotencyKey", "public DateTime ExpiresAtUtc",
        })
        {
            Assert.Contains(field, state);
        }

        // Stored on the conversation's working state — server-owned, and
        // additive so old rows read back as "nothing pending".
        var working = Source("Services", "Gluno", "GlunoWorkingState.cs");
        Assert.Contains("public GlunoPendingAction? PendingAction { get; set; }", working);
    }

    [Fact]
    public void An_ordinary_model_answer_ends_any_previous_offer()
    {
        var source = ChatService();

        // A new full model turn supersedes the offer — a stale yes three
        // questions later must not resume it.
        Assert.Contains("workingState.PendingAction = newPendingAction;", source);
        Assert.Contains("if (newPendingAction != null || workingState.PendingAction != null)", source);
    }
}
