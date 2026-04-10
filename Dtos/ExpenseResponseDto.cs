namespace sidequest.backend.Dtos;

public class ExpenseResponseDto
{
    public Guid Id { get; set; }
    public Guid TripId { get; set; }
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateOnly Date { get; set; }
    public string SplitMode { get; set; } = string.Empty;
    public string Currency { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public string CreatedByName { get; set; } = string.Empty;
    public List<ExpensePayerDto> Payers { get; set; } = new();
    public List<ExpenseParticipantDto> Participants { get; set; } = new();
}

public class ExpensePayerDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}

public class ExpenseParticipantDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
