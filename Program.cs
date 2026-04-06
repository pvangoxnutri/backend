using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using sidequest.backend.Data;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers();

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

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
                    var role = await db.Users
                        .Where(u => u.Id == userId)
                        .Select(u => u.Role)
                        .FirstOrDefaultAsync(context.HttpContext.RequestAborted);

                    if (!string.IsNullOrWhiteSpace(role) && !identity.HasClaim(ClaimTypes.Role, role))
                    {
                        identity.AddClaim(new Claim(ClaimTypes.Role, role));
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

                  if (origin.StartsWith("http://localhost:", StringComparison.OrdinalIgnoreCase) ||
                      origin.StartsWith("https://localhost:", StringComparison.OrdinalIgnoreCase))
                  {
                      return true;
                  }

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
    db.Database.Migrate();
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

app.Run();
