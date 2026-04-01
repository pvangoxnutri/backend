namespace sidequest.backend.Models;

public class User
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public string Role { get; set; } = "user"; // "user" | "admin"
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
