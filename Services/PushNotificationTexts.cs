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

    public static (string Title, string Body) NewActivity(string language, string activityTitle) =>
        language == "sv"
            ? ("Ny aktivitet tillagd", activityTitle)
            : ("New activity added", activityTitle);

    // Deliberately doesn't name the activity — it's a hidden SideQuest, and
    // the whole point of hidden ones is that the title isn't spoiled
    // anywhere outside its own detail screen.
    public static (string Title, string Body) NewHiddenSideQuest(string language) =>
        language == "sv"
            ? ("Ny dold SideQuest", "Någon lade till en överraskning till din resa.")
            : ("New hidden SideQuest", "Someone added a surprise to your trip.");

    public static (string Title, string Body) SideQuestRevealed(string language, string activityTitle) =>
        language == "sv"
            ? ("En SideQuest har avslöjats 🎁", activityTitle)
            : ("A SideQuest was revealed 🎁", activityTitle);

    public static (string Title, string Body) Chat(string language, int count) =>
        language == "sv"
            ? ($"{count} nya chattmeddelanden", "Tryck för att hoppa in i konversationen.")
            : ($"{count} new chat messages", "Tap to jump into the conversation.");

    public static (string Title, string Body) Expense(string language, string expenseTitle, string amount) =>
        language == "sv"
            ? ("Ny utgift tillagd", $"{expenseTitle} • {amount}")
            : ("New expense added", $"{expenseTitle} • {amount}");
}
