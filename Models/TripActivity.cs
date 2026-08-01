namespace sidequest.backend.Models;

public class TripActivity
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public DateOnly Date { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string? Time { get; set; }
    // Multi-day stay support (hotel reservations): Date/Time act as
    // check-in, EndDate/EndTime as check-out. Null EndDate = ordinary
    // single-day activity — every pre-existing row keeps its behavior.
    // The structure is category-agnostic; only the hotel UX exposes it.
    public DateOnly? EndDate { get; set; }
    public string? EndTime { get; set; }
    // Manual drag-to-reorder position within (TripId, Date). Defaults to the
    // end of that day's list on create; only the reorder endpoint rewrites it
    // afterwards, so a manual arrangement survives unrelated edits.
    public int SortIndex { get; set; }
    // The selected symbol/category key (built-in key like "food"/"car", or
    // "sidequest"). For a CUSTOM category this still holds the chosen
    // existing symbol key; CustomCategoryLabel below carries the user's name.
    public string? Category { get; set; }
    // User-entered display name for a custom category. Null for built-in
    // categories (which render their localized label from Category instead).
    public string? CustomCategoryLabel { get; set; }
    public string? ImageUrl { get; set; }
    // Keeps this activity's photo out of the trip's slideshow while leaving it
    // fully visible on the activity itself. False (the default, and what every
    // pre-existing row migrates to) means "include", so nothing changes for
    // anything that already exists. Meaningless without an ImageUrl — there is
    // then nothing to exclude — and deliberately NOT stored as a description
    // marker like blur/location are: this is a real property of the activity,
    // not text the user wrote.
    public bool ExcludeFromSlideshow { get; set; }
    public string? SpotifyUrl { get; set; }
    public string Visibility { get; set; } = "public";
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }
    public int? TeaserOffsetMinutes { get; set; }
    public bool IsHidden { get; set; } = false;
    public DateTime? RevealedAt { get; set; }
    public Guid OwnerId { get; set; }
    public User Owner { get; set; } = null!;
    public Guid? AssignedToUserId { get; set; }
    public User? AssignedTo { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
