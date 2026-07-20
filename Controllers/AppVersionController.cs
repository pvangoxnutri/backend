using Microsoft.AspNetCore.Mvc;

namespace sidequest.backend.Controllers;

// ── GET /api/app/version ──────────────────────────────────────────────────
// Anonymous: tells installed apps what the latest store release is so they
// can offer the in-app update dialog. Values come from configuration
// (AppVersion:* — overridable via AppVersion__* env vars on Railway) with
// the current release compiled in as fallback, so advertising a new
// version is a config change, not a code change.
[ApiController]
[Route("api/app")]
public class AppVersionController : ControllerBase
{
    private const string DefaultLatest = "1.0.4";
    private const string DefaultIosStoreUrl = "https://apps.apple.com/app/id6770268183";
    private const string DefaultAndroidStoreUrl = "https://play.google.com/store/apps/details?id=app.sidequesttravel.mobile";

    private readonly IConfiguration _config;

    public AppVersionController(IConfiguration config)
    {
        _config = config;
    }

    [HttpGet("version")]
    public ActionResult<AppVersionDto> GetVersion() => Ok(new AppVersionDto
    {
        Ios = new PlatformVersionDto
        {
            Latest = _config["AppVersion:Ios:Latest"] ?? DefaultLatest,
            StoreUrl = _config["AppVersion:Ios:StoreUrl"] ?? DefaultIosStoreUrl,
        },
        Android = new PlatformVersionDto
        {
            Latest = _config["AppVersion:Android:Latest"] ?? DefaultLatest,
            StoreUrl = _config["AppVersion:Android:StoreUrl"] ?? DefaultAndroidStoreUrl,
        },
    });
}

public class AppVersionDto
{
    public PlatformVersionDto Ios { get; set; } = new();
    public PlatformVersionDto Android { get; set; } = new();
}

public class PlatformVersionDto
{
    public string Latest { get; set; } = string.Empty;
    public string StoreUrl { get; set; } = string.Empty;
}
