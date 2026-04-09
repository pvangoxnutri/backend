using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/trips/{tripId}/expenses")]
[Authorize]
public class ExpensesController : ControllerBase
{
    private readonly AppDbContext _db;

    public ExpensesController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetCurrentUserId()
    {
        var sub = User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? User.FindFirstValue("sub")
            ?? string.Empty;
        return Guid.TryParse(sub, out var id) ? id : Guid.Empty;
    }

    private async Task<bool> IsTripMember(Guid tripId, Guid userId)
        => await _db.TripMembers.AnyAsync(tm => tm.TripId == tripId && tm.UserId == userId);

    private async Task<bool> TripExists(Guid tripId)
        => await _db.Trips.AnyAsync(t => t.Id == tripId);

    private static ExpenseResponseDto MapExpense(Expense expense)
        => new()
        {
            Id = expense.Id,
            TripId = expense.TripId,
            Description = expense.Description,
            TotalAmount = expense.TotalAmount,
            Date = expense.Date,
            SplitMode = expense.SplitMode,
            CreatedAt = expense.CreatedAt,
            CreatedByName = expense.CreatedBy?.Name ?? string.Empty,
            Payers = expense.Payers.Select(p => new ExpensePayerDto
            {
                UserId = p.UserId,
                UserName = p.User?.Name ?? string.Empty,
                Amount = p.Amount,
            }).ToList(),
            Participants = expense.Participants.Select(p => new ExpenseParticipantDto
            {
                UserId = p.UserId,
                UserName = p.User?.Name ?? string.Empty,
                Amount = p.Amount,
            }).ToList(),
        };

    // GET /api/trips/{tripId}/expenses
    [HttpGet]
    public async Task<IActionResult> GetExpenses(Guid tripId)
    {
        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        var expenses = await _db.Expenses
            .Where(e => e.TripId == tripId)
            .Include(e => e.CreatedBy)
            .Include(e => e.Payers).ThenInclude(p => p.User)
            .Include(e => e.Participants).ThenInclude(p => p.User)
            .OrderByDescending(e => e.Date)
            .ThenByDescending(e => e.CreatedAt)
            .ToListAsync();

        return Ok(expenses.Select(MapExpense));
    }

    // POST /api/trips/{tripId}/expenses
    [HttpPost]
    public async Task<IActionResult> CreateExpense(Guid tripId, [FromBody] CreateExpenseDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();
        if (userId == Guid.Empty)
            return Unauthorized();

        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        if (!await IsTripMember(tripId, userId))
            return Forbid();

        if (dto.TotalAmount <= 0)
            return BadRequest("Total amount must be greater than zero.");

        var validModes = new[] { "equal", "exact", "percentage", "shares" };
        if (!validModes.Contains(dto.SplitMode))
            return BadRequest("Invalid split mode.");

        if (dto.Payers == null || dto.Payers.Count == 0)
            return BadRequest("At least one payer is required.");

        if (dto.Participants == null || dto.Participants.Count == 0)
            return BadRequest("At least one participant is required.");

        // Validate payers sum == TotalAmount (within 0.01)
        var payersSum = dto.Payers.Sum(p => p.Amount);
        if (Math.Abs(payersSum - dto.TotalAmount) > 0.01m)
            return BadRequest($"Payers amounts sum ({payersSum}) must equal total amount ({dto.TotalAmount}).");

        // Calculate participant shares
        var participantAmounts = new List<(Guid UserId, decimal Amount)>();
        int count = dto.Participants.Count;

        switch (dto.SplitMode)
        {
            case "equal":
            {
                var share = Math.Round(dto.TotalAmount / count, 2, MidpointRounding.ToEven);
                for (int i = 0; i < count; i++)
                    participantAmounts.Add((dto.Participants[i].UserId, share));
                break;
            }
            case "exact":
            {
                var exactSum = dto.Participants.Sum(p => p.Value);
                if (Math.Abs(exactSum - dto.TotalAmount) > 0.01m)
                    return BadRequest($"Participant exact amounts sum ({exactSum}) must equal total amount ({dto.TotalAmount}).");
                foreach (var p in dto.Participants)
                    participantAmounts.Add((p.UserId, p.Value));
                break;
            }
            case "percentage":
            {
                var pctSum = dto.Participants.Sum(p => p.Value);
                if (Math.Abs(pctSum - 100m) > 0.01m)
                    return BadRequest($"Percentages must sum to 100 (got {pctSum}).");
                foreach (var p in dto.Participants)
                    participantAmounts.Add((p.UserId, Math.Round(dto.TotalAmount * p.Value / 100m, 2, MidpointRounding.ToEven)));
                break;
            }
            case "shares":
            {
                var totalShares = dto.Participants.Sum(p => p.Value);
                if (totalShares <= 0)
                    return BadRequest("Total shares must be greater than zero.");
                foreach (var p in dto.Participants)
                    participantAmounts.Add((p.UserId, Math.Round(dto.TotalAmount * (p.Value / totalShares), 2, MidpointRounding.ToEven)));
                break;
            }
        }

        // Rounding correction: adjust first participant so sum == TotalAmount exactly
        var calculatedSum = participantAmounts.Sum(p => p.Amount);
        var diff = dto.TotalAmount - calculatedSum;
        if (diff != 0 && participantAmounts.Count > 0)
        {
            var first = participantAmounts[0];
            participantAmounts[0] = (first.UserId, first.Amount + diff);
        }

        var expense = new Expense
        {
            TripId = tripId,
            CreatedByUserId = userId,
            Description = dto.Description.Trim(),
            TotalAmount = dto.TotalAmount,
            Date = dto.Date,
            SplitMode = dto.SplitMode,
            CreatedAt = DateTime.UtcNow,
        };

        _db.Expenses.Add(expense);

        foreach (var payer in dto.Payers)
        {
            _db.ExpensePayers.Add(new ExpensePayer
            {
                ExpenseId = expense.Id,
                UserId = payer.UserId,
                Amount = payer.Amount,
            });
        }

        foreach (var (participantUserId, amount) in participantAmounts)
        {
            _db.ExpenseParticipants.Add(new ExpenseParticipant
            {
                ExpenseId = expense.Id,
                UserId = participantUserId,
                Amount = amount,
            });
        }

        await _db.SaveChangesAsync();

        // Reload with navigation
        var saved = await _db.Expenses
            .Where(e => e.Id == expense.Id)
            .Include(e => e.CreatedBy)
            .Include(e => e.Payers).ThenInclude(p => p.User)
            .Include(e => e.Participants).ThenInclude(p => p.User)
            .FirstAsync();

        return CreatedAtAction(nameof(GetExpenses), new { tripId }, MapExpense(saved));
    }

    // DELETE /api/trips/{tripId}/expenses/{expenseId}
    [HttpDelete("{expenseId}")]
    public async Task<IActionResult> DeleteExpense(Guid tripId, Guid expenseId)
    {
        var userId = GetCurrentUserId();
        if (userId == Guid.Empty)
            return Unauthorized();

        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        if (!await IsTripMember(tripId, userId))
            return Forbid();

        var expense = await _db.Expenses
            .FirstOrDefaultAsync(e => e.Id == expenseId && e.TripId == tripId);

        if (expense == null)
            return NotFound("Expense not found.");

        _db.Expenses.Remove(expense);
        await _db.SaveChangesAsync();
        return NoContent();
    }

    // GET /api/trips/{tripId}/expenses/balances
    [HttpGet("balances")]
    public async Task<IActionResult> GetBalances(Guid tripId)
    {
        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        // Get all members
        var members = await _db.TripMembers
            .Where(tm => tm.TripId == tripId)
            .Include(tm => tm.User)
            .ToListAsync();

        // Get all expense payers for this trip
        var payers = await _db.ExpensePayers
            .Where(ep => ep.Expense.TripId == tripId)
            .ToListAsync();

        // Get all expense participants for this trip
        var participants = await _db.ExpenseParticipants
            .Where(ep => ep.Expense.TripId == tripId)
            .ToListAsync();

        // Get all settlements for this trip
        var settlements = await _db.Settlements
            .Where(s => s.TripId == tripId)
            .ToListAsync();

        // Calculate net for each member
        var netMap = new Dictionary<Guid, decimal>();
        foreach (var member in members)
            netMap[member.UserId] = 0m;

        foreach (var payer in payers)
        {
            if (netMap.ContainsKey(payer.UserId))
                netMap[payer.UserId] += payer.Amount;
        }

        foreach (var participant in participants)
        {
            if (netMap.ContainsKey(participant.UserId))
                netMap[participant.UserId] -= participant.Amount;
        }

        foreach (var settlement in settlements)
        {
            if (netMap.ContainsKey(settlement.ToUserId))
                netMap[settlement.ToUserId] += settlement.Amount;
            if (netMap.ContainsKey(settlement.FromUserId))
                netMap[settlement.FromUserId] -= settlement.Amount;
        }

        var memberLookup = members.ToDictionary(m => m.UserId);

        var balances = netMap.Select(kv => new MemberBalanceDto
        {
            UserId = kv.Key,
            UserName = memberLookup.TryGetValue(kv.Key, out var m) ? m.User?.Name ?? string.Empty : string.Empty,
            AvatarUrl = memberLookup.TryGetValue(kv.Key, out var m2) ? m2.User?.AvatarUrl : null,
            Net = Math.Round(kv.Value, 2),
        }).ToList();

        // Debt simplification (greedy)
        var mutableBalances = balances.Select(b => new { b.UserId, b.UserName, Net = b.Net }).ToList();
        var debts = new List<DebtDto>();

        var working = mutableBalances.Select(b => (b.UserId, b.UserName, Net: b.Net)).ToList();

        while (true)
        {
            var creditors = working.Where(b => b.Net > 0.005m).OrderByDescending(b => b.Net).ToList();
            var debtors = working.Where(b => b.Net < -0.005m).OrderBy(b => b.Net).ToList();

            if (creditors.Count == 0 || debtors.Count == 0)
                break;

            var creditor = creditors[0];
            var debtor = debtors[0];
            var amount = Math.Min(creditor.Net, -debtor.Net);
            amount = Math.Round(amount, 2);

            if (amount <= 0)
                break;

            debts.Add(new DebtDto
            {
                FromUserId = debtor.UserId,
                FromUserName = debtor.UserName,
                ToUserId = creditor.UserId,
                ToUserName = creditor.UserName,
                Amount = amount,
            });

            // Update working balances
            var creditorIdx = working.FindIndex(b => b.UserId == creditor.UserId);
            var debtorIdx = working.FindIndex(b => b.UserId == debtor.UserId);
            working[creditorIdx] = (creditor.UserId, creditor.UserName, Math.Round(creditor.Net - amount, 2));
            working[debtorIdx] = (debtor.UserId, debtor.UserName, Math.Round(debtor.Net + amount, 2));
        }

        return Ok(new BalancesResponseDto
        {
            Balances = balances,
            SimplifiedDebts = debts,
        });
    }

    // GET /api/trips/{tripId}/expenses/settlements
    [HttpGet("settlements")]
    public async Task<IActionResult> GetSettlements(Guid tripId)
    {
        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        var settlements = await _db.Settlements
            .Where(s => s.TripId == tripId)
            .Include(s => s.FromUser)
            .Include(s => s.ToUser)
            .OrderByDescending(s => s.CreatedAt)
            .ToListAsync();

        return Ok(settlements.Select(s => new SettlementResponseDto
        {
            Id = s.Id,
            TripId = s.TripId,
            FromUserId = s.FromUserId,
            FromUserName = s.FromUser?.Name ?? string.Empty,
            ToUserId = s.ToUserId,
            ToUserName = s.ToUser?.Name ?? string.Empty,
            Amount = s.Amount,
            Note = s.Note,
            CreatedAt = s.CreatedAt,
        }));
    }

    // POST /api/trips/{tripId}/expenses/settlements
    [HttpPost("settlements")]
    public async Task<IActionResult> CreateSettlement(Guid tripId, [FromBody] CreateSettlementDto dto)
    {
        if (!ModelState.IsValid)
            return BadRequest(ModelState);

        var userId = GetCurrentUserId();
        if (userId == Guid.Empty)
            return Unauthorized();

        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        if (!await IsTripMember(tripId, userId))
            return Forbid();

        if (!await IsTripMember(tripId, dto.FromUserId))
            return BadRequest("FromUser is not a trip member.");

        if (!await IsTripMember(tripId, dto.ToUserId))
            return BadRequest("ToUser is not a trip member.");

        if (dto.Amount <= 0)
            return BadRequest("Amount must be greater than zero.");

        var settlement = new Settlement
        {
            TripId = tripId,
            FromUserId = dto.FromUserId,
            ToUserId = dto.ToUserId,
            Amount = dto.Amount,
            Note = string.IsNullOrWhiteSpace(dto.Note) ? null : dto.Note.Trim(),
            CreatedAt = DateTime.UtcNow,
        };

        _db.Settlements.Add(settlement);
        await _db.SaveChangesAsync();

        var fromUser = await _db.Users.FindAsync(dto.FromUserId);
        var toUser = await _db.Users.FindAsync(dto.ToUserId);

        return CreatedAtAction(nameof(GetSettlements), new { tripId }, new SettlementResponseDto
        {
            Id = settlement.Id,
            TripId = settlement.TripId,
            FromUserId = settlement.FromUserId,
            FromUserName = fromUser?.Name ?? string.Empty,
            ToUserId = settlement.ToUserId,
            ToUserName = toUser?.Name ?? string.Empty,
            Amount = settlement.Amount,
            Note = settlement.Note,
            CreatedAt = settlement.CreatedAt,
        });
    }

    // DELETE /api/trips/{tripId}/expenses/settlements/{settlementId}
    [HttpDelete("settlements/{settlementId}")]
    public async Task<IActionResult> DeleteSettlement(Guid tripId, Guid settlementId)
    {
        var userId = GetCurrentUserId();
        if (userId == Guid.Empty)
            return Unauthorized();

        if (!await TripExists(tripId))
            return NotFound("Trip not found.");

        if (!await IsTripMember(tripId, userId))
            return Forbid();

        var settlement = await _db.Settlements
            .FirstOrDefaultAsync(s => s.Id == settlementId && s.TripId == tripId);

        if (settlement == null)
            return NotFound("Settlement not found.");

        _db.Settlements.Remove(settlement);
        await _db.SaveChangesAsync();
        return NoContent();
    }
}
