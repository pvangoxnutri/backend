namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Which internal path produced a turn.
///
/// WHY THIS EXISTS. The same production sentence survived three fixes, and each
/// investigation started by guessing which code wrote it — model, history,
/// cache, or an idempotency replay of an older answer. Those four look
/// identical from the app, and the difference is the entire diagnosis.
///
/// A FIXED VOCABULARY AND NOTHING ELSE. No user text, no provider data, no
/// ids, no secrets — it names a branch, not a result. The app may log it in a
/// development build; it must never be rendered.
/// </summary>
public static class GlunoResponseOrigins
{
    /// The model wrote the answer.
    public const string ModelTurn = "model_turn";
    /// The deterministic add path built a proposal or a question.
    public const string PlaceAdd = "place_add";
    /// A retry of a failed add, resumed from server-owned ids.
    public const string PlaceAddRetry = "place_add_retry";
    /// A fresh shortlist from the stored search context.
    public const string PlaceRefresh = "place_refresh";
    /// A question with tappable answers.
    public const string Clarification = "clarification";
    /// A proposal card.
    public const string Proposal = "proposal";
    /// An earlier completed turn replayed for the same idempotency key.
    public const string IdempotencyReplay = "idempotency_replay";
    /// Answered without a model from data SideQuest already had.
    public const string Direct = "direct";

    public static readonly IReadOnlyList<string> All =
    [
        ModelTurn, PlaceAdd, PlaceAddRetry, PlaceRefresh,
        Clarification, Proposal, IdempotencyReplay, Direct,
    ];
}
