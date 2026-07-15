namespace sidequest.backend.Services;

public static class DisplayNameHelper
{
    /// <summary>
    /// User.Name is an empty string (not null) for accounts created before
    /// the app required a name at signup — a plain <c>?? "Someone"</c> never
    /// fired for them, so activity feeds, chat joins and pushes rendered a
    /// blank name ("&#160;gick med i resan"). Whitespace counts as missing.
    /// </summary>
    public static string OrFallback(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "Someone" : name;
}
