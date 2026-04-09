using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateSettlementDto
{
    [Required]
    public Guid FromUserId { get; set; }

    [Required]
    public Guid ToUserId { get; set; }

    [Required]
    public decimal Amount { get; set; }

    public string? Note { get; set; }
}
