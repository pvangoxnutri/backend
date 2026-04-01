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
}
