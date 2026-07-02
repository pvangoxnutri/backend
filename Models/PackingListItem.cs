namespace sidequest.backend.Models;

public class PackingListItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CategoryId { get; set; }
    public PackingListCategory Category { get; set; } = null!;
    public string Text { get; set; } = string.Empty;
    public bool IsChecked { get; set; }
    public int SortOrder { get; set; }
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    public Guid? CheckedByUserId { get; set; }
    public DateTime? CheckedAt { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
