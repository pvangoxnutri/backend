namespace sidequest.backend.Models;

public class Expense
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TripId { get; set; }
    public Trip Trip { get; set; } = null!;
    public Guid CreatedByUserId { get; set; }
    public User CreatedBy { get; set; } = null!;
    public string Description { get; set; } = string.Empty;
    public decimal TotalAmount { get; set; }
    public DateOnly Date { get; set; }
    public string SplitMode { get; set; } = "equal"; // equal | exact | percentage | shares
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public List<ExpensePayer> Payers { get; set; } = new();
    public List<ExpenseParticipant> Participants { get; set; } = new();
}
