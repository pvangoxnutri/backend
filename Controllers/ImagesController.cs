using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ImagesController : ControllerBase
{
    private readonly IWebHostEnvironment _env;

    public ImagesController(IWebHostEnvironment env)
    {
        _env = env;
    }

    [HttpPost("upload")]
    [Authorize]
    public async Task<ActionResult<object>> Upload(IFormFile file)
    {
        if (file == null || file.Length == 0)
            return BadRequest("No file uploaded.");

        var allowed = new[] { "image/jpeg", "image/png", "image/gif", "image/webp" };
        if (!allowed.Contains(file.ContentType))
            return BadRequest("Only image files are allowed.");

        if (file.Length > 10 * 1024 * 1024)
            return BadRequest("File must be smaller than 10 MB.");

        var uploadsDir = Path.Combine(
            _env.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
            "uploads");
        Directory.CreateDirectory(uploadsDir);

        var ext = Path.GetExtension(file.FileName);
        var fileName = $"{Guid.NewGuid()}{ext}";
        var filePath = Path.Combine(uploadsDir, fileName);

        using var stream = System.IO.File.Create(filePath);
        await file.CopyToAsync(stream);

        var url = $"{Request.Scheme}://{Request.Host}/uploads/{fileName}";
        return Ok(new { url });
    }
}
