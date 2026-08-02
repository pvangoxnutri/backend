using System.Globalization;
using System.Text;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Detects the user asking for something tappable, and answers it with
/// something tappable.
///
/// THE FAILURE THIS EXISTS FOR. Someone told Gluno to give them clickable
/// options. Gluno replied that it could not put out buttons itself, that
/// SideQuest does that, and that the app was refusing to open an Adventure
/// because the conversation was not attached to one.
///
/// Every clause of that is wrong to say. Gluno is not a model commenting on an
/// app's implementation — it is one feature of SideQuest, and the boundary
/// between the model, the backend and the client is not the user's problem.
/// Worse, the thing being explained away was possible the whole time: the
/// options existed, nothing needed opening, and the card could have been built
/// on that very turn.
///
/// So the request is caught BEFORE the model. A model asked "can you give me
/// buttons?" will always answer in the first person about its own abilities;
/// the only reliable fix is for it never to see the question.
/// </summary>
public static class GlunoChoiceRequest
{
    /// <summary>
    /// Ways of asking for something to tap.
    ///
    /// Both languages, and deliberately about the FORM of the answer rather
    /// than its content: "which should I pick?" is a question about a plan,
    /// "give me something to pick from" is a request for an interface.
    /// </summary>
    private static readonly string[] Requests =
    [
        // English
        "clickable", "click on", "buttons", "give me options", "show me options",
        "show options", "let me choose", "let me pick", "give me choices",
        "show me a list", "make them clickable", "tappable", "tap on",
        "give me the options", "show me the choices", "list them as options",
        // Swedish
        "klicka pa", "klickbar", "klickbara", "knappar", "knapp",
        "ge mig alternativ", "visa alternativ", "visa val", "ge mig val",
        "lat mig valja", "later mig valja", "ge mig knappar",
        "visa mina", "ge mig resorna", "som alternativ", "att valja mellan",
        "valbara", "tryckbara",
    ];

    /// <summary>
    /// True when the message is asking for an interface rather than an answer.
    ///
    /// Matched on a normalised copy, so Swedish accents and a phone keyboard
    /// without them behave the same.
    /// </summary>
    public static bool IsAskingForChoices(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return false;

        var text = Normalise(message);

        return Requests.Any(phrase => text.Contains(Normalise(phrase), StringComparison.Ordinal));
    }

    private static string Normalise(string value)
    {
        var lowered = value.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(lowered.Length);

        foreach (var character in lowered)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                builder.Append(character);
            }
        }

        return builder.ToString().Normalize(NormalizationForm.FormC);
    }
}
