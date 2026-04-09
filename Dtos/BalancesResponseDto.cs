namespace sidequest.backend.Dtos;

public class BalancesResponseDto
{
    public List<MemberBalanceDto> Balances { get; set; } = new();
    public List<DebtDto> SimplifiedDebts { get; set; } = new();
}

public class MemberBalanceDto
{
    public Guid UserId { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
    public decimal Net { get; set; }
}

public class DebtDto
{
    public Guid FromUserId { get; set; }
    public string FromUserName { get; set; } = string.Empty;
    public Guid ToUserId { get; set; }
    public string ToUserName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
}
