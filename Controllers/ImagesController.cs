using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using sidequest.backend.Services;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private const long MaxBytes = 10L * 1024L * 1024L; // 10 MB

    private readonly ISupabaseStorageService _storage;

    public ImagesController(ISupabaseStorageService storage)
    {
        _storage = storage;
    }

    // Stores bytes and hands back a URL. It deliberately takes no tripId,
    // activityId or messageId: this endpoint only writes to the bucket, and the
    // endpoint that later ATTACHES the returned URL to a trip, activity, chat
    // message or profile is where membership is checked. A URL on its own
    // grants no access to any adventure.
    [HttpPost("upload")]
    [Authorize]
    public async Task<ActionResult<object>> Upload([FromForm] IFormFile file, CancellationToken cancellationToken)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        if (file.Length > MaxBytes)
            return BadRequest("File must be smaller than 10 MB.");

        await using var stream = file.OpenReadStream();

        // The CONTENT decides the type — not the multipart Content-Type header
        // and not the filename. Both come from the client, so neither says
        // anything about the bytes. Rejecting everything but these four raster
        // formats also keeps SVG and HTML out of a bucket that is served
        // publicly, which is where they would otherwise become stored XSS.
        var detected = await ImageFileValidator.DetectAsync(stream, cancellationToken);
        if (detected == null)
            return BadRequest("Only JPEG, PNG, GIF and WebP images are allowed.");

        // DetectAsync consumed the header. Rewinding is required or the stored
        // object would be missing its first bytes; a stream that cannot rewind
        // is refused rather than silently truncated.
        if (!stream.CanSeek)
            return BadRequest("The upload could not be processed. Please try again.");
        stream.Position = 0;

        var url = await _storage.UploadAsync(stream, detected.ContentType, detected.Extension, cancellationToken);

        return Ok(new { url });
    }

    // DELETE /api/images?url=… used to live here. It was [Authorize]d, but that
    // only proved the caller was signed in — there was no ownership check at
    // all, so any user could delete any file in the bucket just by naming its
    // URL, including other adventures' photos. A bare URL carries no owner to
    // check against, and nothing in the app ever called it: image cleanup runs
    // through the owning resource (activity, trip, profile), which does verify
    // membership. Removing the endpoint is the fix — reinstating it would need
    // an owning id plus a membership check, not just [Authorize].
}
