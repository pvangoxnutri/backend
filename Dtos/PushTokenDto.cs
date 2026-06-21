using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class RegisterPushTokenDto
{
    [Required]
    public string Token { get; set; } = string.Empty;

    public string Platform { get; set; } = "unknown";
}
