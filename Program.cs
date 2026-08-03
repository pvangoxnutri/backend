using System.Collections.Concurrent;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using sidequest.backend.Data;
using sidequest.backend.Services;
using sidequest.backend.Services.Gluno;

// Must run BEFORE CreateBuilder: the environment-variable configuration
// provider snapshots Environment.GetEnvironmentVariable at builder-creation
// time, so values set afterward (as this used to do) never reach
// builder.Configuration during a plain `dotnet run` — only appeared to work
// via `dotnet ef`, which loads .env.local itself (DesignTimeDbContextFactory)
// instead of going through this file at all. Real environment variables set
// before the process starts still win — LoadLocalEnvFile only fills gaps.
LoadLocalEnvFile(Path.Combine(Directory.GetCurrentDirectory(), ".env.local"));

var builder = WebApplication.CreateBuilder(args);

// Avoid Windows EventLog writes in this local environment; console logs are enough for dev.
builder.Logging.ClearProviders();
builder.Logging.AddConsole();

builder.Services.AddControllers();
builder.Services.AddHttpClient<LinkPreviewService>();
builder.Services.AddHttpClient<WeatherService>();
builder.Services.AddScoped<TripDayLocationService>();
// Where a trip is on each day, loaded AND resolved in one place. Weather and
// Gluno both go through it, so the cities on the weather screen and the stops
// Gluno describes cannot come from different rows.
builder.Services.AddScoped<ITripResolvedLocationTimelineService, TripResolvedLocationTimelineService>();
builder.Services.AddHttpClient<ISupabaseStorageService, SupabaseStorageService>();
builder.Services
    .AddHttpClient<ITripDocumentStorageService, TripDocumentStorageService>()
    // Default HttpClientFactory logging includes the request URI. Private
    // document URIs contain the storage key, which must never reach logs.
    .RemoveAllLoggers();
builder.Services
    .AddHttpClient<IChatImageStorageService, ChatImageStorageService>()
    // Same reason: these request URIs carry the private chat object path, and
    // the signing response carries a working short-lived URL.
    .RemoveAllLoggers();
// Create-if-absent only, never modifies an existing bucket, never blocks
// startup. See the class comment for why that is safe to run on every boot.
builder.Services.AddHostedService<ChatImageBucketProvisioner>();
// Registered so it can be injected, NOT scheduled. Its only trigger is the
// Development-gated ChatImageBackfillController.
builder.Services.AddScoped<ChatImageBackfillService>();
// ── Gluno ────────────────────────────────────────────────────────────────
// Layered on purpose (see Services/Gluno/): context builder → action executor
// → AI provider → chat orchestrator. Each depends only on the interface below
// it, which is what keeps "Gluno may only propose" enforceable in one place
// instead of being a convention spread across the app.
//
// The AI provider is a singleton because it owns one HTTP client to the model
// API; everything that touches the database is scoped.
builder.Services.AddSingleton<IGlunoAiProvider, AnthropicGlunoAiProvider>();
// External travel data. Adding a provider is one registration here; nothing
// above the registry knows which providers exist.
builder.Services.AddSingleton<TravelDataCache>();
builder.Services
    .AddHttpClient(TripadvisorTravelProvider.HttpClientName)
    // MANDATORY, not an optimisation. Tripadvisor's Content API takes the API
    // key as a QUERY PARAMETER, so every request URI contains the secret — and
    // the default HttpClientFactory logging writes request URIs at Information
    // level. Removing these loggers is what keeps the key out of the logs.
    .RemoveAllLoggers();
// A named client rather than a typed one: the provider is a singleton (the
// registry holds it), and a typed HttpClient captured in a singleton pins one
// message handler forever.
// Tripadvisor Terra — the platform replacing the Content API. Registered
// FIRST so the registry prefers it; see TravelDataRegistry for how the two are
// kept from both running.
//
// No RemoveAllLoggers() here, and that is not an oversight: Terra takes the key
// as an X-API-Key HEADER, so its request URIs carry no secret. The header is
// set per request and never logged.
builder.Services
    .AddHttpClient(TerraTravelProvider.HttpClientName)
    .ConfigureHttpClient(client =>
    {
        client.DefaultRequestHeaders.UserAgent.ParseAdd("SideQuest/1.0");
    });
builder.Services.AddSingleton<ITravelDataProvider, TerraTravelProvider>();
builder.Services.AddSingleton<ITravelDataProvider, TripadvisorTravelProvider>();
builder.Services.AddSingleton<ITravelDataRegistry, TravelDataRegistry>();
// Verified travel times. Same shape as the travel-data registration above, and
// for the same reasons: named client (the provider is a singleton), loggers
// removed (an API key rides in a header and request URIs are logged by
// default), and OFF unless Routing:Enabled is set — deploying this must not
// start calling a paid API by itself.
builder.Services
    .AddHttpClient(GoogleRoutingProvider.HttpClientName)
    .RemoveAllLoggers();
builder.Services.AddSingleton<IRoutingProvider, GoogleRoutingProvider>();
// Scoped, because its per-turn call budget IS the request lifetime.
builder.Services.AddScoped<IRoutingService, RoutingService>();
// Deterministic planning: how long things take, and how a day is laid out.
// Neither is the model's job — see the class comments.
builder.Services.AddSingleton<ActivityDurationTable>();
builder.Services.AddSingleton<DayScheduleEngine>();
builder.Services.AddScoped<IDayPlanPlanner, DayPlanPlanner>();
builder.Services.AddSingleton<GlunoAvailability>();
// Per-user ceiling on external provider calls. A backstop against runaway
// usage, not a plan tier — see the class comment.
builder.Services.AddSingleton<GlunoUsageLimiter>();
builder.Services.AddScoped<IGlunoContextBuilder, GlunoContextBuilder>();
// Gluno's memory for how someone wants to travel — planning preferences only,
// on an allow-listed set of keys. See GlunoPreferenceKeys.
builder.Services.AddScoped<IGlunoPreferenceService, GlunoPreferenceService>();
builder.Services.AddScoped<IGlunoActionExecutor, GlunoActionExecutor>();
// Decision quality: the deterministic layers that decide what a turn is allowed
// to do, resolve what the user pointed at, and check the answer before it goes
// out. All stateless except the working-state store.
builder.Services.AddSingleton<GlunoQualityGate>();
// Model selection, turn planning, usage ceilings and context budgeting. All
// deterministic, all configuration-driven — no model id is hardcoded anywhere
// in the Gluno implementation.
builder.Services.AddSingleton<GlunoModelPolicy>();
builder.Services.AddSingleton<GlunoTurnPlanner>();
builder.Services.AddSingleton<GlunoContextBudget>();
// Singleton: the usage windows ARE process state, like the presence throttle.
builder.Services.AddSingleton<GlunoUsageBudget>();
builder.Services.AddScoped<IGlunoIdempotencyStore, GlunoIdempotencyStore>();
// Live travel information — strikes, closures, events, holidays. OFF by
// default (Gluno__LiveInfo__Enabled). Retrieval happens on the model provider's
// side, so this backend never fetches a URL chosen by a model or a web page.
builder.Services.AddSingleton<ILiveTravelInformationProvider, WebSearchLiveTravelProvider>();
// Scoped: its per-turn search budget IS the request lifetime.
builder.Services.AddScoped<ILiveTravelRegistry, LiveTravelRegistry>();
// Group planning: the shared profile, decisions and polls. Only trip_shared
// preferences ever reach the profile — see TripPlanningProfile.
builder.Services.AddScoped<ITripPlanningProfileBuilder, TripPlanningProfileBuilder>();
builder.Services.AddScoped<IGlunoGroupDecisionService, GlunoGroupDecisionService>();
// Learning from what the user does. NOT model training — see
// GlunoFeedbackService: append-only product data, narrowest scope, and nothing
// influences a plan until the user confirms it.
builder.Services.AddScoped<IGlunoFeedbackService, GlunoFeedbackService>();
// Document understanding. OFF by default (Gluno__Documents__Enabled) like every
// other external integration — shipping the code must not start reading
// people's booking confirmations.
builder.Services.AddSingleton<GlunoDocumentConfig>();
builder.Services.AddSingleton<GlunoDocumentValidator>();
builder.Services.AddSingleton<IGlunoDocumentReader, AnthropicGlunoDocumentReader>();
builder.Services.AddScoped<IGlunoDocumentAnalysisService, GlunoDocumentAnalysisService>();
// Removes working files a crashed analysis left behind. A leaked temp file is
// a private document outside the storage system built to protect it.
builder.Services.AddHostedService<GlunoDocumentTempSweeper>();
// Grounding: the deterministic check that no number reaches the user without
// a ledger entry behind it. See GlunoGroundingValidator for why a prompt alone
// cannot achieve this.
builder.Services.AddSingleton<GlunoGroundingValidator>();
builder.Services.AddScoped<IGlunoWorkingStateStore, GlunoWorkingStateStore>();
builder.Services.AddScoped<IGlunoConversationService, GlunoConversationService>();
builder.Services.AddScoped<IGlunoClarificationService, GlunoClarificationService>();
// Suggestions mid-negotiation. Nothing here writes to an Adventure — a draft
// is a conversation about a change, not the change.
builder.Services.AddScoped<IGlunoProposalDraftService, GlunoProposalDraftService>();
// Fetches a place again from an id, for providers whose terms allow keeping the
// id and not the content. Scoped rather than singleton: it makes upstream calls
// on the caller's own cancellation token.
builder.Services.AddScoped<IGlunoPlaceRehydrator, GlunoPlaceRehydrator>();
builder.Services.AddScoped<IGlunoChatService, GlunoChatService>();
// One instance per HTTP request: the Gluno middleware mints the request id,
// the controller and service stamp branch facts onto it, and the middleware
// writes the one summary line whatever happens.
builder.Services.AddScoped<GlunoRequestDiagnostics>();
// The proposal half: the store records what Gluno suggested, and the apply
// service is the ONLY thing that turns one into a real change — always behind
// an explicit user action, never from the model.
builder.Services.AddScoped<IGlunoProposalStore, GlunoProposalStore>();
builder.Services.AddScoped<IGlunoProposalApplyService, GlunoProposalApplyService>();

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

// Development-only. Lets a token rejection be told apart from a transport
// problem at a glance: if the phone's Supabase project ref differs from the one
// printed here, every authenticated call is a 401 and no amount of network
// debugging will help. Issuer, audience and project ref only — never a key,
// never a JWT.
if (app.Environment.IsDevelopment())
{
    var projectRef = supabaseUrl is null
        ? "(unset)"
        : new Uri(supabaseUrl).Host.Split('.').FirstOrDefault() ?? "(unknown)";
    app.Logger.LogInformation(
        "[DEV] Auth expects issuer={Issuer} audience={Audience} supabaseProjectRef={ProjectRef}. The mobile app's EXPO_PUBLIC_SUPABASE_URL must carry the same project ref.",
        $"{supabaseUrl}/auth/v1", supabaseAudience, projectRef);
}

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

// ── The Gluno error-contract boundary ────────────────────────────────────────
//
// THE BUG THIS CLOSES. A production turn failed and the app showed its generic
// "could not answer" line instead of the provider error. The service layer
// already maps every exception to the structured envelope — but an exception
// in the CONTROLLER (mapping the result) or in RESPONSE SERIALIZATION escaped
// to Kestrel, which answers 500 with an EMPTY body. No code, no retry flag —
// exactly the shape the app can only render generically.
//
// This wraps everything downstream for /api/gluno so no exception can leave
// without the JSON envelope, stamps the request id on every response, and
// writes the one per-request summary line whatever the outcome.
//
// Type name only in the log — an exception message can carry a connection
// string, a request URI or a row's contents.
app.Use(async (ctx, next) =>
{
    if (!ctx.Request.Path.StartsWithSegments("/api/gluno"))
    {
        await next();
        return;
    }

    var glunoDiagnostics = ctx.RequestServices.GetRequiredService<GlunoRequestDiagnostics>();
    ctx.Response.Headers["X-Gluno-Request-Id"] = glunoDiagnostics.RequestId;

    try
    {
        await next();
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        if (ctx.RequestAborted.IsCancellationRequested)
        {
            // The caller went away — nobody is reading the body, and a
            // cancellation must never be dressed up as a server failure.
            glunoDiagnostics.ErrorCode = GlunoFailureCodes.Cancelled;
            return;
        }

        glunoDiagnostics.ErrorCode = GlunoFailureCodes.AiMalformedResponse;
        app.Logger.LogError(
            "[GLUNO] request escaped type={Category} requestId={RequestId}",
            ex.GetType().Name, glunoDiagnostics.RequestId);

        // Headers already on the wire — a second body would corrupt the
        // response, so the envelope cannot be written. Rethrowing at least
        // keeps the failure visible to the host instead of half-answering.
        if (ctx.Response.HasStarted) throw;

        // Existing headers (the request id stamped above, CORS) are KEPT —
        // only the status, a possibly stale length and the content type
        // change. Clearing everything here would strip headers other
        // middleware already negotiated.
        ctx.Response.StatusCode = GlunoErrors.StatusFor(GlunoFailureCodes.AiMalformedResponse);
        ctx.Response.Headers.ContentLength = null;
        ctx.Response.ContentType = "application/json";
        await ctx.Response.WriteAsync(System.Text.Json.JsonSerializer.Serialize(
            GlunoErrors.Body(
                GlunoFailureCodes.AiMalformedResponse,
                retryable: true,
                responseOrigin: glunoDiagnostics.ResponseOrigin,
                requestId: glunoDiagnostics.RequestId)));
    }
    finally
    {
        // The one line per request, whatever happened. Guarded so the
        // summary itself can never replace the real outcome with a logging
        // failure.
        try { glunoDiagnostics.WriteSummary(app.Logger, ctx.Response.StatusCode); }
        catch { /* a diagnostics line is never worth a request */ }
    }
});

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
    // Requests the app fires while it is NOT on screen (late background
    // timers, in-flight work finishing after an app switch) carry this
    // header and must not count as "seen" — a backgrounded app is not
    // online. Older clients never send the header, so they keep the
    // previous behavior unchanged.
    var isBackgroundRequest = ctx.Request.Headers.ContainsKey("X-App-Background");
    if (!isBackgroundRequest && idClaim != null && Guid.TryParse(idClaim, out var seenUserId))
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

// Lightweight health endpoint for load balancers / uptime checks — and the
// neutral probe that separates "the phone cannot reach this process at all"
// from "it reached the API and the request itself failed".
//
// Deliberately touches nothing: no [Authorize], no database query, no Supabase
// call. If this answers, the transport chain (Wi-Fi → LAN address → port →
// Kestrel) is intact and any remaining failure is above it. It exposes only a
// fixed status string, the service name, the environment name and the server
// clock — no user data, and nothing that changes production behaviour.
// Which BINARY is answering.
//
// Without this there is no way to tell a deployed fix from a stale image
// except by guessing from behaviour — which is exactly how an afternoon gets
// spent debugging code that is not running. Railway injects
// RAILWAY_GIT_COMMIT_SHA into every deployment, so this needs no build
// argument and no configuration.
//
// Short SHA only. Never the branch, the repository, a path, or anything about
// how the environment is configured.
var buildSha = (Environment.GetEnvironmentVariable("RAILWAY_GIT_COMMIT_SHA")
        ?? Environment.GetEnvironmentVariable("GIT_COMMIT_SHA"))
    is { Length: > 0 } sha
        ? sha[..Math.Min(7, sha.Length)]
        : "unknown";

// ── Process lifecycle diagnostics ────────────────────────────────────────
//
// THE ONE QUESTION THESE ANSWER. When a container disappears mid-request
// there are two very different causes and they look identical from outside:
// the platform asked the process to stop (deploy, failed healthcheck, manual
// restart), or the process was killed outright (OOM). The first produces the
// stopping/stopped pair below. The second produces NOTHING — and that silence
// is the finding.
//
// Types and flags only. No messages, no stack traces: an exception message can
// carry a connection string, a request URI with a key in it, or a row's
// contents.
{
    var lifetimeLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Process");

    lifetimeLogger.LogInformation("[PROCESS] started build={Build}", buildSha);

    app.Lifetime.ApplicationStopping.Register(() => lifetimeLogger.LogInformation("[PROCESS] stopping"));
    app.Lifetime.ApplicationStopped.Register(() => lifetimeLogger.LogInformation("[PROCESS] stopped"));

    // A genuinely unhandled exception on any thread. The runtime is already on
    // its way down by the time this runs, so this is a record, not a recovery.
    AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        lifetimeLogger.LogCritical(
            "[PROCESS] unhandled type={Category} terminating={Terminating}",
            args.ExceptionObject?.GetType().Name ?? "unknown",
            args.IsTerminating);

    // A Task that faulted with nobody awaiting it. Marked observed so it
    // cannot escalate — but LOGGED, because a fire-and-forget failure is
    // otherwise completely invisible and is exactly the shape that kills a
    // process for reasons no request-level handler can see.
    TaskScheduler.UnobservedTaskException += (_, args) =>
    {
        lifetimeLogger.LogError(
            "[PROCESS] unobserved task exception type={Category}",
            args.Exception?.InnerException?.GetType().Name
                ?? args.Exception?.GetType().Name ?? "unknown");

        args.SetObserved();
    };
}

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    service = "sidequest-backend",
    environment = app.Environment.EnvironmentName,
    build = buildSha,
    timestamp = DateTime.UtcNow
})).AllowAnonymous();


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
