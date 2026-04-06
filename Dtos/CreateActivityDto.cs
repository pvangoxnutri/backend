using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateActivityDto
{
    [Required]
    public DateOnly Date { get; set; }

    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string? Time { get; set; }

    public string? Category { get; set; }

    public string? ImageUrl { get; set; }

    public string? SpotifyUrl { get; set; }

    public string Visibility { get; set; } = "public";

    public DateTime? RevealAt { get; set; }

    public string? Teaser { get; set; }

    public int? TeaserOffsetMinutes { get; set; }
}
