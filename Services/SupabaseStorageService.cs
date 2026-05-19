using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace sidequest.backend.Services;

public interface ISupabaseStorageService
{
    /// <summary>Upload a file to Supabase Storage. Returns the public URL.</summary>
    Task<string> UploadAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default);

    /// <summary>
    /// Delete a file by its public URL. Safe to call with null/empty/external URLs - they are ignored.
    /// Returns true if the object was deleted or already gone; false on transient errors.
    /// </summary>
    Task<bool> DeleteByUrlAsync(string? url, CancellationToken cancellationToken = default);

    /// <summary>Best-effort batch deletion. Failures are logged but never thrown.</summary>
    Task DeleteManyByUrlAsync(IEnumerable<string?> urls, CancellationToken cancellationToken = default);
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

    public async Task<string> UploadAsync(Stream content, string contentType, string fileName, CancellationToken cancellationToken = default)
    {
        var extension = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = contentType switch
            {
                "image/jpeg" => ".jpg",
                "image/png"  => ".png",
                "image/gif"  => ".gif",
                "image/webp" => ".webp",
                _ => string.Empty,
            };
        }

        var objectPath = $"{Guid.NewGuid():N}{extension}";
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
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Supabase storage upload failed ({(int)response.StatusCode}): {body}");
        }

        return _publicPrefix + objectPath;
    }

    public async Task<bool> DeleteByUrlAsync(string? url, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return false;

        // Only touch URLs that belong to our Supabase Storage public bucket.
        // External (Unsplash, Google, etc.) and legacy local /uploads/ URLs are ignored.
        if (!url.StartsWith(_publicPrefix, StringComparison.OrdinalIgnoreCase))
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
            _logger.LogInformation("[TIMING] storage delete status={Status} elapsedMs={Elapsed} path={Path}", (int)response.StatusCode, sw.ElapsedMilliseconds, objectPath);
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                // Already gone - treat as success for idempotency.
                return true;
            }
            if (!response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning(
                    "Supabase storage delete failed ({StatusCode}) for {Path}: {Body}",
                    (int)response.StatusCode, objectPath, body);
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[TIMING] storage delete threw elapsedMs={Elapsed} path={Path}", sw.ElapsedMilliseconds, objectPath);
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
