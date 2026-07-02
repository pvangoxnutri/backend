namespace sidequest.backend.Models;

public class PackingListCategory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public string Name { get; set; } = string.Empty;
    public string Scope { get; set; } = "shared"; // "shared" | "private"
    public Guid? UserId { get; set; } // set for private categories
    public int SortOrder { get; set; }
    public Guid CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<PackingListItem> Items { get; set; } = new();
}
