namespace sidequest.backend.Dtos;

public class AuthResponseDto
{
    public string Token { get; set; } = string.Empty;
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public bool EmailVerified { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public bool HasCompletedOnboarding { get; set; }
    public string Role { get; set; } = "user";
    public string Language { get; set; } = "en";
}
