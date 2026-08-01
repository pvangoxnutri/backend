using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

/// <summary>
/// The manual trigger for ChatImageBackfillService, and the ONLY way it runs.
///
/// Development-only, on purpose. Moving existing chat photos between buckets is
/// a data migration that rewrites rows and deletes objects, so it must be a
/// deliberate act performed by someone who can watch it and stop it — not
/// something a deploy can start by itself.
///
/// Outside Development every route here answers 404, so the endpoint does not
/// exist in production even to someone who knows the path.
/// </summary>
[ApiController]
[Route("api/dev/chat-image-backfill")]
[Authorize]
public class ChatImageBackfillController : ControllerBase
{
    private readonly ChatImageBackfillService _backfill;
    private readonly IWebHostEnvironment _env;

    public ChatImageBackfillController(ChatImageBackfillService backfill, IWebHostEnvironment env)
    {
        _backfill = backfill;
        _env = env;
    }

    /// <summary>
    /// Runs one bounded pass. Defaults to a dry run: pass dryRun=false only
    /// when the dry run's numbers look right. Keep calling it while the
    /// response reports moreRemaining=true — each pass is independent, and
    /// stopping between passes loses nothing.
    /// </summary>
    [HttpPost]
    public async Task<ActionResult<ChatImageBackfillResult>> Run(
        [FromQuery] int batchSize = 50,
        [FromQuery] bool dryRun = true,
        CancellationToken cancellationToken = default)
    {
        if (!_env.IsDevelopment()) return NotFound();

        var result = await _backfill.RunAsync(batchSize, dryRun, cancellationToken);
        return Ok(result);
    }
}
