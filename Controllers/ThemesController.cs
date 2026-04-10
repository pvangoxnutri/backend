using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using sidequest.backend.Data;
using sidequest.backend.Dtos;

namespace sidequest.backend.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class ThemesController : ControllerBase
{
    private readonly AppDbContext _db;

    public ThemesController(AppDbContext db)
    {
        _db = db;
    }

    [HttpGet]
    public async Task<ActionResult<List<ThemeResponseDto>>> GetThemes(CancellationToken cancellationToken)
    {
        var themes = await _db.Themes
            .OrderBy(t => t.Name)
            .Select(t => new ThemeResponseDto
            {
                Id = t.Id,
                Name = t.Name,
                PrimaryColor = t.PrimaryColor,
                SecondaryColor = t.SecondaryColor,
            })
            .ToListAsync(cancellationToken);

        return Ok(themes);
    }
}
