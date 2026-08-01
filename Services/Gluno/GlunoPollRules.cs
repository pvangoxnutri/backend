using System.Text.Json;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services.Gluno;

public sealed record GlunoPollOption(string Id, string Label, string? Summary);

public sealed record GlunoPollTally(
    string OptionId,
    string Label,
    int Votes);

public sealed record GlunoPollResult
{
    public required IReadOnlyList<GlunoPollTally> Tallies { get; init; }
    public required int Responded { get; init; }
    public required int Abstained { get; init; }
    public required int GroupSize { get; init; }

    /// <summary>
    /// The single option with the most votes, or null when there is a tie.
    ///
    /// Null on a tie is deliberate. A tie is a real outcome and resolving it
    /// arbitrarily — first option, alphabetical, random — produces a "group
    /// decision" nobody made.
    /// </summary>
    public string? WinningOptionId { get; init; }

    public bool IsTie { get; init; }

    /// <summary>
    /// Everyone who is still a member has answered.
    ///
    /// Counted against CURRENT membership: somebody who left mid-poll cannot
    /// keep it open forever, and their vote stops counting the moment they are
    /// no longer part of the group.
    /// </summary>
    public bool EveryoneResponded => Responded >= GroupSize && GroupSize > 0;

    /// A coarse bucket for telemetry. Never the individual votes.
    public string ResponseRateBucket
    {
        get
        {
            if (GroupSize == 0) return "none";

            var rate = (double)Responded / GroupSize;
            return rate switch
            {
                >= 1 => "all",
                >= 0.6 => "most",
                > 0 => "some",
                _ => "none",
            };
        }
    }
}

/// <summary>
/// Rules for the polls Gluno creates.
///
/// WHY OPTIONS ARE CAPPED AT FOUR. A poll with fifteen options is not a
/// decision, it is a survey — and a group faced with one either does not answer
/// or splits so thinly that nothing wins. Two to four forces the actual
/// trade-off into the open, which is the only thing a poll is good for.
///
/// WHY LEADING OPTIONS ARE REJECTED. Gluno writes the options. That makes it
/// trivially easy to write "A: a lovely relaxed day" against "B: an exhausting
/// rush", and a group that votes on those has not decided anything — it has
/// agreed with the phrasing. The detector below is crude and one-sided on
/// purpose: it only fires on obviously loaded language, because a false
/// positive costs a rewrite and a false negative costs the group's actual
/// choice.
/// </summary>
public static class GlunoPollRules
{
    public const int MinOptions = 2;
    public const int MaxOptions = 4;
    public const int MaxLabelLength = 80;
    public const int MaxSummaryLength = 160;

    /// <summary>
    /// Words that put a thumb on the scale.
    ///
    /// Split into positive and negative because the tell is ASYMMETRY: one
    /// option described warmly and another coldly. A poll where every option
    /// says "nice" is merely enthusiastic; a poll where one does is steering.
    /// </summary>
    private static readonly string[] LoadedPositive =
    [
        "best", "perfect", "ideal", "obviously", "clearly better", "amazing",
        "wonderful", "recommended", "smartest", "obvious choice",
        "bast", "perfekt", "idealisk", "sjalvklart", "uppenbart", "fantastisk",
        "underbar", "rekommenderas", "smartast",
    ];

    private static readonly string[] LoadedNegative =
    [
        "exhausting", "boring", "pointless", "waste", "stressful", "terrible",
        "nobody wants", "worst", "bad idea",
        "utmattande", "trakigt", "meninglost", "slosseri", "stressigt", "hemskt",
        "ingen vill", "samst", "daligt",
    ];

    /// <summary>
    /// Checks a set of options and reports what is wrong with them.
    /// </summary>
    public static IReadOnlyList<string> Validate(IReadOnlyList<GlunoPollOption> options)
    {
        var problems = new List<string>();

        if (options.Count < MinOptions) problems.Add("too_few_options");
        if (options.Count > MaxOptions) problems.Add("too_many_options");

        if (options.Select(option => option.Id).Distinct(StringComparer.Ordinal).Count() != options.Count)
            problems.Add("duplicate_option_ids");

        foreach (var option in options)
        {
            if (string.IsNullOrWhiteSpace(option.Label)) problems.Add("empty_label");
            if (option.Label.Length > MaxLabelLength) problems.Add("label_too_long");
            if ((option.Summary?.Length ?? 0) > MaxSummaryLength) problems.Add("summary_too_long");
        }

        if (IsLeading(options)) problems.Add("leading_options");

        return problems;
    }

    /// <summary>
    /// True when the wording steers rather than describes.
    ///
    /// Fires on ASYMMETRY, not on enthusiasm: praise on one option and
    /// criticism on another, or praise on exactly one of several. Uniformly
    /// warm options are a style, not a thumb on the scale.
    /// </summary>
    public static bool IsLeading(IReadOnlyList<GlunoPollOption> options)
    {
        if (options.Count < 2) return false;

        var positive = 0;
        var negative = 0;

        foreach (var option in options)
        {
            var text = GlunoIntentRouter.Normalise($"{option.Label} {option.Summary}");

            if (LoadedPositive.Any(word => text.Contains(word, StringComparison.Ordinal))) positive++;
            if (LoadedNegative.Any(word => text.Contains(word, StringComparison.Ordinal))) negative++;
        }

        // One option praised while another is disparaged: unambiguous.
        if (positive > 0 && negative > 0) return true;

        // Exactly one option praised out of several: the others are being
        // quietly framed as the lesser choice.
        return positive == 1 && options.Count > 1;
    }

    /// <summary>
    /// Trims a set of options to the allowed shape.
    ///
    /// Used when Gluno produced too many: the first four survive rather than a
    /// "best" four, because choosing which options a group gets to see is
    /// exactly the influence this file exists to prevent.
    /// </summary>
    public static IReadOnlyList<GlunoPollOption> Clamp(IReadOnlyList<GlunoPollOption> options)
        => options
            .Where(option => !string.IsNullOrWhiteSpace(option.Label))
            .Take(MaxOptions)
            .Select(option => option with
            {
                Label = Truncate(option.Label, MaxLabelLength),
                Summary = option.Summary == null ? null : Truncate(option.Summary, MaxSummaryLength),
            })
            .ToList();

    /// <summary>
    /// Counts the result FROM THE VOTE ROWS.
    ///
    /// Never from a total a client sent — a client-supplied tally is a
    /// client-supplied outcome, and a poll whose result can be posted is not a
    /// poll.
    /// </summary>
    public static GlunoPollResult Tally(
        IReadOnlyList<GlunoPollOption> options,
        IReadOnlyList<(Guid UserId, string? OptionId)> votes,
        IReadOnlySet<Guid> currentMemberIds)
    {
        // Votes from people who have left the Adventure do not count. They are
        // no longer part of the group whose decision this is.
        var live = votes.Where(vote => currentMemberIds.Contains(vote.UserId)).ToList();

        var counts = options.ToDictionary(
            option => option.Id,
            option => live.Count(vote => vote.OptionId == option.Id),
            StringComparer.Ordinal);

        var tallies = options
            .Select(option => new GlunoPollTally(option.Id, option.Label, counts[option.Id]))
            .ToList();

        var top = counts.Values.DefaultIfEmpty(0).Max();
        var leaders = counts.Where(pair => pair.Value == top && top > 0).Select(pair => pair.Key).ToList();

        return new GlunoPollResult
        {
            Tallies = tallies,
            Responded = live.Count,
            Abstained = live.Count(vote => vote.OptionId == null),
            GroupSize = currentMemberIds.Count,
            // A tie leaves the winner null. Gluno then offers a compromise or
            // asks the group to choose again — it does not pick.
            WinningOptionId = leaders.Count == 1 ? leaders[0] : null,
            IsTie = leaders.Count > 1,
        };
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max].TrimEnd() + "…";
}

/// <summary>Reading the stored options JSON, forgivingly.</summary>
public static class GlunoPollOptions
{
    public static IReadOnlyList<GlunoPollOption> Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<GlunoPollOption>();

        try
        {
            var options = JsonSerializer.Deserialize<List<GlunoPollOption>>(json, GlunoJson.Options);
            return options ?? [];
        }
        catch (JsonException)
        {
            // A malformed row renders as "no options" rather than breaking the
            // whole conversation.
            return Array.Empty<GlunoPollOption>();
        }
    }

    public static string? LabelOf(string? json, string? optionId)
    {
        if (optionId == null) return null;
        return Parse(json).FirstOrDefault(option => option.Id == optionId)?.Label;
    }
}
