namespace sidequest.backend.Services.Gluno;

/// <summary>
/// Who is allowed to use a capability.
/// </summary>
public static class CapabilityAudiences
{
    /// Any signed-in user, no Adventure needed.
    public const string Anyone = "anyone";
    /// Any member of the Adventure.
    public const string AnyMember = "any_member";
    /// Members only when the owner has left collaborative editing on;
    /// owners always.
    public const string EditorsOnly = "editors_only";
    /// The Adventure's owner only.
    public const string OwnerOnly = "owner_only";
    /// Only whoever created the thing (hidden SideQuests).
    public const string CreatorOnly = "creator_only";
}

/// <summary>
/// One thing SideQuest can actually do.
///
/// WHY THIS IS DATA AND NOT DOCUMENTATION. A language model asked "how do I
/// add a hotel?" will happily invent a plausible button, and a plausible wrong
/// answer is worse than "I don't know" — the user goes looking for something
/// that is not there and concludes the app is broken. This registry is the
/// only thing Gluno is allowed to describe the app from: if a capability is
/// not here, as far as Gluno is concerned SideQuest cannot do it.
///
/// It is deliberately not prose. <see cref="Audience"/>,
/// <see cref="Prerequisites"/>, <see cref="GlunoActions"/> and
/// <see cref="FeatureFlag"/> are machine-checkable, which is what lets Gluno
/// answer "you can't, only the owner can" instead of confidently telling a
/// regular member to open a settings screen they cannot reach.
///
/// <see cref="GlunoActions"/> is the important one for honesty: empty means
/// "the user has to do this themselves", and Gluno must say so rather than
/// implying it can take care of it.
/// </summary>
public sealed class SideQuestCapability
{
    /// Stable id, e.g. "activity.create". Never renamed — an old conversation
    /// referencing one must keep resolving.
    public required string Id { get; init; }

    public required string NameEn { get; init; }
    public required string NameSv { get; init; }
    /// One sentence on what it does.
    public required string DescriptionEn { get; init; }
    public required string DescriptionSv { get; init; }
    /// Where it lives, in the app's own words. This is what Gluno turns into
    /// steps, so it names real screens and real labels only.
    public required string WhereEn { get; init; }
    public required string WhereSv { get; init; }

    /// Machine keys the caller can check, e.g. "trip_selected",
    /// "activity_has_image", "coordinates_required".
    public IReadOnlyList<string> Prerequisites { get; init; } = Array.Empty<string>();

    /// One of <see cref="CapabilityAudiences"/>.
    public required string Audience { get; init; }

    /// Stable screen ids this lives on — see <see cref="SideQuestScreens"/>.
    public IReadOnlyList<string> Screens { get; init; } = Array.Empty<string>();

    /// Allow-listed navigation target, when Gluno can offer to open it.
    public string? NavigationTarget { get; init; }

    /// Gluno actions that touch this capability. EMPTY means Gluno can only
    /// explain it — the user does it themselves.
    public IReadOnlyList<string> GlunoActions { get; init; } = Array.Empty<string>();

    /// Things it deliberately cannot do. Written so Gluno can say "no" with a
    /// reason rather than inventing a workaround.
    public IReadOnlyList<string> LimitationsEn { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> LimitationsSv { get; init; } = Array.Empty<string>();

    /// Null when always on. Otherwise the flag that gates it.
    public string? FeatureFlag { get; init; }

    /// Minimum client build. 0 = every shipped version has it.
    public int MinClientVersion { get; init; }

    /// Search terms in both languages, plus the misspellings people actually
    /// type. Not for display.
    public IReadOnlyList<string> Synonyms { get; init; } = Array.Empty<string>();

    public string Name(string language) => language == "sv" ? NameSv : NameEn;
    public string Description(string language) => language == "sv" ? DescriptionSv : DescriptionEn;
    public string Where(string language) => language == "sv" ? WhereSv : WhereEn;
    public IReadOnlyList<string> Limitations(string language) => language == "sv" ? LimitationsSv : LimitationsEn;
}

/// <summary>
/// Stable screen ids. The mobile app maps these to routes; nothing above this
/// layer ever sees a route string, which is what keeps a renamed file from
/// breaking Gluno's answers.
/// </summary>
public static class SideQuestScreens
{
    public const string Home = "home";
    public const string Calendar = "calendar";
    public const string Profile = "profile";
    public const string CreateAdventure = "create_adventure";
    public const string AdventureOverview = "adventure_overview";
    public const string AdventureFunctions = "adventure_functions";
    public const string AdventureSettings = "adventure_settings";
    public const string ActivityDetail = "activity_detail";
    public const string ActivityCreate = "activity_create";
    public const string Chat = "chat";
    public const string Expenses = "expenses";
    public const string Packlist = "packlist";
    public const string Documents = "documents";
    public const string Weather = "weather";
    public const string TravelTracker = "travel_tracker";
    public const string PreviousAdventures = "previous_adventures";
    public const string Support = "support";
    public const string Gluno = "gluno";

    public static readonly IReadOnlyList<string> All =
    [
        Home, Calendar, Profile, CreateAdventure, AdventureOverview, AdventureFunctions,
        AdventureSettings, ActivityDetail, ActivityCreate, Chat, Expenses, Packlist,
        Documents, Weather, TravelTracker, PreviousAdventures, Support, Gluno,
    ];

    public static bool IsKnown(string? screen) => screen != null && All.Contains(screen);
}

/// <summary>
/// Everything SideQuest can do, as far as Gluno is concerned.
///
/// <see cref="Version"/> is stamped into the context so an old conversation
/// can be told apart from a new one after the registry changes. Ids are
/// permanent: adding, editing or removing an entry must never make an existing
/// conversation fail — a reference to an id that no longer exists resolves to
/// nothing, which Gluno handles as "that is not something SideQuest does".
/// </summary>
public static class SideQuestCapabilities
{
    /// <summary>
    /// Bump whenever an entry is added, removed or materially changed.
    ///
    /// 1 — first registry: Adventures, days, Activities, stays, slideshow,
    ///     weather, chat, documents, expenses, packing, notifications,
    ///     reveals, Travel Tracker, members, invites, Gluno, support.
    /// </summary>
    public const int Version = 1;

    /// Feature-flag keys. Only Gluno is gated today.
    public const string GlunoFlag = "gluno_assistant";

    public static readonly IReadOnlyList<SideQuestCapability> All =
    [
        new()
        {
            Id = "adventure.create",
            NameEn = "Create an Adventure", NameSv = "Skapa ett äventyr",
            DescriptionEn = "Start a new trip with a title, destination and dates.",
            DescriptionSv = "Starta en ny resa med titel, resmål och datum.",
            WhereEn = "Home, using the button to create a new Adventure.",
            WhereSv = "Hem, via knappen för att skapa ett nytt äventyr.",
            Audience = CapabilityAudiences.Anyone,
            Screens = [SideQuestScreens.Home, SideQuestScreens.CreateAdventure],
            NavigationTarget = GlunoNavigationTargets.CreateAdventure,
            LimitationsEn = ["Gluno cannot create an Adventure for you."],
            LimitationsSv = ["Gluno kan inte skapa ett äventyr åt dig."],
            Synonyms = ["new trip", "ny resa", "skapa resa", "create trip", "start adventure", "nytt äventyr"],
        },
        new()
        {
            Id = "adventure.dates",
            NameEn = "Adventure dates", NameSv = "Äventyrets datum",
            DescriptionEn = "Change the start and end date of an Adventure.",
            DescriptionSv = "Ändra äventyrets start- och slutdatum.",
            WhereEn = "Adventure settings, under the dates section.",
            WhereSv = "Äventyrets inställningar, under datumsektionen.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.OwnerOnly,
            Screens = [SideQuestScreens.AdventureSettings],
            NavigationTarget = GlunoNavigationTargets.AdventureSettings,
            GlunoActions = [GlunoActions.ProposeTripDateChange],
            LimitationsEn = ["Dates cannot be narrowed while Activities would fall outside them."],
            LimitationsSv = ["Datum kan inte snävas in medan aktiviteter skulle hamna utanför."],
            Synonyms = ["change dates", "ändra datum", "start date", "slutdatum", "startdatum", "trip dates", "resedatum"],
        },
        new()
        {
            Id = "adventure.open_ended",
            NameEn = "Adventure without an end date", NameSv = "Äventyr utan slutdatum",
            DescriptionEn = "Leave the end date unset while you do not know when the trip ends.",
            DescriptionSv = "Lämna slutdatumet tomt så länge du inte vet när resan slutar.",
            WhereEn = "When creating the Adventure, or in Adventure settings, by leaving the end date empty.",
            WhereSv = "När du skapar äventyret, eller i inställningarna, genom att lämna slutdatumet tomt.",
            Audience = CapabilityAudiences.OwnerOnly,
            Screens = [SideQuestScreens.CreateAdventure, SideQuestScreens.AdventureSettings],
            NavigationTarget = GlunoNavigationTargets.AdventureSettings,
            GlunoActions = [GlunoActions.ProposeTripDateChange],
            LimitationsEn = ["An open-ended Adventure still accepts plans, up to two years from the start date."],
            LimitationsSv = ["Ett äventyr utan slutdatum tar fortfarande emot planer, upp till två år från startdatumet."],
            Synonyms = ["no end date", "öppet slut", "okänt slutdatum", "open ended", "pågående resa", "ongoing"],
        },
        new()
        {
            Id = "day.locations",
            NameEn = "Day locations", NameSv = "Dagsplatser",
            DescriptionEn = "Set where you are on a given day, and add extra stops to the same day.",
            DescriptionSv = "Ange var ni är en viss dag, och lägg till fler platser samma dag.",
            WhereEn = "Open the Adventure, tap the day you want to change, then choose the places option and add another place.",
            WhereSv = "Öppna äventyret, tryck på dagen du vill ändra, välj sedan platser och lägg till ytterligare plats.",
            Prerequisites = ["trip_selected", "coordinates_required"],
            Audience = CapabilityAudiences.EditorsOnly,
            Screens = [SideQuestScreens.AdventureOverview],
            NavigationTarget = GlunoNavigationTargets.AdventureFeedDay,
            GlunoActions = [GlunoActions.ProposeDayLocation],
            LimitationsEn = [
                "The first place on a day is its main location and carries forward to later days; extra stops belong to their own day only.",
                "A place needs coordinates — a typed name alone cannot be a day location.",
            ],
            LimitationsSv = [
                "Den första platsen en dag är huvudplatsen och följer med till senare dagar; extra stopp hör bara till sin egen dag.",
                "En plats behöver koordinater — bara ett skrivet namn räcker inte.",
            ],
            Synonyms = ["day location", "dagsplats", "flera platser", "multiple places", "var är vi", "stad", "ort", "extra stopp"],
        },
        new()
        {
            Id = "activity.create",
            NameEn = "Add an Activity", NameSv = "Lägg till aktivitet",
            DescriptionEn = "Add something to a day: a sight, a meal, a booking.",
            DescriptionSv = "Lägg till något på en dag: en sevärdhet, en måltid, en bokning.",
            WhereEn = "Open the Adventure and use the add button on the day you want.",
            WhereSv = "Öppna äventyret och använd lägg till-knappen på den dag du vill.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.EditorsOnly,
            Screens = [SideQuestScreens.AdventureOverview, SideQuestScreens.ActivityCreate],
            NavigationTarget = GlunoNavigationTargets.ActivityCreate,
            GlunoActions = [GlunoActions.ProposeActivity, GlunoActions.ProposeDayPlan],
            LimitationsEn = ["The date must be inside the Adventure's dates."],
            LimitationsSv = ["Datumet måste ligga inom äventyrets datum."],
            Synonyms = ["add activity", "ny aktivitet", "lägg till aktivitet", "activty", "aktivtet", "schema", "schedule", "plan a day", "planera dag"],
        },
        new()
        {
            Id = "activity.stay",
            NameEn = "Hotels and stays", NameSv = "Hotell och boenden",
            DescriptionEn = "An Activity that spans several days, with a check-in and a check-out.",
            DescriptionSv = "En aktivitet som sträcker sig över flera dagar, med incheckning och utcheckning.",
            WhereEn = "Add an Activity on the check-in day, pick the hotel category, then set the check-out date and time.",
            WhereSv = "Lägg till en aktivitet på incheckningsdagen, välj hotellkategorin och ange sedan datum och tid för utcheckning.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.EditorsOnly,
            Screens = [SideQuestScreens.ActivityCreate, SideQuestScreens.ActivityDetail],
            NavigationTarget = GlunoNavigationTargets.ActivityCreate,
            GlunoActions = [GlunoActions.ProposeActivity],
            LimitationsEn = [
                "Check-out must be after check-in and inside the Adventure's dates.",
                "SideQuest does not book anything — a stay is a plan, not a reservation.",
            ],
            LimitationsSv = [
                "Utcheckningen måste vara efter incheckningen och ligga inom äventyrets datum.",
                "SideQuest bokar ingenting — ett boende är en plan, inte en reservation.",
            ],
            Synonyms = ["hotel", "hotell", "boende", "stay", "check in", "incheckning", "utcheckning", "checkout", "övernattning", "airbnb"],
        },
        new()
        {
            Id = "activity.move",
            NameEn = "Move or reorder Activities", NameSv = "Flytta eller ändra ordning på aktiviteter",
            DescriptionEn = "Move an Activity to another day, or change the order within a day.",
            DescriptionSv = "Flytta en aktivitet till en annan dag, eller ändra ordningen inom en dag.",
            WhereEn = "In the Adventure, press and hold an Activity to drag it, or open it and change its date.",
            WhereSv = "I äventyret, håll in en aktivitet för att dra den, eller öppna den och ändra datum.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.EditorsOnly,
            Screens = [SideQuestScreens.AdventureOverview, SideQuestScreens.ActivityDetail],
            NavigationTarget = GlunoNavigationTargets.AdventureOverview,
            GlunoActions = [GlunoActions.ProposeActivityMove],
            LimitationsEn = ["Moving a stay shifts check-out by the same number of days, and must stay inside the Adventure."],
            LimitationsSv = ["Att flytta ett boende förskjuter utcheckningen lika många dagar, och måste hållas inom äventyret."],
            Synonyms = ["move activity", "flytta aktivitet", "reorder", "ändra ordning", "drag", "dra", "sortera", "byta dag"],
        },
        new()
        {
            Id = "activity.reschedule",
            NameEn = "Rescheduling when dates change", NameSv = "Omplanering när datum ändras",
            DescriptionEn = "When Adventure dates change so Activities would fall outside, SideQuest asks you to reschedule them first.",
            DescriptionSv = "När äventyrets datum ändras så att aktiviteter hamnar utanför ber SideQuest dig omplanera dem först.",
            WhereEn = "Adventure settings, when saving new dates.",
            WhereSv = "Äventyrets inställningar, när du sparar nya datum.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.OwnerOnly,
            Screens = [SideQuestScreens.AdventureSettings],
            NavigationTarget = GlunoNavigationTargets.AdventureSettings,
            LimitationsEn = ["Gluno cannot skip this step — a date change that would strand Activities is refused."],
            LimitationsSv = ["Gluno kan inte hoppa över detta — en datumändring som skulle strandsätta aktiviteter nekas."],
            Synonyms = ["reschedule", "omplanera", "flytta allt", "dates changed", "utanför datum"],
        },
        new()
        {
            Id = "slideshow",
            NameEn = "Slideshow", NameSv = "Bildspel",
            DescriptionEn = "The Adventure cover cycles through the cover image and your Activity photos.",
            DescriptionSv = "Äventyrets omslag växlar mellan omslagsbilden och dina aktivitetsbilder.",
            WhereEn = "Adventure settings, where the slideshow can be turned on or off.",
            WhereSv = "Äventyrets inställningar, där bildspelet kan slås på eller av.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.OwnerOnly,
            Screens = [SideQuestScreens.AdventureSettings, SideQuestScreens.AdventureOverview],
            NavigationTarget = GlunoNavigationTargets.AdventureSettings,
            LimitationsEn = ["Only Activities that have a photo appear."],
            LimitationsSv = ["Bara aktiviteter som har ett foto visas."],
            Synonyms = ["slideshow", "bildspel", "cover", "omslag", "bilder på framsidan", "photo carousel"],
        },
        new()
        {
            Id = "slideshow.exclude",
            NameEn = "Hide a photo from the slideshow", NameSv = "Dölj en bild från bildspelet",
            DescriptionEn = "Keep one Activity's photo out of the slideshow while it stays visible on the Activity.",
            DescriptionSv = "Håll en aktivitets bild utanför bildspelet medan den fortfarande syns på aktiviteten.",
            WhereEn = "Open the Activity, edit it, and turn off the option to include its photo in the slideshow.",
            WhereSv = "Öppna aktiviteten, redigera den och stäng av alternativet att ta med bilden i bildspelet.",
            Prerequisites = ["trip_selected", "activity_has_image"],
            Audience = CapabilityAudiences.EditorsOnly,
            Screens = [SideQuestScreens.ActivityDetail, SideQuestScreens.ActivityCreate],
            NavigationTarget = GlunoNavigationTargets.ActivityDetail,
            LimitationsEn = ["The setting only does anything once the Activity actually has a photo."],
            LimitationsSv = ["Inställningen gör något först när aktiviteten faktiskt har ett foto."],
            Synonyms = [
                "hide image", "dölj bild", "exclude from slideshow", "utesluta bild",
                // The phrasings people actually use when the photo is missing —
                // long enough to outweigh the generic slideshow entry.
                "bilden syns inte", "syns inte i bildspelet", "image not showing",
                "showing in the slideshow", "missing from the slideshow",
            ],
        },
        new()
        {
            Id = "weather",
            NameEn = "Weather", NameSv = "Väder",
            DescriptionEn = "The forecast for each day of the Adventure, based on that day's location.",
            DescriptionSv = "Prognosen för varje dag i äventyret, utifrån dagens plats.",
            WhereEn = "Open the Adventure, go to its functions, and choose Weather.",
            WhereSv = "Öppna äventyret, gå till dess funktioner och välj Väder.",
            Prerequisites = ["trip_selected", "coordinates_required"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Weather, SideQuestScreens.AdventureFunctions],
            NavigationTarget = GlunoNavigationTargets.Weather,
            LimitationsEn = [
                "A day needs a location with coordinates before it has a forecast.",
                "Forecasts only reach about two weeks ahead.",
            ],
            LimitationsSv = [
                "En dag behöver en plats med koordinater innan den har en prognos.",
                "Prognoser sträcker sig bara ungefär två veckor framåt.",
            ],
            Synonyms = ["weather", "väder", "forecast", "prognos", "regn", "rain", "temperatur"],
        },
        new()
        {
            Id = "chat",
            NameEn = "Adventure chat", NameSv = "Äventyrschatten",
            DescriptionEn = "A group chat with everyone on the Adventure.",
            DescriptionSv = "En gruppchatt med alla i äventyret.",
            WhereEn = "Open the Adventure and use its chat.",
            WhereSv = "Öppna äventyret och använd dess chatt.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Chat],
            NavigationTarget = GlunoNavigationTargets.Chat,
            LimitationsEn = [
                "Gluno is not part of the Adventure chat and cannot read, write or delete messages there.",
                "A sent message cannot be deleted.",
            ],
            LimitationsSv = [
                "Gluno är inte med i äventyrschatten och kan varken läsa, skriva eller radera meddelanden där.",
                "Ett skickat meddelande kan inte raderas.",
            ],
            Synonyms = ["chat", "chatt", "message", "meddelande", "gruppchatt", "skriva till gruppen"],
        },
        new()
        {
            Id = "chat.images",
            NameEn = "Chat photos", NameSv = "Chattbilder",
            DescriptionEn = "Send photos in the Adventure chat.",
            DescriptionSv = "Skicka foton i äventyrschatten.",
            WhereEn = "In the Adventure chat, use the photo button next to the message field.",
            WhereSv = "I äventyrschatten, använd bildknappen bredvid meddelandefältet.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Chat],
            NavigationTarget = GlunoNavigationTargets.Chat,
            LimitationsEn = ["Chat photos are private to the Adventure's members."],
            LimitationsSv = ["Chattbilder är privata för äventyrets medlemmar."],
            Synonyms = ["chat image", "chattbild", "skicka bild", "send photo", "foto i chatten"],
        },
        new()
        {
            Id = "documents",
            NameEn = "Documents", NameSv = "Dokument",
            DescriptionEn = "Tickets, bookings and other files for the Adventure.",
            DescriptionSv = "Biljetter, bokningar och andra filer för äventyret.",
            WhereEn = "Open the Adventure, go to its functions, and choose Documents.",
            WhereSv = "Öppna äventyret, gå till dess funktioner och välj Dokument.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Documents, SideQuestScreens.AdventureFunctions],
            NavigationTarget = GlunoNavigationTargets.Documents,
            LimitationsEn = ["Documents are private to the Adventure's members and open through short-lived links."],
            LimitationsSv = ["Dokument är privata för äventyrets medlemmar och öppnas via kortlivade länkar."],
            Synonyms = ["documents", "dokument", "tickets", "biljetter", "bokningsbekräftelse", "files", "filer", "pdf"],
        },
        new()
        {
            Id = "expenses",
            NameEn = "Expenses and splitting", NameSv = "Utgifter och kostnadsdelning",
            DescriptionEn = "Record what the trip costs and see who owes whom.",
            DescriptionSv = "Registrera vad resan kostar och se vem som är skyldig vem.",
            WhereEn = "Open the Adventure, go to its functions, and choose Cost Split.",
            WhereSv = "Öppna äventyret, gå till dess funktioner och välj Kostnadsdelning.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Expenses, SideQuestScreens.AdventureFunctions],
            NavigationTarget = GlunoNavigationTargets.Expenses,
            LimitationsEn = [
                "SideQuest does not move money — it records who paid and what is owed.",
                "Gluno cannot add or settle expenses.",
            ],
            LimitationsSv = [
                "SideQuest flyttar inga pengar — den registrerar vem som betalat och vad som är skuldsatt.",
                "Gluno kan inte lägga till eller reglera utgifter.",
            ],
            Synonyms = ["expenses", "utgifter", "kostnader", "split", "dela nota", "kostnadsdelning", "betala", "swish", "skuld", "who owes"],
        },
        new()
        {
            Id = "packlist",
            NameEn = "Packing list", NameSv = "Packlista",
            DescriptionEn = "A shared checklist of what to bring.",
            DescriptionSv = "En delad checklista över vad ni ska ta med.",
            WhereEn = "Open the Adventure, go to its functions, and choose Packing List.",
            WhereSv = "Öppna äventyret, gå till dess funktioner och välj Packlista.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.Packlist, SideQuestScreens.AdventureFunctions],
            NavigationTarget = GlunoNavigationTargets.Packlist,
            LimitationsEn = ["Gluno cannot add items to the packing list."],
            LimitationsSv = ["Gluno kan inte lägga till saker i packlistan."],
            Synonyms = ["packing list", "packlista", "packning", "pack", "vad ska jag ta med", "checklist", "checklista"],
        },
        new()
        {
            Id = "notifications",
            NameEn = "Notifications", NameSv = "Notiser",
            DescriptionEn = "Alerts about invitations, new Activities and revealed SideQuests.",
            DescriptionSv = "Aviseringar om inbjudningar, nya aktiviteter och avslöjade SideQuests.",
            WhereEn = "The bell in the header, and the notification settings in your profile.",
            WhereSv = "Klockan i sidhuvudet, och notisinställningarna i din profil.",
            Audience = CapabilityAudiences.Anyone,
            Screens = [SideQuestScreens.Home, SideQuestScreens.Profile],
            NavigationTarget = GlunoNavigationTargets.Profile,
            Synonyms = ["notifications", "notiser", "aviseringar", "push", "klockan", "alerts"],
        },
        new()
        {
            Id = "sidequest.hidden",
            NameEn = "Hidden SideQuests", NameSv = "Dolda SideQuests",
            DescriptionEn = "An Activity kept secret from the other travellers until a reveal time you choose.",
            DescriptionSv = "En aktivitet som hålls hemlig för de andra resenärerna tills en tidpunkt du väljer.",
            WhereEn = "When adding an Activity, mark it as hidden and set when it should be revealed.",
            WhereSv = "När du lägger till en aktivitet, markera den som dold och ange när den ska avslöjas.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.CreatorOnly,
            Screens = [SideQuestScreens.ActivityCreate, SideQuestScreens.ActivityDetail],
            NavigationTarget = GlunoNavigationTargets.ActivityCreate,
            LimitationsEn = [
                "Only the person who created it can see or edit it before the reveal.",
                "Gluno never creates hidden SideQuests — a surprise is yours to author.",
            ],
            LimitationsSv = [
                "Bara den som skapat den kan se eller redigera den före avslöjandet.",
                "Gluno skapar aldrig dolda SideQuests — en överraskning är din att skriva.",
            ],
            Synonyms = ["hidden", "dold", "surprise", "överraskning", "sidequest", "reveal", "avslöja", "hemlig"],
        },
        new()
        {
            Id = "travel_tracker",
            NameEn = "Travel Tracker", NameSv = "Travel Tracker",
            DescriptionEn = "The globe showing the countries and places you have travelled to.",
            DescriptionSv = "Jordgloben som visar länderna och platserna du rest till.",
            WhereEn = "Your profile, via Travel Tracker.",
            WhereSv = "Din profil, via Travel Tracker.",
            Audience = CapabilityAudiences.Anyone,
            Screens = [SideQuestScreens.TravelTracker, SideQuestScreens.Profile],
            NavigationTarget = GlunoNavigationTargets.TravelTracker,
            LimitationsEn = ["It fills in from completed Adventures; it cannot be edited directly."],
            LimitationsSv = ["Den fylls i från avslutade äventyr och kan inte redigeras direkt."],
            Synonyms = ["travel tracker", "globe", "jordglob", "karta", "map", "länder", "countries", "besökta platser", "resestatistik"],
        },
        new()
        {
            Id = "members",
            NameEn = "Members and profiles", NameSv = "Medlemmar och profiler",
            DescriptionEn = "Who is on the Adventure, and their profiles.",
            DescriptionSv = "Vilka som är med i äventyret, och deras profiler.",
            WhereEn = "Open the Adventure — members are shown on its overview.",
            WhereSv = "Öppna äventyret — medlemmarna visas på översikten.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.AnyMember,
            Screens = [SideQuestScreens.AdventureOverview, SideQuestScreens.Profile],
            NavigationTarget = GlunoNavigationTargets.AdventureOverview,
            LimitationsEn = ["Only an owner can remove a member or hand over ownership."],
            LimitationsSv = ["Bara en ägare kan ta bort en medlem eller lämna över ägarskapet."],
            Synonyms = ["members", "medlemmar", "resenärer", "travellers", "profil", "profile", "vem är med"],
        },
        new()
        {
            Id = "invites",
            NameEn = "Invitations", NameSv = "Inbjudningar",
            DescriptionEn = "Invite someone to the Adventure by email or with an invite code.",
            DescriptionSv = "Bjud in någon till äventyret via e-post eller med en inbjudningskod.",
            WhereEn = "Adventure settings, in the members section.",
            WhereSv = "Äventyrets inställningar, i medlemssektionen.",
            Prerequisites = ["trip_selected"],
            Audience = CapabilityAudiences.OwnerOnly,
            Screens = [SideQuestScreens.AdventureSettings],
            NavigationTarget = GlunoNavigationTargets.AdventureSettings,
            LimitationsEn = ["Gluno cannot invite anyone on your behalf."],
            LimitationsSv = ["Gluno kan inte bjuda in någon åt dig."],
            Synonyms = ["invite", "bjud in", "inbjudan", "invitation", "kod", "code", "lägga till person", "add friend"],
        },
        new()
        {
            Id = "gluno",
            NameEn = "Gluno", NameSv = "Gluno",
            DescriptionEn = "The travel expert built into SideQuest — planning help, recommendations and suggested changes you approve.",
            DescriptionSv = "Reseexperten i SideQuest — planeringshjälp, rekommendationer och föreslagna ändringar du godkänner.",
            // Two real entry points, and the difference matters to what Gluno
            // may say. From the app header it opens with no Adventure and has
            // to ask which one; from an Adventure's own header it opens with
            // that trip's plan already in front of it.
            WhereEn = "The Gluno button in the app header, or the Gluno button in an Adventure's header — that one opens Gluno already knowing which Adventure you mean.",
            WhereSv = "Gluno-knappen i appens sidhuvud, eller Gluno-knappen i ett äventyrs sidhuvud — den öppnar Gluno med det äventyret redan valt.",
            Audience = CapabilityAudiences.Anyone,
            Screens = [SideQuestScreens.Gluno],
            NavigationTarget = null,
            FeatureFlag = GlunoFlag,
            LimitationsEn = [
                "Gluno proposes changes; nothing is saved until you review and apply it.",
                "Gluno cannot book, pay, listen for a wake word, or use a microphone.",
            ],
            LimitationsSv = [
                "Gluno föreslår ändringar; ingenting sparas förrän du granskat och genomfört det.",
                "Gluno kan inte boka, betala, lyssna efter ett väckningsord eller använda mikrofon.",
            ],
            Synonyms = ["gluno", "assistent", "assistant", "ai", "hjälp", "help"],
        },
        new()
        {
            Id = "support",
            NameEn = "Support and reporting a problem", NameSv = "Support och felrapportering",
            DescriptionEn = "Contact the SideQuest team about a problem or a question.",
            DescriptionSv = "Kontakta SideQuest-teamet om ett problem eller en fråga.",
            WhereEn = "Your profile, via support.",
            WhereSv = "Din profil, via support.",
            Audience = CapabilityAudiences.Anyone,
            Screens = [SideQuestScreens.Support, SideQuestScreens.Profile],
            NavigationTarget = GlunoNavigationTargets.Support,
            Synonyms = ["support", "hjälp", "help", "bug", "bugg", "problem", "felanmälan", "kontakt", "contact"],
        },
    ];

    public static SideQuestCapability? Find(string? id)
        => id == null ? null : All.FirstOrDefault(capability => capability.Id == id);
}
