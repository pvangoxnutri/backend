using System.Collections.Concurrent;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using sidequest.backend.Data;
using sidequest.backend.Services;

var builder = WebApplication.CreateBuilder(args);
LoadLocalEnvFile(Path.Combine(builder.Environment.ContentRootPath, ".env.local"));

// Avoid Windows EventLog writes in this local environment; console logs are enough for dev.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddControllers();
builder.Services.AddHttpClient<LinkPreviewService>();
builder.Services.AddHttpClient<WeatherService>();
builder.Services.AddHttpClient<ISupabaseStorageService, SupabaseStorageService>();
builder.Services.AddHttpClient<IExpoPushService, ExpoPushService>();
builder.Services.AddScoped<INotificationDispatchService, NotificationDispatchService>();
// Trip-invite emails to addresses without a SideQuest account — sent via
// the same Resend account/domain as the website's auth/approval mails
// (RESEND_API_KEY). Without the key it logs the intended mail and returns.
builder.Services.AddHttpClient<IEmailSender, ResendEmailSender>();

// Always runs — it's also what claims "sidequest_revealed"/"teaser"
// NotificationLog rows that the in-app notification center reads (see
// NotificationsController). Push:Enabled only gates the actual Expo send
// inside NotificationDispatchService.ClaimAndSendAsync, so this scheduler
// being on does NOT mean real pushes go out to real users while the flag is
// off — that go-live step (flip the env var, no redeploy needed) still
// stands; it now just controls delivery, not whether the notification is
// recorded at all.
builder.Services.AddHostedService<RevealNotificationScheduler>();
var pushNotificationsEnabled = builder.Configuration.GetValue<bool>("Push:Enabled");

// Unrelated to push — runs regardless of Push:Enabled. Keeps TripEvents
// and ChatMessages from growing forever (see DataRetentionScheduler).
builder.Services.AddHostedService<DataRetentionScheduler>();

var configuredConnectionString = builder.Configuration.GetConnectionString("DefaultConnection")
    ?? throw new InvalidOperationException("ConnectionStrings:DefaultConnection must be configured.");

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(configuredConnectionString)
           .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.RelationalEventId.PendingModelChangesWarning)));

var supabaseUrl = builder.Configuration["Supabase:Url"]?.TrimEnd('/');
var supabaseAudience = builder.Configuration["Supabase:JwtAudience"] ?? "authenticated";

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        if (string.IsNullOrWhiteSpace(supabaseUrl))
            throw new InvalidOperationException("Supabase:Url must be configured.");

        options.Authority = $"{supabaseUrl}/auth/v1";
        options.MapInboundClaims = false;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = $"{supabaseUrl}/auth/v1",
            ValidAudience = supabaseAudience,
            NameClaimType = ClaimTypes.NameIdentifier,
            RoleClaimType = ClaimTypes.Role
        };
        if (builder.Environment.IsDevelopment())
        {
            // Local fallback when metadata/JWKS fetch is blocked; keep issuer/audience/lifetime checks.
            options.RequireHttpsMetadata = false;
            options.UseSecurityTokenValidators = true;
            options.TokenValidationParameters.ValidateIssuerSigningKey = false;
            options.TokenValidationParameters.RequireSignedTokens = false;
            options.TokenValidationParameters.SignatureValidator = (token, _) => new JwtSecurityToken(token);
        }
        options.Events = new JwtBearerEvents
        {
            OnTokenValidated = async context =>
            {
                var identity = context.Principal?.Identity as ClaimsIdentity;
                if (identity == null)
                    return;

                var subject = context.Principal?.FindFirst("sub")?.Value;
                if (!string.IsNullOrWhiteSpace(subject) && !identity.HasClaim(ClaimTypes.NameIdentifier, subject))
                {
                    identity.AddClaim(new Claim(ClaimTypes.NameIdentifier, subject));
                }

                var email = context.Principal?.FindFirst("email")?.Value;
                if (!string.IsNullOrWhiteSpace(email) && !identity.HasClaim(ClaimTypes.Email, email))
                {
                    identity.AddClaim(new Claim(ClaimTypes.Email, email));
                }

                var db = context.HttpContext.RequestServices.GetRequiredService<AppDbContext>();
                if (Guid.TryParse(subject, out var userId))
                {
                    try
                    {
                        var userData = await db.Users
                            .Where(u => u.Id == userId)
                            .Select(u => new { u.Role, u.IsBanned })
                            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (!string.IsNullOrWhiteSpace(userData?.Role) && !identity.HasClaim(ClaimTypes.Role, userData.Role))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, userData.Role));
                        }

                        if (userData?.IsBanned == true)
                        {
                            identity.AddClaim(new Claim("sidequest:banned", "true"));
                        }
                    }
                    catch
                    {
                        // Keep auth flow alive even when role lookup storage is temporarily unreachable.
                    }
                }
            }
        };
    });

builder.Services.AddAuthorization();

builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("LocalFrontend", policy =>
    {
        policy.SetIsOriginAllowed(origin =>
              {
                  if (string.IsNullOrWhiteSpace(origin))
                      return false;

                  // Production web origins
                  if (origin.Equals("https://sidequesttravel.app", StringComparison.OrdinalIgnoreCase) ||
                      origin.Equals("https://www.sidequesttravel.app", StringComparison.OrdinalIgnoreCase) ||
                      origin.Equals("https://api.sidequesttravel.app", StringComparison.OrdinalIgnoreCase))
                  {
                      return true;
                  }

                  // Local development
                  if (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                      origin.StartsWith("https://localhost:", StringComparison.OrdinalIgnoreCase))
                  {
                      return true;
                  }

                  // Expo Go tunnels
                  if (origin.Contains(".exp.direct", StringComparison.OrdinalIgnoreCase) ||
                      origin.Contains(".trycloudflare.com", StringComparison.OrdinalIgnoreCase))
                  {
                      return true;
                  }

                  return false;
              })
              .AllowAnyHeader()
              .AllowAnyMethod();
    });
});

var app = builder.Build();

app.Logger.LogInformation(
    "Push notifications actual Expo delivery: {Status}. NotificationLog rows (in-app notification center) are always recorded regardless of this flag. Manual test-send is unaffected by this flag.",
    pushNotificationsEnabled ? "ENABLED" : "disabled (set Push__Enabled=true to turn on)");

// Run pending migrations and ensure uploads directory exists
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    try
    {
        db.Database.Migrate();
    }
    catch (Exception ex)
    {
        // Swallowing this used to let the app boot against a stale schema —
        // every request touching the unmigrated columns then 500'd with no
        // indication why. Fail loudly instead so a broken migration shows up
        // as a crashed deploy, not a silently broken API.
        app.Logger.LogCritical(ex, "Database migration failed at startup.");
        throw;
    }
}

var uploadsPath = Path.Combine(
    app.Environment.WebRootPath ?? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot"),
    "uploads");
Directory.CreateDirectory(uploadsPath);

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseStaticFiles();
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}
app.UseCors("LocalFrontend");
app.UseAuthentication();

// Return 403 immediately for banned users — lets the mobile app kick them out
// mid-session without waiting for a restart. /api/auth/* is exempt so the
// sync endpoint can still return isBanned:true for the graceful startup path.
app.Use(async (ctx, next) =>
{
    if (ctx.User.Identity?.IsAuthenticated == true
        && ctx.User.HasClaim("sidequest:banned", "true")
        && !ctx.Request.Path.StartsWithSegments("/api/auth"))
    {
        ctx.Response.StatusCode = 403;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync("{\"error\":\"banned\"}");
        return;
    }
    await next();
});

// Presence: any authenticated request counts as "seen", powering the green
// online dot. Writes are throttled per user via an in-memory map (single
// instance deployment) so busy screens don't turn every request into an
// UPDATE — at most one write per user per 60s. Failures are swallowed:
// presence must never break a real request.
var lastSeenWrites = new ConcurrentDictionary<Guid, DateTime>();
app.Use(async (ctx, next) =>
{
    var idClaim = ctx.User.Identity?.IsAuthenticated == true
        ? ctx.User.FindFirstValue(ClaimTypes.NameIdentifier)
        : null;
    if (idClaim != null && Guid.TryParse(idClaim, out var seenUserId))
    {
        var now = DateTime.UtcNow;
        var lastWrite = lastSeenWrites.GetValueOrDefault(seenUserId);
        if (now - lastWrite > TimeSpan.FromSeconds(60))
        {
            lastSeenWrites[seenUserId] = now;
            try
            {
                var db = ctx.RequestServices.GetRequiredService<AppDbContext>();
                await db.Users
                    .Where(u => u.Id == seenUserId)
                    .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastSeenAt, now));
            }
            catch
            {
                // Roll back the throttle stamp so the next request retries.
                lastSeenWrites.TryRemove(seenUserId, out _);
            }
        }
    }
    await next();
});

app.UseAuthorization();
app.MapControllers();

// Lightweight health endpoint for load balancers / uptime checks.
app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "sidequest-backend",
    environment = app.Environment.EnvironmentName,
    timestamp = DateTime.UtcNow
}));


app.Run();

static void LoadLocalEnvFile(string path)
{
    if (!File.Exists(path))
        return;

    foreach (var rawLine in File.ReadAllLines(path))
    {
        var line = rawLine.Trim();
        if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#"))
            continue;

        var separator = line.IndexOf('=');
        if (separator <= 0)
            continue;

        var key = line[..separator].Trim();
        if (string.IsNullOrWhiteSpace(key))
            continue;

        var value = line[(separator + 1)..].Trim().Trim('"');
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}
