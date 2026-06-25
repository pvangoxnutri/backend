namespace sidequest.backend.Services;

// Title/body templates for the four push notification types, keyed by the
// recipient's User.Language ("en"/"sv"). User-authored content (teaser text,
// names, trip titles) is interpolated as-is — only the surrounding template
// is translated.
public static class PushNotificationTexts
{
    public static (string Title, string Body) Teaser(string language, string teaserText) =>
        language == "sv"
            ? ("En SideQuest börjar närma sig 👀", teaserText)
            : ("A SideQuest is getting closer 👀", teaserText);

    public static (string Title, string Body) Reveal(string language, string activityTitle) =>
        language == "sv"
            ? ("SideQuest upplåst! 🎉", $"{activityTitle} har avslöjats")
            : ("SideQuest unlocked! 🎉", $"{activityTitle} is revealed");

    public static (string Title, string Body) TripInvite(string language, string ownerName, string tripTitle) =>
        language == "sv"
            ? ($"{ownerName} bjöd in dig", $"Gå med i {tripTitle}")
            : ($"{ownerName} invited you", $"Join {tripTitle}");

    public static (string Title, string Body) ChatMessage(string language, string userName, string tripTitle) =>
        language == "sv"
            ? ($"{userName} skickade ett meddelande", $"I {tripTitle}")
            : ($"{userName} sent a message", $"In {tripTitle}");

    // Deliberately doesn't name the activity — it may be a hidden SideQuest,
    // and the whole point of hidden ones is that the title isn't spoiled
    // anywhere outside its own detail screen.
    public static (string Title, string Body) ActivityAdded(string language, string actorName, string tripTitle) =>
        language == "sv"
            ? ($"{actorName} la till en ny SideQuest", $"I {tripTitle}")
            : ($"{actorName} added a new SideQuest", $"In {tripTitle}");
}
