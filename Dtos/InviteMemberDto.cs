using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class InviteMemberDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;
}
