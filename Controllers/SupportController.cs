using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Models;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/support")]
public class SupportController : ControllerBase
{
    private readonly AppDbContext _db;
    private readonly INotificationDispatchService _notifications;
    private readonly IConfiguration _config;

    public SupportController(AppDbContext db, INotificationDispatchService notifications, IConfiguration config)
    {
        _db = db;
        _notifications = notifications;
        _config = config;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    private bool IsAdminAuthorized()
    {
        var key = HttpContext.Request.Headers["X-Admin-Key"].FirstOrDefault();
        var expected = _config["Admin:ApiKey"];
        return !string.IsNullOrWhiteSpace(expected) && key == expected;
    }

    // ── User endpoints ───────────────────────────────────────────────────────

    // POST /api/support/tickets
    [HttpPost("tickets")]
    [Authorize]
    public async Task<ActionResult> CreateTicket([FromBody] CreateTicketDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Subject))
            return BadRequest("Subject is required.");
        if (string.IsNullOrWhiteSpace(dto.Body))
            return BadRequest("Description is required.");

        var validCategories = new[] { "bug", "feature", "account", "billing", "report_user", "other" };
        var category = validCategories.Contains(dto.Category) ? dto.Category : "other";

        var ticket = new SupportTicket
        {
            UserId = GetUserId(),
            Category = category,
            Subject = dto.Subject.Trim(),
            Status = "open",
        };
        _db.SupportTickets.Add(ticket);

        var message = new SupportMessage
        {
            TicketId = ticket.Id,
            SenderType = "user",
            Body = dto.Body.Trim(),
        };
        _db.SupportMessages.Add(message);

        foreach (var url in dto.AttachmentUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Take(10))
        {
            _db.SupportAttachments.Add(new SupportAttachment
            {
                MessageId = message.Id,
                FileUrl = url,
                FileType = InferMimeType(url),
            });
        }

        await _db.SaveChangesAsync(ct);
        return Created("", new { id = ticket.Id });
    }

    // GET /api/support/tickets
    [HttpGet("tickets")]
    [Authorize]
    public async Task<ActionResult<List<SupportTicketSummaryDto>>> ListTickets(CancellationToken ct)
    {
        var userId = GetUserId();
        var tickets = await _db.SupportTickets
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new SupportTicketSummaryDto
            {
                Id = t.Id,
                Category = t.Category,
                Subject = t.Subject,
                Status = t.Status,
                HasUnreadAdminReply = t.HasUnreadAdminReply,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                MessageCount = t.Messages.Count,
            })
            .ToListAsync(ct);

        return Ok(tickets);
    }

    // GET /api/support/tickets/{id}
    [HttpGet("tickets/{id:guid}")]
    [Authorize]
    public async Task<ActionResult<SupportTicketDetailDto>> GetTicket(Guid id, CancellationToken ct)
    {
        var userId = GetUserId();
        var ticket = await _db.SupportTickets
            .Include(t => t.Messages)
            .ThenInclude(m => m.Attachments)
            .FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);

        if (ticket == null) return NotFound();

        if (ticket.HasUnreadAdminReply)
        {
            ticket.HasUnreadAdminReply = false;
            await _db.SaveChangesAsync(ct);
        }

        return Ok(MapTicketDetail(ticket));
    }

    // POST /api/support/tickets/{id}/messages
    [HttpPost("tickets/{id:guid}/messages")]
    [Authorize]
    public async Task<ActionResult> AddUserMessage(Guid id, [FromBody] AddMessageDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Body))
            return BadRequest("Message body is required.");

        var userId = GetUserId();
        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id && t.UserId == userId, ct);
        if (ticket == null) return NotFound();
        if (ticket.Status == "closed")
            return BadRequest("This ticket is closed.");

        var message = new SupportMessage
        {
            TicketId = ticket.Id,
            SenderType = "user",
            Body = dto.Body.Trim(),
        };
        _db.SupportMessages.Add(message);

        foreach (var url in dto.AttachmentUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Take(10))
        {
            _db.SupportAttachments.Add(new SupportAttachment
            {
                MessageId = message.Id,
                FileUrl = url,
                FileType = InferMimeType(url),
            });
        }

        ticket.Status = "open";
        ticket.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        return Created("", new { id = message.Id });
    }

    // ── Admin endpoints (X-Admin-Key header) ─────────────────────────────────

    // GET /api/admin/support/tickets
    [HttpGet("~/api/admin/support/tickets")]
    public async Task<ActionResult<List<AdminTicketSummaryDto>>> AdminListTickets(
        [FromQuery] string? status, [FromQuery] string? search, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var query = _db.SupportTickets
            .Include(t => t.User)
            .Include(t => t.Messages)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(status) && status != "all")
            query = query.Where(t => t.Status == status);

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.ToLower();
            query = query.Where(t =>
                t.Subject.ToLower().Contains(s) ||
                t.User.Name.ToLower().Contains(s) ||
                t.User.Email.ToLower().Contains(s));
        }

        var tickets = await query
            .OrderByDescending(t => t.UpdatedAt)
            .Select(t => new AdminTicketSummaryDto
            {
                Id = t.Id,
                UserName = t.User.Name,
                UserEmail = t.User.Email,
                UserAvatarUrl = t.User.AvatarUrl,
                Category = t.Category,
                Subject = t.Subject,
                Status = t.Status,
                CreatedAt = t.CreatedAt,
                UpdatedAt = t.UpdatedAt,
                MessageCount = t.Messages.Count,
            })
            .ToListAsync(ct);

        return Ok(tickets);
    }

    // GET /api/admin/support/tickets/{id}
    [HttpGet("~/api/admin/support/tickets/{id:guid}")]
    public async Task<ActionResult<AdminTicketDetailDto>> AdminGetTicket(Guid id, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var ticket = await _db.SupportTickets
            .Include(t => t.User)
            .Include(t => t.Messages)
            .ThenInclude(m => m.Attachments)
            .FirstOrDefaultAsync(t => t.Id == id, ct);

        if (ticket == null) return NotFound();

        return Ok(new AdminTicketDetailDto
        {
            Id = ticket.Id,
            Category = ticket.Category,
            Subject = ticket.Subject,
            Status = ticket.Status,
            CreatedAt = ticket.CreatedAt,
            UpdatedAt = ticket.UpdatedAt,
            ClosedAt = ticket.ClosedAt,
            User = new AdminTicketUserDto
            {
                Id = ticket.User.Id,
                Name = ticket.User.Name,
                Email = ticket.User.Email,
                AvatarUrl = ticket.User.AvatarUrl,
            },
            Messages = ticket.Messages
                .OrderBy(m => m.CreatedAt)
                .Select(m => new SupportMessageDto
                {
                    Id = m.Id,
                    SenderType = m.SenderType,
                    Body = m.Body,
                    CreatedAt = m.CreatedAt,
                    Attachments = m.Attachments.Select(a => new SupportAttachmentDto
                    {
                        Id = a.Id,
                        FileUrl = a.FileUrl,
                        FileType = a.FileType,
                    }).ToList(),
                }).ToList(),
        });
    }

    // POST /api/admin/support/tickets/{id}/messages
    [HttpPost("~/api/admin/support/tickets/{id:guid}/messages")]
    public async Task<ActionResult> AdminReply(Guid id, [FromBody] AddMessageDto dto, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();
        if (string.IsNullOrWhiteSpace(dto.Body))
            return BadRequest("Message body is required.");

        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound();

        var message = new SupportMessage
        {
            TicketId = ticket.Id,
            SenderType = "admin",
            Body = dto.Body.Trim(),
        };
        _db.SupportMessages.Add(message);

        foreach (var url in dto.AttachmentUrls.Where(u => !string.IsNullOrWhiteSpace(u)).Take(10))
        {
            _db.SupportAttachments.Add(new SupportAttachment
            {
                MessageId = message.Id,
                FileUrl = url,
                FileType = InferMimeType(url),
            });
        }

        ticket.Status = "waiting_for_reply";
        ticket.HasUnreadAdminReply = true;
        ticket.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(ct);

        await _notifications.SendSupportReplyAsync(ticket, ct);

        return Created("", new { id = message.Id });
    }

    // PATCH /api/admin/support/tickets/{id}/status
    [HttpPatch("~/api/admin/support/tickets/{id:guid}/status")]
    public async Task<ActionResult> AdminSetStatus(Guid id, [FromBody] SetStatusDto dto, CancellationToken ct)
    {
        if (!IsAdminAuthorized()) return Unauthorized();

        var validStatuses = new[] { "open", "waiting_for_reply", "closed" };
        if (!validStatuses.Contains(dto.Status))
            return BadRequest("Invalid status.");

        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound();

        ticket.Status = dto.Status;
        ticket.UpdatedAt = DateTime.UtcNow;
        if (dto.Status == "closed")
            ticket.ClosedAt = DateTime.UtcNow;
        else if (ticket.ClosedAt.HasValue)
            ticket.ClosedAt = null;

        await _db.SaveChangesAsync(ct);
        return Ok(new { status = ticket.Status });
    }

    // POST /api/internal/support/tickets/{id}/notify-reply
    // Called by the website admin after it inserts an admin reply via Supabase.
    // Authenticated by the Supabase service role key so no extra env var is needed.
    [HttpPost("~/api/internal/support/tickets/{id:guid}/notify-reply")]
    public async Task<ActionResult> NotifyReply(Guid id, CancellationToken ct)
    {
        var bearer = HttpContext.Request.Headers["Authorization"].FirstOrDefault();
        var key = bearer?.StartsWith("Bearer ") == true ? bearer[7..] : null;
        var expected = _config["Supabase:ServiceRoleKey"];
        if (string.IsNullOrWhiteSpace(expected) || key != expected)
            return Unauthorized();

        var ticket = await _db.SupportTickets.FirstOrDefaultAsync(t => t.Id == id, ct);
        if (ticket == null) return NotFound();

        await _notifications.SendSupportReplyAsync(ticket, ct);
        return Ok();
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static SupportTicketDetailDto MapTicketDetail(SupportTicket ticket) => new()
    {
        Id = ticket.Id,
        Category = ticket.Category,
        Subject = ticket.Subject,
        Status = ticket.Status,
        CreatedAt = ticket.CreatedAt,
        Messages = ticket.Messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => new SupportMessageDto
            {
                Id = m.Id,
                SenderType = m.SenderType,
                Body = m.Body,
                CreatedAt = m.CreatedAt,
                Attachments = m.Attachments.Select(a => new SupportAttachmentDto
                {
                    Id = a.Id,
                    FileUrl = a.FileUrl,
                    FileType = a.FileType,
                }).ToList(),
            }).ToList(),
    };

    private static string InferMimeType(string url)
    {
        var lower = url.ToLower();
        if (lower.Contains(".png")) return "image/png";
        if (lower.Contains(".gif")) return "image/gif";
        if (lower.Contains(".webp")) return "image/webp";
        return "image/jpeg";
    }
}

// ── DTOs ─────────────────────────────────────────────────────────────────────

public class CreateTicketDto
{
    public string Category { get; set; } = "other";
    public string Subject { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public List<string> AttachmentUrls { get; set; } = new();
}

public class AddMessageDto
{
    public string Body { get; set; } = string.Empty;
    public List<string> AttachmentUrls { get; set; } = new();
}

public class SetStatusDto
{
    public string Status { get; set; } = string.Empty;
}

public class SupportTicketSummaryDto
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public bool HasUnreadAdminReply { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}

public class SupportTicketDetailDto
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<SupportMessageDto> Messages { get; set; } = new();
}

public class SupportMessageDto
{
    public Guid Id { get; set; }
    public string SenderType { get; set; } = string.Empty;
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public List<SupportAttachmentDto> Attachments { get; set; } = new();
}

public class SupportAttachmentDto
{
    public Guid Id { get; set; }
    public string FileUrl { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
}

public class AdminTicketSummaryDto
{
    public Guid Id { get; set; }
    public string UserName { get; set; } = string.Empty;
    public string UserEmail { get; set; } = string.Empty;
    public string? UserAvatarUrl { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public int MessageCount { get; set; }
}

public class AdminTicketDetailDto
{
    public Guid Id { get; set; }
    public string Category { get; set; } = string.Empty;
    public string Subject { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public DateTime? ClosedAt { get; set; }
    public AdminTicketUserDto User { get; set; } = null!;
    public List<SupportMessageDto> Messages { get; set; } = new();
}

public class AdminTicketUserDto
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string? AvatarUrl { get; set; }
}
