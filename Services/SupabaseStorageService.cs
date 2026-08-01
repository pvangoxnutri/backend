using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace sidequest.backend.Services;

public interface ISupabaseStorageService
{
    /// <summary>
    /// Upload a file to Supabase Storage. Returns the public URL.
    ///
    /// <paramref name="extension"/> must come from server-side content
    /// detection (ImageFileValidator), never from the uploaded filename — it
    /// ends up in the stored object's name, so a client-supplied value would
    /// mean a client-named object in a publicly served bucket.
    /// </summary>
    Task<string> UploadAsync(Stream content, string contentType, string extension, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file by its public URL. Safe to call with null/empty/external URLs - they are ignored.
    /// Returns true if the object was deleted or already gone; false on transient errors.
    /// </summary>
    Task<bool> DeleteByUrlAsync(string? url, CancellationToken cancellationToken = default);

    /// <summary>Best-effort batch deletion. Failures are logged but never thrown.</summary>
    Task DeleteManyByUrlAsync(IEnumerable<string?> urls, CancellationToken cancellationToken = default);

    /// <summary>
    /// True when the URL points at an object in THIS project's public bucket —
    /// i.e. something we uploaded and can delete. External hosts (Unsplash,
    /// Google) and legacy local /uploads/ paths return false.
    /// </summary>
    bool IsOwnedPublicUrl(string? url);
}

public class SupabaseStorageService : ISupabaseStorageService
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<SupabaseStorageService> _logger;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly string _bucket;
    private readonly string _publicPrefix;

    public SupabaseStorageService(HttpClient httpClient, IConfiguration configuration, ILogger<SupabaseStorageService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;

        _supabaseUrl = configuration["Supabase:Url"]?.TrimEnd('/')
            ?? throw new InvalidOperationException("Supabase:Url must be configured for storage.");

        _serviceRoleKey = configuration["Supabase:ServiceRoleKey"]
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
            ?? throw new InvalidOperationException("Supabase service role key is required for storage operations. Set Supabase:ServiceRoleKey or SUPABASE_SERVICE_ROLE_KEY.");

        _bucket = configuration["Supabase:StorageBucket"] ?? "sidequest-uploads";
        _publicPrefix = $"{_supabaseUrl}/storage/v1/object/public/{_bucket}/";
    }

    // Extensions the server itself can produce. Anything else is dropped rather
    // than trusted: this is the last line before a value becomes part of an
    // object path, so it stays a closed allow-list even though the only caller
    // already validated the content. That rules out path traversal ("../"),
    // double extensions and executable suffixes by construction.
    private static readonly HashSet<string> AllowedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".jpg", ".png", ".gif", ".webp" };

    public async Task<string> UploadAsync(Stream content, string contentType, string extension, CancellationToken cancellationToken = default)
    {
        var safeExtension = AllowedExtensions.Contains(extension) ? extension.ToLowerInvariant() : string.Empty;

        // A random GUID, not the uploaded filename: the object name leaks
        // nothing about the user or the adventure, cannot collide with or
        // overwrite anyone else's file (x-upsert is false below), and is not
        // guessable by someone enumerating the bucket.
        var objectPath = $"{Guid.NewGuid():N}{safeExtension}";
        var endpoint = $"{_supabaseUrl}/storage/v1/object/{_bucket}/{objectPath}";

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);
        request.Headers.Add("x-upsert", "false");

        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            // Status only. Supabase's error payload echoes the object path, and
            // this message travels into logs and can surface in an error
            // response — neither is a place for a link to a stored file.
            throw new InvalidOperationException($"Supabase storage upload failed ({(int)response.StatusCode}).");
        }

        return _publicPrefix + objectPath;
    }

    public bool IsOwnedPublicUrl(string? url)
        => !string.IsNullOrWhiteSpace(url)
           && url.StartsWith(_publicPrefix, StringComparison.OrdinalIgnoreCase);

    public async Task<bool> DeleteByUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Only touch URLs that belong to our Supabase Storage public bucket.
        // External (Unsplash, Google, etc.) and legacy local /uploads/ URLs are ignored.
        if (!IsOwnedPublicUrl(url))
        {
            return false;
        }

        var objectPath = url[_publicPrefix.Length..];
        if (string.IsNullOrWhiteSpace(objectPath)) return false;

        var endpoint = $"{_supabaseUrl}/storage/v1/object/{_bucket}/{objectPath}";

        using var request = new HttpRequestMessage(HttpMethod.Delete, endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);

        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            // The object path is deliberately absent from every log line below.
            // The public prefix is a constant, so path + prefix reconstructs a
            // working permanent link to a user's photo — that belongs in the
            // database row, not in a log aggregator. Status and timing are
            // enough to diagnose a failing delete.
            _logger.LogInformation("[TIMING] storage delete status={Status} elapsedMs={Elapsed}", (int)response.StatusCode, sw.ElapsedMilliseconds);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone - treat as success for idempotency.
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                // The response body is not logged either: Supabase echoes the
                // requested object path back in its error payloads.
                _logger.LogWarning(
                    "Supabase storage delete failed ({StatusCode}).",
                    (int)response.StatusCode);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TIMING] storage delete threw elapsedMs={Elapsed}", sw.ElapsedMilliseconds);
            return false;
        }
    }

    public async Task DeleteManyByUrlAsync(IEnumerable<string?> urls, CancellationToken cancellationToken = default)
    {
        var distinct = urls
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => u!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var total = Stopwatch.StartNew();
        _logger.LogInformation("[TIMING] storage deleteMany start count={Count}", distinct.Count);

        foreach (var url in distinct)
        {
            if (cancellationToken.IsCancellationRequested) break;
            await DeleteByUrlAsync(url, cancellationToken);
        }

        _logger.LogInformation("[TIMING] storage deleteMany total count={Count} elapsedMs={Elapsed}", distinct.Count, total.ElapsedMilliseconds);
    }
}
