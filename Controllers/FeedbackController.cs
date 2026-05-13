using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Data;
using sidequest.backend.Models;
using System.Security.Claims;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/feedback")]
public class FeedbackController : ControllerBase
{
    private readonly AppDbContext _context;

    public FeedbackController(AppDbContext context)
    {
        _context = context;
    }

    [HttpPost]
    public async Task<ActionResult<FeedbackDto>> Submit([FromBody] SubmitFeedbackRequest request)
    {
        try
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? "anonymous";
            sidequest.backend.Models.User? user = null;
            if (Guid.TryParse(userId, out var userGuid))
            {
                user = await _context.Users.FindAsync(userGuid);
            }

            var feedback = new Feedback
            {
                Id = Guid.NewGuid().ToString(),
                UserId = userId,
                Email = user?.Email ?? "unknown@example.com",
                Name = user?.Name ?? "Anonymous",
                Type = request.Type?.Trim() ?? "feedback",
                Message = request.Message?.Trim() ?? "",
                CreatedAt = DateTime.UtcNow,
            };

            _context.Feedbacks.Add(feedback);
            await _context.SaveChangesAsync();

            return Ok(new FeedbackDto
            {
                Id = feedback.Id,
                Email = feedback.Email,
                Name = feedback.Name,
                Type = feedback.Type,
                Message = feedback.Message,
                CreatedAt = feedback.CreatedAt,
            });
        }
        catch (Exception ex)
        {
            return StatusCode(500, new { error = ex.Message });
        }
    }
}

public class SubmitFeedbackRequest
{
    public string? Type { get; set; }
    public string? Message { get; set; }
}

public class FeedbackDto
{
    public string Id { get; set; } = null!;
    public string Email { get; set; } = null!;
    public string Name { get; set; } = null!;
    public string Type { get; set; } = null!;
    public string Message { get; set; } = null!;
    public DateTime CreatedAt { get; set; }
}
