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

    // Set only when the user picked a place suggestion — free-text
    // destinations legitimately have no coordinates.
    public double? DestinationLatitude { get; set; }
    public double? DestinationLongitude { get; set; }
    [MaxLength(300)]
    public string? DestinationPlaceId { get; set; }

    public string? ImageUrl { get; set; }
    public string? InviteCode { get; set; }
    public List<string> Countries { get; set; } = new();

    [Required]
    public DateOnly StartDate { get; set; }

    [Required]
    public DateOnly EndDate { get; set; }

    // Hidden SideQuest fields
    public string Visibility { get; set; } = "public"; // "public" | "hidden"
    public DateTime? RevealAt { get; set; }
    public string? Teaser { get; set; }

    // Slideshow cover — defaults on; older clients that omit the field get
    // the default.
    public bool SlideshowEnabled { get; set; } = true;
}
