using System.Globalization;
using System.Text.Json;
using sidequest.backend.Models;

namespace sidequest.backend.Services.Gluno;

/// <summary>
/// One row of a draft day plan, read out of the payload.
///
/// A read model, not an entity. The payload is a detached document that has
/// already been through the action executor's validation; this reads it back
/// so the conflict resolver can reason about times without every caller
/// re-implementing the same defensive JSON walk.
/// </summary>
public sealed record GlunoDraftRow
{
    public required int Index { get; init; }
    public required string Title { get; init; }
    public TimeOnly? Start { get; init; }
    public TimeOnly? End { get; init; }

    /// <summary>
    /// How long it takes, in order of trust: an explicit duration, the gap
    /// between start and end, then null.
    ///
    /// Null means genuinely unknown, and unknown is never treated as zero — a
    /// row assumed instantaneous is a row the scheduler will happily stack
    /// something else on top of.
    /// </summary>
    public int? DurationMinutes { get; init; }

    /// A booking with a time. Never moved, never shortened.
    public bool IsFixed { get; init; }

    /// Already in the Adventure. Not a suggestion's to rearrange.
    public Guid? ExistingActivityId { get; init; }

    public double? Latitude { get; init; }
    public double? Longitude { get; init; }

    /// Minutes the journey from the previous stop needs, when known.
    public int? TravelFromPreviousMinutes { get; init; }

    /// <summary>
    /// When the place opens and closes on this day, as the schedule engine
    /// already resolved it.
    ///
    /// Read from the payload rather than fetched again. The engine did the
    /// provider call, applied the freshness rule and wrote the answer; asking a
    /// second time would risk a different answer for the same day and turn one
    /// question into two.
    ///
    /// Both null means genuinely unknown — which is a caveat on the answer,
    /// never a reason to hide half the day from somebody.
    /// </summary>
    public TimeOnly? OpensAt { get; init; }
    public TimeOnly? ClosesAt { get; init; }

    /// <summary>
    /// True when this row belongs to somebody else's plan rather than to this
    /// suggestion. The single question that decides what may be offered.
    /// </summary>
    public bool IsLocked => IsFixed || ExistingActivityId.HasValue;

    /// The row's own length, or a sensible default when it has none.
    public int EffectiveDuration => DurationMinutes
        ?? (Start is { } start && End is { } end && end > start
            ? (int)(end - start).TotalMinutes
            : GlunoDraftPlan.DefaultDurationMinutes);
}

/// <summary>
/// A draft day plan, parsed — and the deterministic edits a conflict answer
/// can make to it.
///
/// WHY THIS IS NOT A MODEL CALL. Every question a conflict raises has an exact
/// answer available from data the backend already holds: which days the trip
/// covers, where it is on each of them, what is already booked, when a place is
/// open, how long the journey takes. A model asked to "move this to a better
/// time" would be guessing at all of it, and would sometimes guess a time the
/// scheduler then rejects — so the user taps, waits, and gets the same card
/// back.
///
/// Deterministic also means the options are TRUE BEFORE THEY ARE SHOWN. A day
/// that cannot hold the activity is never offered, so no tap can fail.
///
/// NOTHING HERE WRITES. Every method returns a new payload string or a list of
/// candidates. The draft is a conversation about a change, not the change.
/// </summary>
public static class GlunoDraftPlan
{
    /// <summary>
    /// What an activity of unknown length is assumed to take.
    ///
    /// Ninety minutes: long enough that the scheduler does not stack three
    /// things into an afternoon on the strength of a guess, short enough that a
    /// quick stop does not swallow a day.
    /// </summary>
    public const int DefaultDurationMinutes = 90;

    /// <summary>
    /// The shortest an activity may be made.
    ///
    /// Thirty minutes. Below that "shorten it" stops being a plan and becomes a
    /// way of pretending something fits — and a fifteen-minute museum visit is
    /// a worse answer than being told the day is full.
    /// </summary>
    public const int MinimumDurationMinutes = 30;

    /// <summary>
    /// How many times to offer. Five, matching the clarification card: a list
    /// somebody scrolls is a list they stop reading.
    /// </summary>
    public const int MaxTimeOptions = 5;

    /// Times are offered on the half hour. Anything finer is false precision
    /// about when a museum will let somebody in.
    private const int SlotMinutes = 30;

    // ── Reading ──────────────────────────────────────────────────────────

    /// The plan's date, or null when the payload does not carry a valid one.
    public static DateOnly? DateOf(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object) return null;
        if (!payload.TryGetProperty("date", out var element)) return null;
        if (element.ValueKind != JsonValueKind.String) return null;

        return DateOnly.TryParseExact(
            element.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    /// <summary>
    /// The plan's rows.
    ///
    /// Read defensively throughout: this runs on a live turn against a document
    /// the model produced, so a missing or wrongly-typed property is a missing
    /// answer, never an exception.
    /// </summary>
    public static IReadOnlyList<GlunoDraftRow> Rows(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("activities", out var activities)
            || activities.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GlunoDraftRow>();
        }

        var rows = new List<GlunoDraftRow>();
        var index = 0;

        foreach (var entry in activities.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) { index++; continue; }

            var start = ParseTime(Text(entry, "time"));
            var end = ParseTime(Text(entry, "endTime"));

            rows.Add(new GlunoDraftRow
            {
                Index = index++,
                Title = Text(entry, "title") ?? string.Empty,
                Start = start,
                End = end,
                DurationMinutes = Number(entry, "durationMinutes") is { } minutes and >= 5 and <= 720
                    ? (int)minutes
                    : null,
                IsFixed = entry.TryGetProperty("isFixed", out var fixedFlag)
                    && fixedFlag.ValueKind == JsonValueKind.True,
                ExistingActivityId = Guid.TryParse(Text(entry, "existingActivityId"), out var activityId)
                    ? activityId
                    : null,
                Latitude = Number(entry, "latitude"),
                Longitude = Number(entry, "longitude"),
                TravelFromPreviousMinutes = entry.TryGetProperty("travelFromPrevious", out var travel)
                    && travel.ValueKind == JsonValueKind.Object
                        ? Number(travel, "minutes") is { } travelMinutes ? (int)travelMinutes : null
                        : null,
                OpensAt = entry.TryGetProperty("openingHours", out var hours)
                    && hours.ValueKind == JsonValueKind.Object
                        ? ParseTime(Text(hours, "opensAt"))
                        : null,
                ClosesAt = hours.ValueKind == JsonValueKind.Object
                    ? ParseTime(Text(hours, "closesAt"))
                    : null,
            });
        }

        return rows;
    }

    /// <summary>
    /// The row a conflict is about: the last one this suggestion added.
    ///
    /// "The new one" in every strategy name. A locked row is by definition not
    /// what Gluno just suggested, so it is never the answer.
    /// </summary>
    public static GlunoDraftRow? NewestSuggestion(IReadOnlyList<GlunoDraftRow> rows)
        => rows.LastOrDefault(row => !row.IsLocked);

    // ── Which days could hold it ─────────────────────────────────────────

    /// <summary>
    /// The trip days the affected activity could actually be placed on.
    ///
    /// EVERY EXCLUSION HERE IS A FACT, NOT A PREFERENCE. A day outside the trip
    /// is not a day. A day whose hours are known and closed is not a day. A day
    /// already at capacity is not a day. Offering one anyway produces a card
    /// where tapping does nothing, which reads as the product ignoring the user.
    ///
    /// What is NOT excluded: a day that merely looks busy, or one in a
    /// different town. Being somewhere else is a reason to warn, not a reason
    /// to remove the choice — people do take a train back for a concert.
    /// </summary>
    public static IReadOnlyList<DateOnly> AvailableDays(
        GlunoTripContext trip,
        GlunoDraftRow row,
        DateOnly? currentDate,
        int capacityPerDay)
    {
        var days = new List<DateOnly>();
        var end = trip.EndDate ?? trip.EffectiveEndDate;

        for (var date = trip.StartDate; date <= end; date = date.AddDays(1))
        {
            // The day the clash is on. Keeping it would offer the user the
            // option of changing nothing.
            if (currentDate is { } current && date == current) continue;

            var onThatDay = trip.Activities.Where(activity => activity.Date == date).ToList();

            // Already as full as the pace allows. One more would produce the
            // capacity conflict this choice is meant to escape.
            if (onThatDay.Count >= capacityPerDay) continue;

            // A check-in or check-out at the same hour is the same clash again,
            // one day over.
            if (row.Start is { } start && ClashesWithAFixedItem(onThatDay, start, row.EffectiveDuration)) continue;

            days.Add(date);
        }

        return days;
    }

    /// <summary>
    /// Whether a proposed slot collides with something on that day that cannot
    /// move.
    ///
    /// Only rows with a real time count. An activity with no time is not a
    /// statement about when it happens, and treating it as one would rule out
    /// days for no reason.
    /// </summary>
    private static bool ClashesWithAFixedItem(
        IReadOnlyList<GlunoActivityContext> activities, TimeOnly start, int durationMinutes)
    {
        var proposedEnd = start.AddMinutes(durationMinutes);

        foreach (var activity in activities)
        {
            if (ParseTime(activity.Time) is not { } existingStart) continue;

            var existingEnd = ParseTime(activity.EndTime)
                ?? existingStart.AddMinutes(DefaultDurationMinutes);

            if (start < existingEnd && proposedEnd > existingStart) return true;
        }

        return false;
    }

    // ── Which times could hold it ────────────────────────────────────────

    /// <summary>
    /// Valid start times for the affected row, at most five.
    ///
    /// Built by walking the day in half-hour steps and keeping only the slots
    /// that survive every constraint the schedule engine would apply anyway:
    /// the activity's own length, what is already booked, the journey to and
    /// from its neighbours, and the opening hours where they are known.
    ///
    /// THE POINT IS THAT A SHOWN TIME IS A TIME THAT WORKS. The alternative —
    /// offering every half hour and validating on tap — turns a choice into a
    /// guessing game with a round trip per guess.
    /// </summary>
    public static IReadOnlyList<TimeOnly> AvailableTimes(
        IReadOnlyList<GlunoDraftRow> rows,
        GlunoDraftRow row,
        TimeOnly dayStart,
        TimeOnly dayEnd)
    {
        var duration = row.EffectiveDuration;
        var others = rows.Where(other => other.Index != row.Index).ToList();
        var times = new List<TimeOnly>();

        // Only where the hours are actually known. An unknown-hours place is a
        // caveat on the answer, never a reason to hide half the day.
        if (row.OpensAt is { } opens && opens > dayStart) dayStart = opens;
        if (row.ClosesAt is { } closes && closes < dayEnd) dayEnd = closes;

        // The journey either side, so a slot is not offered that leaves no time
        // to reach it or to leave it.
        var travelBefore = row.TravelFromPreviousMinutes ?? 0;
        var travelAfter = rows
            .FirstOrDefault(other => other.Index == row.Index + 1)
            ?.TravelFromPreviousMinutes ?? 0;

        for (var slot = dayStart; slot.AddMinutes(duration) <= dayEnd; slot = slot.AddMinutes(SlotMinutes))
        {
            var slotEnd = slot.AddMinutes(duration);

            // Something already there, with its travel either side.
            if (others.Any(other => Overlaps(other, slot, slotEnd, travelBefore, travelAfter))) continue;

            times.Add(slot);

            if (times.Count >= MaxTimeOptions) break;
        }

        return times;
    }

    /// <summary>
    /// Whether a candidate slot collides with an existing row.
    ///
    /// The travel buffers are applied ASYMMETRICALLY and on purpose: the
    /// journey before this activity eats into the gap in front of it, the
    /// journey after eats into the gap behind. Applying one figure to both
    /// sides would reject slots that genuinely work.
    /// </summary>
    private static bool Overlaps(
        GlunoDraftRow other, TimeOnly start, TimeOnly end, int travelBefore, int travelAfter)
    {
        if (other.Start is not { } otherStart) return false;

        var otherEnd = other.End ?? otherStart.AddMinutes(other.EffectiveDuration);

        return start.AddMinutes(-travelBefore) < otherEnd
            && end.AddMinutes(travelAfter) > otherStart;
    }

    // ── Editing the draft ────────────────────────────────────────────────

    /// <summary>
    /// Moves the whole plan to another date.
    ///
    /// The times stay as they were. A day chosen to escape a clash is a day
    /// where those times were checked to be free, so re-laying them out would
    /// discard the very thing that made the day offerable.
    /// </summary>
    public static string? WithDate(string payloadJson, DateOnly date)
        => Rewrite(payloadJson, (writer, root) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("date")) continue;
                property.WriteTo(writer);
            }

            writer.WriteString("date", date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
        });

    /// <summary>
    /// Moves one row to a new start time, carrying its length with it.
    ///
    /// The end time is recomputed rather than kept: a row whose start moved and
    /// whose end did not is a row that silently changed length, and the next
    /// validation would treat that as the user's intention.
    /// </summary>
    public static string? WithTime(string payloadJson, int index, TimeOnly start, int durationMinutes)
        => RewriteRow(payloadJson, index, (writer, row) =>
        {
            foreach (var property in row.EnumerateObject())
            {
                if (property.NameEquals("time") || property.NameEquals("endTime")) continue;
                property.WriteTo(writer);
            }

            writer.WriteString("time", start.ToString("HH:mm", CultureInfo.InvariantCulture));
            writer.WriteString("endTime",
                start.AddMinutes(durationMinutes).ToString("HH:mm", CultureInfo.InvariantCulture));
        });

    /// <summary>
    /// Makes one row shorter, to the given length.
    ///
    /// Refuses below <see cref="MinimumDurationMinutes"/> and refuses outright
    /// on a locked row — a booking is not Gluno's to trim, and a fifteen-minute
    /// visit is a worse answer than an honest "it doesn't fit".
    /// </summary>
    public static string? WithShortened(string payloadJson, int index, int durationMinutes)
    {
        if (durationMinutes < MinimumDurationMinutes) return null;

        var rows = ReadRows(payloadJson);
        var row = rows?.FirstOrDefault(candidate => candidate.Index == index);

        if (row == null || row.IsLocked) return null;
        // Shortening something to longer than it was is not shortening.
        if (durationMinutes >= row.EffectiveDuration) return null;

        return RewriteRow(payloadJson, index, (writer, element) =>
        {
            foreach (var property in element.EnumerateObject())
            {
                if (property.NameEquals("endTime") || property.NameEquals("durationMinutes")) continue;
                property.WriteTo(writer);
            }

            writer.WriteNumber("durationMinutes", durationMinutes);

            if (row.Start is { } start)
            {
                writer.WriteString("endTime",
                    start.AddMinutes(durationMinutes).ToString("HH:mm", CultureInfo.InvariantCulture));
            }
        });
    }

    /// <summary>
    /// Records an intended change to an Activity that already exists.
    ///
    /// THE WHOLE POINT: this writes to the DRAFT, never to the Adventure. A
    /// suggestion the user has not approved must not move their dinner booking,
    /// so the move is stored as an operation the apply will carry out — atomic,
    /// once, behind the button — and re-validated against the live row then.
    ///
    /// The snapshot fields let apply tell "still as it was" from "somebody
    /// changed it since", which is the difference between honouring the user's
    /// answer and acting on a plan they never saw.
    /// </summary>
    public static string? WithOperation(string payloadJson, GlunoDraftOperation operation)
        => Rewrite(payloadJson, (writer, root) =>
        {
            var existing = root.TryGetProperty("operations", out var operations)
                && operations.ValueKind == JsonValueKind.Array
                    ? operations.EnumerateArray().ToList()
                    : new List<JsonElement>();

            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("operations")) continue;
                property.WriteTo(writer);
            }

            writer.WritePropertyName("operations");
            writer.WriteStartArray();

            // The same Activity twice would be two moves of one thing, and the
            // second would be computed from a position the first invalidated.
            foreach (var previous in existing)
            {
                if (SameTarget(previous, operation)) continue;
                previous.WriteTo(writer);
            }

            JsonSerializer.SerializeToDocument(operation, GlunoJson.Options).RootElement.WriteTo(writer);

            writer.WriteEndArray();
        });

    private static bool SameTarget(JsonElement previous, GlunoDraftOperation operation)
        => previous.ValueKind == JsonValueKind.Object
            && previous.TryGetProperty("activityId", out var activityId)
            && activityId.ValueKind == JsonValueKind.String
            && Guid.TryParse(activityId.GetString(), out var parsed)
            && parsed == operation.ActivityId;

    /// The operations a draft carries, or empty when it carries none.
    public static IReadOnlyList<GlunoDraftOperation> Operations(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("operations", out var operations)
            || operations.ValueKind != JsonValueKind.Array)
        {
            return Array.Empty<GlunoDraftOperation>();
        }

        var result = new List<GlunoDraftOperation>();

        foreach (var entry in operations.EnumerateArray())
        {
            try
            {
                var operation = entry.Deserialize<GlunoDraftOperation>(GlunoJson.Options);
                // An operation naming no Activity cannot be carried out and
                // must not silently become a no-op at apply time.
                if (operation is { ActivityId: var id } && id != Guid.Empty) result.Add(operation);
            }
            catch (JsonException)
            {
                // One unreadable operation does not invalidate the others.
            }
        }

        return result;
    }

    // ── JSON plumbing ────────────────────────────────────────────────────

    private static IReadOnlyList<GlunoDraftRow>? ReadRows(string payloadJson)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            return Rows(document.RootElement);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// Rebuilds the document. Returns null on anything unparseable, because a
    /// payload that cannot be read is a reason to fall back, never to throw on
    /// a live turn.
    private static string? Rewrite(string payloadJson, Action<Utf8JsonWriter, JsonElement> write)
    {
        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                write(writer, document.RootElement);
                writer.WriteEndObject();
            }

            return System.Text.Encoding.UTF8.GetString(stream.ToArray());
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? RewriteRow(string payloadJson, int index, Action<Utf8JsonWriter, JsonElement> write)
        => Rewrite(payloadJson, (writer, root) =>
        {
            foreach (var property in root.EnumerateObject())
            {
                if (property.NameEquals("activities")) continue;
                property.WriteTo(writer);
            }

            writer.WritePropertyName("activities");
            writer.WriteStartArray();

            var position = 0;

            if (root.TryGetProperty("activities", out var activities)
                && activities.ValueKind == JsonValueKind.Array)
            {
                foreach (var row in activities.EnumerateArray())
                {
                    if (position++ != index || row.ValueKind != JsonValueKind.Object)
                    {
                        row.WriteTo(writer);
                        continue;
                    }

                    writer.WriteStartObject();
                    write(writer, row);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        });

    internal static TimeOnly? ParseTime(string? value)
        => TimeOnly.TryParseExact(value, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;
}

/// <summary>
/// A change to an Activity that already exists, recorded on the draft and
/// carried out only at apply.
///
/// WHY IT IS NOT DONE IMMEDIATELY. "Move the existing one" and "replace the
/// existing one" both touch something the user already has. Doing that when
/// they tap a conflict option would be a write from a suggestion they have not
/// approved — and the whole draft flow exists so that no write happens before
/// the Apply button.
///
/// So the intent is stored, shown on the proposal card, and executed inside the
/// apply transaction with everything else. The snapshot fields are re-checked
/// there: an Activity somebody moved or deleted in the meantime makes the whole
/// proposal stale rather than being overwritten.
/// </summary>
public sealed record GlunoDraftOperation
{
    /// move_existing | replace_existing
    public required string Type { get; init; }

    /// The Activity this acts on. Always a real id read from the plan, never
    /// one the model produced.
    public required Guid ActivityId { get; init; }

    /// Where it should end up. Null on a replacement.
    public string? ToDate { get; init; }
    public string? ToTime { get; init; }

    // ── The snapshot apply re-checks ─────────────────────────────────────

    /// Where it was when the user answered, so a change since is detectable.
    public string? FromDate { get; init; }
    public string? FromTime { get; init; }

    /// The title as it stood, so a renamed row is caught too.
    public string? FromTitle { get; init; }
}

public static class GlunoDraftOperationTypes
{
    public const string MoveExisting = "move_existing";
    public const string ReplaceExisting = "replace_existing";

    public static bool IsKnown(string? type) => type is MoveExisting or ReplaceExisting;
}
