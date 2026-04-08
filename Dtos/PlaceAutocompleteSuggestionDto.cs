namespace sidequest.backend.Dtos;

public class PlaceAutocompleteSuggestionDto
{
    public string PlaceId { get; set; } = string.Empty;
    public string PrimaryText { get; set; } = string.Empty;
    public string? SecondaryText { get; set; }
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string? GoogleMapsUri { get; set; }
}
