using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class EmailRequestDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
