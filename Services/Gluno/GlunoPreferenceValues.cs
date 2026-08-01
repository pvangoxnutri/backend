using System.Globalization;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// How each preference key may be edited, and what counts as a valid value.
///
/// WHY THIS EXISTS SEPARATELY FROM THE KEY ALLOW-LIST. The key list stops the
/// store holding arbitrary FIELDS. This stops it holding arbitrary VALUES in
/// the fields it does allow — which matters the moment a settings screen lets
/// somebody type into one. Without it, "pace" can end up holding a paragraph,
/// a JSON fragment, or an instruction aimed at the model, and every one of
/// those reaches a prompt.
///
/// TOLERANT ON READ, STRICT ON WRITE. Rows already in the database were
/// written by Gluno from conversation ("later start", "shorter walks") and
/// predate any option list. They must keep displaying — deleting somebody's
/// settings because a validator arrived later would be the worst possible
/// behaviour. So an unrecognised stored value is shown as-is and simply cannot
/// be re-selected from the picker.
///
/// The option tokens here are STABLE IDS, never display text. The app maps them
/// to localised product language; nothing in this file is ever shown to anyone.
/// </summary>
public static class GlunoPreferenceValues
{
    /// <summary>
    /// Which control the app should offer.
    ///
    /// The app is free not to recognise a kind it has never seen — an unknown
    /// kind renders as read-only rather than as a broken editor.
    /// </summary>
    public static class Editors
    {
        public const string Choice = "choice";
        public const string Time = "time";
        public const string Minutes = "minutes";
        /// Short free text, capped. Interests and dietary needs are genuinely
        /// open-ended and a closed list would be wrong rather than merely
        /// inconvenient.
        public const string Text = "text";
        /// No editor. The value is displayed and can be forgotten, not changed.
        public const string ReadOnly = "read_only";
    }

    public const int MaxTextLength = 120;
    public const int MinMinutes = 5;
    public const int MaxMinutes = 240;

    private static readonly IReadOnlyDictionary<string, string[]> Choices =
        new Dictionary<string, string[]>(StringComparer.Ordinal)
        {
            [GlunoPreferenceKeys.Pace] = ["relaxed", "balanced", "packed"],
            [GlunoPreferenceKeys.Budget] = ["budget", "moderate", "comfortable", "premium"],
            [GlunoPreferenceKeys.Transport] = ["walking", "public_transport", "car", "taxi", "mixed"],
            [GlunoPreferenceKeys.Nightlife] = ["none", "some", "lots"],
            [GlunoPreferenceKeys.Intent] = ["inspiration", "booking"],
        };

    /// <summary>
    /// The control for a key.
    ///
    /// Accessibility and group context are deliberately READ-ONLY here. They
    /// are the most sensitive things the store holds, they were stated in
    /// conversation with context around them, and a settings screen that
    /// invites someone to re-type "limited mobility" into a text box has
    /// turned a planning constraint into a profile field. They can still be
    /// forgotten, which is the control that matters.
    /// </summary>
    public static string EditorFor(string key) => key switch
    {
        GlunoPreferenceKeys.Pace or GlunoPreferenceKeys.Budget
            or GlunoPreferenceKeys.Transport or GlunoPreferenceKeys.Nightlife
            or GlunoPreferenceKeys.Intent => Editors.Choice,
        GlunoPreferenceKeys.StartTime => Editors.Time,
        GlunoPreferenceKeys.WalkingDistance => Editors.Minutes,
        GlunoPreferenceKeys.Interests or GlunoPreferenceKeys.Food
            or GlunoPreferenceKeys.Avoid => Editors.Text,
        _ => Editors.ReadOnly,
    };

    /// The stable option ids for a choice key, or empty for every other kind.
    public static IReadOnlyList<string> OptionsFor(string key)
        => Choices.TryGetValue(key, out var options) ? options : [];

    /// <summary>
    /// Validates and canonicalises a value the user just picked or typed.
    ///
    /// Returns null when the value is not acceptable for this key. The caller
    /// refuses the request rather than storing a corrected guess — silently
    /// turning "10:70" into "11:10" writes something the user did not choose.
    /// </summary>
    public static string? Canonicalise(string key, string? value)
    {
        var trimmed = value?.Trim();
        if (string.IsNullOrEmpty(trimmed)) return null;

        switch (EditorFor(key))
        {
            case Editors.Choice:
                return OptionsFor(key).Contains(trimmed, StringComparer.Ordinal) ? trimmed : null;

            case Editors.Time:
                // Stored as HH:mm, 24-hour, culture-invariant. A stored time
                // that means different things in two locales is a bug waiting
                // for somebody's flight.
                return TimeOnly.TryParseExact(trimmed, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                    ? time.ToString("HH:mm", CultureInfo.InvariantCulture)
                    : null;

            case Editors.Minutes:
                return int.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var minutes)
                    && minutes >= MinMinutes && minutes <= MaxMinutes
                        ? minutes.ToString(CultureInfo.InvariantCulture)
                        : null;

            case Editors.Text:
                // Sanitised, not just capped. This value ends up in a prompt,
                // and a control character or an instruction-shaped line is the
                // one thing a free-text planning preference must not smuggle.
                var cleaned = GlunoTextSanitizer.Clean(trimmed, MaxTextLength);
                return cleaned.LooksLikeInjection || string.IsNullOrWhiteSpace(cleaned.Value)
                    ? null
                    : cleaned.Value;

            default:
                // Read-only. Not editable through this path at all, which is
                // different from "the value was wrong".
                return null;
        }
    }

    /// <summary>
    /// True when this preference decides whether a plan is even workable.
    ///
    /// Used to decide whether changing it invalidates pending proposals. A
    /// plan built around "no car" or "30 minutes of walking maximum" stops
    /// being a plan the moment that changes; one built around "likes museums"
    /// merely becomes less good.
    /// </summary>
    public static bool AffectsFeasibility(string key) => key
        is GlunoPreferenceKeys.WalkingDistance
        or GlunoPreferenceKeys.Transport
        or GlunoPreferenceKeys.StartTime
        or GlunoPreferenceKeys.Accessibility
        or GlunoPreferenceKeys.Avoid;
}
