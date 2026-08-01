namespace sidequest.backend.Services.Gluno;

public enum GlunoFallbackReason
{
    TripadvisorUnavailable,
    RoutingUnavailable,
    WeatherUnavailable,
    OpeningHoursUnavailable,
    AdventureDataChanged,
    ReferencedItemDeleted,
    GroundingFailed,
    ProviderRefused,
    ProviderTimeout,
    ToolBudgetReached,
}

/// <summary>
/// What Gluno says when something it needed was not available.
///
/// FOUR RULES, all learned from how these go wrong.
///
///  1. **Keep helping.** A missing provider removes one capability, not the
///     whole assistant. "I couldn't check the hours, but here's a sensible
///     order for the day" is a good answer. "I can't help right now" is a
///     product that feels broken every time a third party has a bad minute.
///
///  2. **No internal detail.** No error codes, no provider status, no mention
///     of timeouts or configuration. The user cannot act on any of it and it
///     reads as the app confessing to being unwell.
///
///  3. **Do not blame the provider for our own gaps.** When Tripadvisor is
///     simply not configured in this environment, saying "Tripadvisor is
///     unavailable" is false — it is working fine, we never called it. The
///     wording below says what Gluno cannot do, not who is at fault.
///
///  4. **Stay inside the response contract.** A fallback for an app-help
///     question is still short and still offers somewhere to go; a fallback for
///     a day plan still talks about the day. It is a different answer, not a
///     different assistant.
/// </summary>
public static class GlunoFallbacks
{
    public static string Text(GlunoFallbackReason reason, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return reason switch
        {
            // Note the phrasing: what Gluno cannot do, never "Tripadvisor is
            // down". Most of the time this fires because the integration is off
            // in this environment, and blaming them would be a lie.
            GlunoFallbackReason.TripadvisorUnavailable => swedish
                ? "Jag kan inte slå upp aktuella platsuppgifter just nu, så jag håller mig till det jag vet om er plan. "
                  + "Säg vad ni är sugna på så föreslår jag utifrån det."
                : "I can't look up current place details right now, so I'll work from what I know about your plan. "
                  + "Tell me what you're after and I'll suggest from there.",

            GlunoFallbackReason.RoutingUnavailable => swedish
                ? "Jag kan inte verifiera restider just nu. Jag kan fortfarande lägga dagen i en vettig ordning "
                  + "utifrån avstånden, men räkna med lite marginal mellan stoppen."
                : "I can't verify travel times right now. I can still put the day in a sensible order based on "
                  + "distances, but leave yourself some slack between stops.",

            GlunoFallbackReason.WeatherUnavailable => swedish
                ? "Jag har ingen prognos för den dagen just nu. Jag planerar utan väderhänsyn — kolla prognosen "
                  + "innan ni bestämmer er för utomhusdelarna."
                : "I don't have a forecast for that day right now. I'll plan without weather in mind — check the "
                  + "forecast before you commit to the outdoor parts.",

            // The example the spec called out. Names the limitation, keeps the
            // offer, and tells the user exactly what to do about it.
            GlunoFallbackReason.OpeningHoursUnavailable => swedish
                ? "Jag kunde inte verifiera aktuella öppettider. Jag kan fortfarande hjälpa dig planera ordningen, "
                  + "men kontrollera tiden innan ni går dit."
                : "I could not verify the current opening hours. I can still help plan the order, but check the "
                  + "time before you go.",

            GlunoFallbackReason.AdventureDataChanged => swedish
                ? "Planen har ändrats sedan jag tittade. Fråga en gång till så utgår jag från hur den ser ut nu."
                : "The plan changed since I looked. Ask me again and I'll work from how it stands now.",

            GlunoFallbackReason.ReferencedItemDeleted => swedish
                ? "Det du syftar på finns inte kvar i planen. Vilket menade du?"
                : "What you're pointing at isn't in the plan any more. Which one did you mean?",

            // Deliberately does not mention validation, models or checks. From
            // the user's side this is simply Gluno not being sure enough to say.
            GlunoFallbackReason.GroundingFailed => swedish
                ? "Jag är inte säker nog på detaljerna för att svara på det utan att gissa. Fråga gärna om en sak "
                  + "i taget så tittar jag ordentligt på den."
                : "I'm not confident enough in the details to answer that without guessing. Ask me about one thing "
                  + "at a time and I'll look at it properly.",

            GlunoFallbackReason.ProviderRefused => swedish
                ? "Det där kan jag tyvärr inte hjälpa till med. Fråga mig något annat om resan så gör jag mitt bästa."
                : "I can't help with that one, sorry. Ask me something else about the trip and I'll do my best.",

            GlunoFallbackReason.ProviderTimeout => swedish
                ? "Det tog för lång tid den här gången. Prova igen, eller fråga om något mindre så går det snabbare."
                : "That took too long this time. Try again, or ask about something smaller and it'll be quicker.",

            GlunoFallbackReason.ToolBudgetReached => swedish
                ? "Jag hann inte hela vägen med den frågan. Ta den i mindre delar så gör jag varje del ordentligt."
                : "I didn't get all the way through that one. Break it into smaller pieces and I'll do each properly.",

            _ => swedish
                ? "Jag har inget bra svar på det just nu. Vill du prova att fråga på ett annat sätt?"
                : "I don't have a good answer for that right now. Want to try asking it a different way?",
        };
    }

    /// <summary>
    /// A short note to add to an otherwise good answer, when one source was
    /// missing but the rest held up.
    ///
    /// Different from a full fallback: the answer stands, and this is the one
    /// clause of honesty attached to it.
    /// </summary>
    public static string Note(GlunoFallbackReason reason, string language)
    {
        var swedish = string.Equals(language, "sv", StringComparison.OrdinalIgnoreCase);

        return reason switch
        {
            GlunoFallbackReason.OpeningHoursUnavailable => swedish
                ? "Öppettiderna är inte verifierade — kolla innan ni går."
                : "Opening hours aren't verified — check before you go.",
            GlunoFallbackReason.RoutingUnavailable => swedish
                ? "Restiderna är uppskattade, inte verifierade."
                : "Travel times are estimates, not verified.",
            GlunoFallbackReason.WeatherUnavailable => swedish
                ? "Jag har ingen prognos för den dagen."
                : "I don't have a forecast for that day.",
            GlunoFallbackReason.TripadvisorUnavailable => swedish
                ? "Platsuppgifterna är inte uppslagna den här gången."
                : "Place details weren't looked up this time.",
            _ => swedish ? "En del uppgifter kunde inte verifieras." : "Some details couldn't be verified.",
        };
    }
}
