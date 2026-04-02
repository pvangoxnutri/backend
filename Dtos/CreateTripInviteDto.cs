using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateTripInviteDto
{
    [Required]
    [EmailAddress]
    [MaxLength(320)]
    public string Email { get; set; } = string.Empty;
}
