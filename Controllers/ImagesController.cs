using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private static readonly string[] AllowedContentTypes =
        { "image/jpeg", "image/png", "image/gif", "image/webp" };

    private const long MaxBytes = 10L * 1024L * 1024L; // 10 MB

    private readonly ISupabaseStorageService _storage;

    public ImagesController(ISupabaseStorageService storage)
    {
        _storage = storage;
    }

    [HttpPost("upload")]
    [Authorize]
    public async Task<ActionResult<object>> Upload([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (!AllowedContentTypes.Contains(file.ContentType))
            return BadRequest("Only image files are allowed.");

        if (file.Length > MaxBytes)
            return BadRequest("File must be smaller than 10 MB.");

        await using var stream = file.OpenReadStream();
        var url = await _storage.UploadAsync(stream, file.ContentType, file.FileName, cancellationToken);

        return Ok(new { url });
    }

    [HttpDelete]
    [Authorize]
    public async Task<ActionResult> Delete([FromQuery] string url, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(url))
            return BadRequest("url query parameter is required.");

        var ok = await _storage.DeleteByUrlAsync(url, cancellationToken);
        return ok ? NoContent() : NotFound();
    }
}
