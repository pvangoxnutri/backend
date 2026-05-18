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
builder.Services.AddHttpClient<ISupabaseStorageService, SupabaseStorageService>();

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
                        var role = await db.Users
                            .Where(u => u.Id == userId)
                            .Select(u => u.Role)
                            .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                        if (!string.IsNullOrWhiteSpace(role) && !identity.HasClaim(ClaimTypes.Role, role))
                        {
                            identity.AddClaim(new Claim(ClaimTypes.Role, role));
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
        app.Logger.LogWarning(ex, "Database migration skipped at startup.");
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

// TEMPORARY: diagnostic endpoint to verify which connection string Railway
// has injected (host/user/ssl + password presence only — never the password
// itself) and whether the DB is reachable. Remove once Supabase password
// rotation is confirmed working in production.
app.MapGet("/health/db", async (IConfiguration configuration) =>
{
    var raw = configuration.GetConnectionString("DefaultConnection");
    if (string.IsNullOrWhiteSpace(raw))
    {
        return Results.Ok(new
        {
            configured = false,
            message = "ConnectionStrings:DefaultConnection is empty or missing.",
        });
    }

    string? host = null;
    int? port = null;
    string? database = null;
    string? username = null;
    string? sslMode = null;
    bool passwordPresent = false;
    int passwordLength = 0;
    string? parseError = null;

    try
    {
        var builder = new NpgsqlConnectionStringBuilder(raw);
        host = builder.Host;
        port = builder.Port;
        database = builder.Database;
        username = builder.Username;
        sslMode = builder.SslMode.ToString();
        passwordPresent = !string.IsNullOrEmpty(builder.Password);
        passwordLength = builder.Password?.Length ?? 0;
    }
    catch (Exception ex)
    {
        parseError = ex.GetType().Name + ": " + ex.Message;
    }

    bool dbOk = false;
    string? errorType = null;
    string? sqlState = null;
    string? errorMessage = null;

    try
    {
        await using var conn = new NpgsqlConnection(raw);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT 1", conn);
        var result = await cmd.ExecuteScalarAsync();
        dbOk = result is int i && i == 1;
    }
    catch (PostgresException pgEx)
    {
        errorType = nameof(PostgresException);
        sqlState = pgEx.SqlState;
        errorMessage = pgEx.MessageText;
    }
    catch (NpgsqlException npgEx)
    {
        errorType = nameof(NpgsqlException);
        errorMessage = npgEx.Message;
    }
    catch (Exception ex)
    {
        errorType = ex.GetType().Name;
        errorMessage = ex.Message;
    }

    return Results.Ok(new
    {
        configured = true,
        connection = new
        {
            host,
            port,
            database,
            username,
            sslMode,
            passwordPresent,
            passwordLength,
            parseError,
        },
        dbOk,
        error = errorType is null ? null : new
        {
            type = errorType,
            sqlState,
            message = errorMessage,
        },
        timestamp = DateTime.UtcNow,
    });
});

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
