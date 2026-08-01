namespace sidequest.backend.Dtos;

public class UpdateTripDto
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? Destination { get; set; }
    // Picked-place coordinates. Null = leave untouched;
    // ClearDestinationCoordinates = the destination was re-typed as free
    // text, so stored coordinates no longer describe it.
    public double? DestinationLatitude { get; set; }
    public double? DestinationLongitude { get; set; }
    public string? DestinationPlaceId { get; set; }
    public bool ClearDestinationCoordinates { get; set; }
    public string? ImageUrl { get; set; }
    public string? SpotifyUrl { get; set; }
    public bool ClearImage { get; set; }
    public bool ClearSpotifyUrl { get; set; }
    public DateOnly? StartDate { get; set; }
    // Null here means "leave untouched", the same convention every other
    // optional field on this DTO uses — it does NOT mean "remove the end
    // date". Switching an adventure to open-ended is an explicit act, so it
    // has its own flag, mirroring ClearRevealAt / ClearImage below.
    public DateOnly? EndDate { get; set; }
    public bool ClearEndDate { get; set; }
    public List<string>? Countries { get; set; }

    // Hidden SideQuest fields
    public string? Visibility { get; set; }
    public DateTime? RevealAt { get; set; }
    public bool ClearRevealAt { get; set; } = false;
    public string? Teaser { get; set; }

    // Owner-controlled: whether non-owner members may edit activities.
    public bool? MembersCanEdit { get; set; }

    // Slideshow cover on/off. Null = leave untouched.
    public bool? SlideshowEnabled { get; set; }
}
