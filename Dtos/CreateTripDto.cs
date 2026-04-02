using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateTripDto
{
    [Required]
    [MaxLength(200)]
    public string Title { get; set; } = string.Empty;

    public string? Description { get; set; }

    [Required]
    [MaxLength(200)]
    public string Destination { get; set; } = string.Empty;

    public string? ImageUrl { get; set; }
    public string? InviteCode { get; set; }

    [Required]
    public DateOnly StartDate { get; set; }

    [Required]
    public DateOnly EndDate { get; set; }

    // Hidden SideQuest fields
    public string Visibility { get; set; } = "public"; // "public" | "hidden"
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }
}
