namespace sidequest.backend.Models;

public class TripDocument
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;

    public string Name { get; set; } = string.Empty;
    public string Category { get; set; } = "other";
    public string FileType { get; set; } = string.Empty;
    public long FileSize { get; set; }

    // Internal key in the private document bucket. This is never returned by
    // an API DTO; clients receive a short-lived signed URL when opening.
    public string StoragePath { get; set; } = string.Empty;

    public DateOnly? Date { get; set; }

    public Guid? ActivityId { get; set; }
    public TripActivity? Activity { get; set; }

    public string? Note { get; set; }
    public string? BookingReference { get; set; }

    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;

    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
