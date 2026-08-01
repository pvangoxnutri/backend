using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace sidequest.backend.Services;

/// <summary>
/// Creates the private chat-image bucket once, at startup, if it does not
/// already exist.
///
/// Two rules make this safe to run on every boot, including in production:
///
///   1. It only ever CREATES. A bucket that already exists is left completely
///      untouched — this code will not flip visibility, change limits, or
///      delete anything. Someone who deliberately configured the bucket keeps
///      their configuration.
///   2. It never blocks startup. Storage being unreachable at boot is not a
///      reason for the API to refuse to serve text messages, and every
///      operation in ChatImageStorageService re-verifies the bucket is private
///      before it touches an object anyway. This is a convenience so a fresh
///      environment works without a manual dashboard step, not a security
///      control.
///
/// Rule 2 is enforced structurally — see StartAsync. It has to be: in minimal
/// hosting, Kestrel's own GenericWebHostService is registered during Build(),
/// AFTER everything added to builder.Services, so the host awaits every
/// IHostedService.StartAsync here BEFORE it binds the listening socket. An
/// awaited HTTP call in StartAsync therefore delays the port opening by however
/// long that call takes — which, on HttpClient's 100-second default, meant a
/// slow or unreachable Supabase kept the API from listening at all.
///
/// If the bucket exists but is PUBLIC, that is loud in the log and nothing else
/// happens here — silently making it private could break something, and
/// ChatImageStorageService already refuses to store or sign anything against a
/// public bucket, so photos fail closed rather than leaking.
/// </summary>
public sealed class ChatImageBucketProvisioner : IHostedService
{
    // Mirrors ImageFileValidator — the bucket should not accept anything the
    // upload endpoint would have rejected.
    private static readonly string[] AllowedMimeTypes =
        ["image/jpeg", "image/png", "image/gif", "image/webp"];

    private const long MaxFileSizeBytes = 10L * 1024L * 1024L;

    /// <summary>
    /// Hard ceiling for the whole probe-and-create sequence. Independent of
    /// HttpClient.Timeout (100s by default), which is far too long for
    /// something running alongside boot.
    /// </summary>
    private static readonly TimeSpan ProvisioningTimeout = TimeSpan.FromSeconds(10);

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ChatImageBucketProvisioner> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _provisioning;

    public ChatImageBucketProvisioner(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        ILogger<ChatImageBucketProvisioner> logger)
    {
        _httpClientFactory = httpClientFactory;
        _configuration = configuration;
        _logger = logger;
    }

    /// <summary>
    /// Returns immediately. The work runs on a detached task so the host moves
    /// straight on to starting Kestrel — nothing about opening the API's
    /// listening socket depends on Supabase Storage being reachable.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _provisioning = Task.Run(async () =>
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_stopping.Token);
            timeout.CancelAfter(ProvisioningTimeout);
            try
            {
                await EnsureBucketAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning(
                    "Chat image bucket provisioning timed out after {Seconds}s; the API is unaffected and this retries on the next start.",
                    ProvisioningTimeout.TotalSeconds);
            }
            catch (Exception ex)
            {
                // Never include the key or the provider response body. Swallowed
                // on purpose: an exception escaping here would take the host down.
                _logger.LogWarning(ex, "Chat image bucket provisioning could not complete at startup.");
            }
        }, CancellationToken.None);

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await _stopping.CancelAsync();
        // Give the detached task a moment to unwind so shutdown is clean, but
        // never let it hold the process open.
        if (_provisioning is not null)
        {
            await Task.WhenAny(_provisioning, Task.Delay(TimeSpan.FromSeconds(2), cancellationToken));
        }
    }

    private async Task EnsureBucketAsync(CancellationToken cancellationToken)
    {
        var supabaseUrl = _configuration["Supabase:Url"]?.Trim().TrimEnd('/');
        var serviceRoleKey = _configuration["Supabase:ServiceRoleKey"]
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY");
        var bucket = _configuration["Supabase:ChatImagesStorageBucket"]?.Trim() ?? "sidequest-chat-uploads";

        if (string.IsNullOrWhiteSpace(supabaseUrl) || string.IsNullOrWhiteSpace(serviceRoleKey))
        {
            _logger.LogInformation("Chat image bucket provisioning skipped: storage is not configured.");
            return;
        }

        var http = _httpClientFactory.CreateClient();

        using (var probe = CreateRequest(HttpMethod.Get, $"{supabaseUrl}/storage/v1/bucket/{Uri.EscapeDataString(bucket)}", serviceRoleKey))
        using (var probeResponse = await http.SendAsync(probe, cancellationToken))
        {
            if (probeResponse.IsSuccessStatusCode)
            {
                await using var stream = await probeResponse.Content.ReadAsStreamAsync(cancellationToken);
                using var payload = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var isPublic = payload.RootElement.TryGetProperty("public", out var publicProperty)
                               && publicProperty.ValueKind == JsonValueKind.True;

                if (isPublic)
                {
                    _logger.LogError(
                        "Chat image bucket exists but is PUBLIC. Private chat images will fail to store or load until it is made private.");
                }

                return;
            }

            // Supabase Storage answers a lookup for a bucket that does not
            // exist with 400, not 404 — so treating only 404 as "missing" meant
            // this never actually created anything. Both are taken as
            // not-found; the bucket name is already validated against a strict
            // pattern before it gets here, so a malformed request is not a
            // realistic reading of a 400. Creation below is still safe if that
            // assumption is ever wrong: a 409 means it already existed.
            if (probeResponse.StatusCode is not (HttpStatusCode.NotFound or HttpStatusCode.BadRequest))
            {
                _logger.LogWarning(
                    "Chat image bucket check returned status {Status}; leaving provisioning to a later start.",
                    (int)probeResponse.StatusCode);
                return;
            }
        }

        using var create = CreateRequest(HttpMethod.Post, $"{supabaseUrl}/storage/v1/bucket", serviceRoleKey);
        create.Content = new StringContent(
            JsonSerializer.Serialize(new
            {
                id = bucket,
                name = bucket,
                @public = false,
                file_size_limit = MaxFileSizeBytes,
                allowed_mime_types = AllowedMimeTypes,
            }),
            Encoding.UTF8,
            "application/json");

        using var createResponse = await http.SendAsync(create, cancellationToken);
        if (createResponse.IsSuccessStatusCode)
        {
            _logger.LogInformation("Created the private chat image bucket.");
            return;
        }

        // A 409 means someone (or another instance booting at the same time)
        // created it first — the desired end state, not an error.
        if (createResponse.StatusCode == HttpStatusCode.Conflict)
        {
            return;
        }

        _logger.LogWarning(
            "Could not create the private chat image bucket (status {Status}). Create it manually as a NON-public bucket.",
            (int)createResponse.StatusCode);
    }

    private static HttpRequestMessage CreateRequest(HttpMethod method, string url, string serviceRoleKey)
    {
        var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", serviceRoleKey);
        request.Headers.Add("apikey", serviceRoleKey);
        return request;
    }
}
