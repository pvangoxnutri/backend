namespace sidequest.backend.Models;

public class ExpensePayer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExpenseId { get; set; }
    public Expense Expense { get; set; } = null!;
    public Guid UserId { get; set; }
    public User User { get; set; } = null!;
    public decimal Amount { get; set; }
}
