using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class VerifyEmailDto
{
    [Required]
    public string Token { get; set; } = string.Empty;
}
