namespace sidequest.backend.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public bool HasCompletedOnboarding { get; set; } = false;
    public string? FoundVia { get; set; }
    public string? Purpose { get; set; }
    public string? PurposeOtherText { get; set; }
    public string Role { get; set; } = "user"; // "user" | "admin"
    public bool IsBanned { get; set; } = false;
    public string Language { get; set; } = "en"; // "en" | "sv" — drives push notification text
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    // Bumped (throttled) on every authenticated request — powers the green
    // online dot. Null for users who haven't been seen since the column landed.
    public DateTime? LastSeenAt { get; set; }
}
