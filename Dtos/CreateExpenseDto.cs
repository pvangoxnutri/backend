using System.ComponentModel.DataAnnotations;

namespace sidequest.backend.Dtos;

public class CreateExpenseDto
{
    [Required]
    [MaxLength(200)]
    public string Description { get; set; } = string.Empty;

    [Required]
    public decimal TotalAmount { get; set; }

    [Required]
    public DateOnly Date { get; set; }

    [Required]
    public string SplitMode { get; set; } = "equal"; // equal | exact | percentage | shares

    [Required]
    public List<ExpensePayerInputDto> Payers { get; set; } = new();

    [Required]
    public List<ExpenseParticipantInputDto> Participants { get; set; } = new();
}

public class ExpensePayerInputDto
{
    public Guid UserId { get; set; }
    public decimal Amount { get; set; }
}

public class ExpenseParticipantInputDto
{
    public Guid UserId { get; set; }
    public decimal Value { get; set; } // percentage, shares, or exact amount depending on SplitMode
}
