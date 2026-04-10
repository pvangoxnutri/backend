namespace sidequest.backend.Dtos;

public class UpdateProfileDto
{
    public string? Name { get; set; }
    public string? AvatarUrl { get; set; }
    public string? Bio { get; set; }
    public bool? HasCompletedOnboarding { get; set; }
    public string? FoundVia { get; set; }
    public string? Purpose { get; set; }
    public string? PurposeOtherText { get; set; }
    public string? ThemeId { get; set; }
}
