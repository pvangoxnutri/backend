namespace sidequest.backend.Services;

// Title/body templates for the push notification types, keyed by the
// recipient's User.Language ("en"/"sv"). User-authored content (activity
// titles, names, amounts) is interpolated as-is — only the surrounding
// template is translated.
public static class PushNotificationTexts
{
    public static (string Title, string Body) Teaser(string language, string teaserText) =>
        language == "sv"
            ? ("En SideQuest börjar närma sig 👀", teaserText)
            : ("A SideQuest is getting closer 👀", teaserText);

    public static (string Title, string Body) TripInvite(string language, string ownerName, string tripTitle) =>
        language == "sv"
            ? ($"{ownerName} bjöd in dig", $"Gå med i {tripTitle}")
            : ($"{ownerName} invited you", $"Join {tripTitle}");

    public static (string Title, string Body) MemberJoined(string language, string memberName, string tripTitle, int memberCount) =>
        language == "sv"
            ? ($"{memberName} gick med i {tripTitle}", $"Äventyret har nu {memberCount} medlemmar.")
            : ($"{memberName} joined {tripTitle}", $"The adventure now has {memberCount} members.");

    // Title carries the actor (who), Body carries the activity title (what)
    // — the in-app notification center shows both since the activity isn't
    // hidden.
    public static (string Title, string Body) NewActivity(string language, string actorName, string activityTitle) =>
        language == "sv"
            ? ($"{actorName} la till en ny SideQuest", activityTitle)
            : ($"{actorName} added a new SideQuest", activityTitle);

    // Deliberately anonymous and titleless — it's a hidden SideQuest, and
    // the whole point of hidden ones is that neither who added it nor its
    // title is spoiled anywhere outside its own detail screen.
    public static (string Title, string Body) NewHiddenSideQuest(string language) =>
        language == "sv"
            ? ("Någon la till en SideQuest 👀", "")
            : ("Somebody added a SideQuest 👀", "");

    // Titleless on purpose — tapping the notification already opens the
    // SideQuest's detail screen, where the title is right there. Naming it
    // here too was just noise (and the in-app center mirrors this exactly).
    public static (string Title, string Body) SideQuestRevealed(string language) =>
        language == "sv"
            ? ("En SideQuest har avslöjats! 🎁", "")
            : ("A SideQuest was revealed! 🎁", "");

    // A single new message names its sender — once there's more than one
    // unread, naming just the latest sender would be misleading, so it
    // collapses to a count instead.
    public static (string Title, string Body) Chat(string language, string senderName, int count) =>
        count > 1
            ? (language == "sv" ? ($"{count} nya chattmeddelanden", "Tryck för att hoppa in i konversationen.") : ($"{count} new chat messages", "Tap to jump into the conversation."))
            : (language == "sv" ? ($"{senderName} skickade ett meddelande", "Tryck för att hoppa in i konversationen.") : ($"{senderName} sent a message", "Tap to jump into the conversation."));

    public static (string Title, string Body) Expense(string language, string expenseTitle, string amount) =>
        language == "sv"
            ? ("Ny utgift tillagd", $"{expenseTitle} • {amount}")
            : ("New expense added", $"{expenseTitle} • {amount}");
}
