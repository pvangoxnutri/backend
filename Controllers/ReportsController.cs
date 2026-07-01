using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Data;
using sidequest.backend.Models;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ReportsController : ControllerBase
{
    private readonly AppDbContext _db;

    public ReportsController(AppDbContext db)
    {
        _db = db;
    }

    private Guid GetUserId() => Guid.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier)!);

    // POST /api/reports
    [HttpPost]
    public async Task<ActionResult> CreateReport([FromBody] CreateReportDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.ContentType) || string.IsNullOrWhiteSpace(dto.ContentId))
            return BadRequest("ContentType and ContentId are required.");

        var validTypes = new[] { "user", "chat_message", "activity" };
        if (!validTypes.Contains(dto.ContentType))
            return BadRequest("Invalid ContentType.");

        var validReasons = new[] { "spam", "harassment", "offensive", "explicit", "other" };
        var reason = validReasons.Contains(dto.Reason) ? dto.Reason : "other";

        var report = new UserReport
        {
            ReporterId = GetUserId(),
            ContentType = dto.ContentType,
            ContentId = dto.ContentId.Trim(),
            Reason = reason,
            Notes = dto.Notes?.Trim(),
        };

        _db.UserReports.Add(report);
        await _db.SaveChangesAsync(ct);

        return Created("", new { id = report.Id });
    }
}

public class CreateReportDto
{
    public string ContentType { get; set; } = string.Empty;
    public string ContentId { get; set; } = string.Empty;
    public string Reason { get; set; } = "other";
    public string? Notes { get; set; }
}
