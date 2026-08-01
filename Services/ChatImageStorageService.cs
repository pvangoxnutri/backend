using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services;

public sealed record SignedChatImageUrl(string Url, DateTime ExpiresAt);

public interface IChatImageStorageService
{
    Task UploadAsync(
        Stream content,
        string contentType,
        string objectPath,
        CancellationToken cancellationToken = default);

    Task<SignedChatImageUrl> CreateSignedReadUrlAsync(
        string objectPath,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string objectPath,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteManyAsync(
        IEnumerable<string> objectPaths,
        CancellationToken cancellationToken = default);
}

public sealed class ChatImageStorageException : Exception
{
    public ChatImageStorageException(string message) : base(message) { }
}

/// <summary>
/// Tells apart the two things ChatMessages.ImageUrl can hold, and builds the
/// new kind.
///
/// Chat photos used to land in the PUBLIC uploads bucket, so the column held a
/// permanent, unauthenticated https URL — anyone who ever saw it, or captured
/// it from a log or a screenshot, could fetch that private conversation photo
/// forever. New chat images go to a private bucket instead, and the column
/// holds an internal reference that is useless on its own.
///
/// Old rows are NOT rewritten by this change (see ChatImageBackfillService for
/// the separate, opt-in migration), so both forms have to coexist. The scheme
/// prefix is what separates them, and it was chosen to be obviously not a URL:
/// an app build that predates this work and renders the column directly gets a
/// broken image rather than leaking anything.
/// </summary>
public static class ChatImageReference
{
    public const string Scheme = "sqchat://";

    private static readonly string[] AllowedExtensions = [".jpg", ".png", ".gif", ".webp"];

    /// <summary>An object in the private chat bucket.</summary>
    public static bool IsPrivate(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && value.StartsWith(Scheme, StringComparison.Ordinal);

    /// <summary>A pre-migration row: a permanent URL in the public bucket.</summary>
    public static bool IsLegacyPublicUrl(string? value)
        => !string.IsNullOrWhiteSpace(value)
           && (value.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://", StringComparison.OrdinalIgnoreCase));

    public static string Build(Guid tripId, string extension)
        => Scheme + BuildObjectPath(tripId, extension);

    /// <summary>
    /// trips/{tripId}/{guid}{ext} — the trip id is written by the server after
    /// it has verified membership, never taken from the request body. Grouping
    /// by trip means a leaked or mis-signed path can be checked against the
    /// message's own trip before anything is signed.
    /// </summary>
    public static string BuildObjectPath(Guid tripId, string extension)
        => $"trips/{tripId:N}/{Guid.NewGuid():N}{extension}";

    /// <summary>
    /// Unwraps a stored reference into its object path, and refuses anything
    /// that does not match the exact shape this service writes. Callers pass
    /// the trip the message actually belongs to; a path naming a different
    /// trip is rejected, so a reference smuggled into a message row cannot be
    /// used to read another adventure's photo.
    /// </summary>
    public static bool TryGetObjectPath(string? value, Guid expectedTripId, out string objectPath)
    {
        objectPath = string.Empty;
        if (!IsPrivate(value)) return false;

        var candidate = value![Scheme.Length..];
        if (!IsWellFormedObjectPath(candidate)) return false;

        var segments = candidate.Split('/');
        if (!Guid.TryParseExact(segments[1], "N", out var tripId) || tripId != expectedTripId) return false;

        objectPath = candidate;
        return true;
    }

    /// <summary>
    /// Shape check without the trip binding — for cleanup paths, which delete
    /// by whatever the row holds and have no separate trip to compare against.
    /// </summary>
    public static bool TryGetObjectPathForCleanup(string? value, out string objectPath)
    {
        objectPath = string.Empty;
        if (!IsPrivate(value)) return false;

        var candidate = value![Scheme.Length..];
        if (!IsWellFormedObjectPath(candidate)) return false;

        objectPath = candidate;
        return true;
    }

    private static bool IsWellFormedObjectPath(string candidate)
    {
        var segments = candidate.Split('/');
        if (segments.Length != 3) return false;
        if (!string.Equals(segments[0], "trips", StringComparison.Ordinal)) return false;
        if (!Guid.TryParseExact(segments[1], "N", out _)) return false;

        var name = Path.GetFileNameWithoutExtension(segments[2]);
        var extension = Path.GetExtension(segments[2]);
        return Guid.TryParseExact(name, "N", out _)
               && AllowedExtensions.Contains(extension, StringComparer.Ordinal);
    }
}

/// <summary>
/// Supabase Storage access for the private chat-image bucket.
///
/// Deliberately a near-copy of TripDocumentStorageService rather than a shared
/// base: the two buckets have different lifetimes, different path shapes and
/// different allowed types, and folding them together would make it easy to
/// widen one by editing the other. What IS shared is the discipline — the
/// service-role key never leaves the server, the bucket is verified private
/// before every operation, and no object path, key or signed URL ever reaches
/// an exception message or a log line.
/// </summary>
public sealed partial class ChatImageStorageService : IChatImageStorageService
{
    // Long enough to load an image on a slow connection, short enough that a
    // URL copied out of a device's memory is worthless in minutes. The client
    // caches it in memory only and re-requests on expiry.
    public const int SignedUrlLifetimeSeconds = 300;

    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly string _bucket;

    public ChatImageStorageService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        // Validated lazily, like the documents service: constructing the chat
        // controller for a plain text message must not fail because chat image
        // storage has not been provisioned.
        _supabaseUrl = configuration["Supabase:Url"]?.Trim().TrimEnd('/') ?? string.Empty;
        _serviceRoleKey = configuration["Supabase:ServiceRoleKey"]
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
            ?? string.Empty;
        _bucket = configuration["Supabase:ChatImagesStorageBucket"]?.Trim() ?? "sidequest-chat-uploads";
    }

    public async Task UploadAsync(
        Stream content,
        string contentType,
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        await EnsurePrivateBucketAsync(cancellationToken);

        using var request = CreateRequest(HttpMethod.Post, $"object/{EncodeStorageTarget(objectPath)}");
        // No overwrite: a GUID name should never collide, and if one somehow
        // did, failing is correct — silently replacing another member's photo
        // is the outcome worth preventing.
        request.Headers.Add("x-upsert", "false");
        request.Headers.TryAddWithoutValidation("cache-control", "private, no-store, max-age=0");
        request.Content = new StreamContent(content);
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatImageStorageException(
                    $"Private chat image upload failed with status {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException)
        {
            // The object may or may not exist after a cancelled/timed-out PUT.
            // Try to remove it: the caller will not create a message, so
            // anything left behind is an orphan nothing points at.
            await BestEffortDeleteAfterAmbiguousUploadAsync(objectPath);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new ChatImageStorageException("Private chat image upload timed out.");
        }
        catch (HttpRequestException)
        {
            await BestEffortDeleteAfterAmbiguousUploadAsync(objectPath);
            throw new ChatImageStorageException("Private chat image storage is unavailable.");
        }
    }

    public async Task<SignedChatImageUrl> CreateSignedReadUrlAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        await EnsurePrivateBucketAsync(cancellationToken);

        using var request = CreateRequest(HttpMethod.Post, $"object/sign/{EncodeStorageTarget(objectPath)}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { expiresIn = SignedUrlLifetimeSeconds }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatImageStorageException(
                    $"Private chat image signing failed with status {(int)response.StatusCode}.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

            if (!TryGetSignedUrl(payload.RootElement, out var signedUrl))
            {
                throw new ChatImageStorageException("Private chat image signing returned an invalid response.");
            }

            return new SignedChatImageUrl(
                TripDocumentStorageService.ComposeSignedUrl(_supabaseUrl, signedUrl),
                DateTime.UtcNow.AddSeconds(SignedUrlLifetimeSeconds));
        }
        catch (TripDocumentStorageException)
        {
            // ComposeSignedUrl is shared with the documents service and throws
            // its exception type when the provider hands back a URL pointing
            // somewhere other than this project. Re-wrap so callers only ever
            // handle one exception type.
            throw new ChatImageStorageException("Private chat image signing returned an invalid URL.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatImageStorageException("Private chat image signing timed out.");
        }
        catch (HttpRequestException)
        {
            throw new ChatImageStorageException("Private chat image storage is unavailable.");
        }
        catch (JsonException)
        {
            throw new ChatImageStorageException("Private chat image signing returned an invalid response.");
        }
    }

    public async Task<bool> DeleteAsync(
        string objectPath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsurePrivateBucketAsync(cancellationToken);
            return await DeleteObjectAsync(objectPath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Never include the key, bucket, path or provider response.
            return false;
        }
    }

    public async Task<bool> DeleteManyAsync(
        IEnumerable<string> objectPaths,
        CancellationToken cancellationToken = default)
    {
        var distinctPaths = objectPaths.Distinct(StringComparer.Ordinal).ToList();
        if (distinctPaths.Count == 0) return true;

        try
        {
            await EnsurePrivateBucketAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }

        var allDeleted = true;
        foreach (var objectPath in distinctPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await DeleteObjectAsync(objectPath, cancellationToken))
                {
                    allDeleted = false;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                allDeleted = false;
            }
        }
        return allDeleted;
    }

    private async Task<bool> DeleteObjectAsync(string objectPath, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"object/{EncodeStorageTarget(objectPath)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        // Already gone counts as deleted — cleanup has to be idempotent, or a
        // retried trip deletion would report failure forever.
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    private async Task BestEffortDeleteAfterAmbiguousUploadAsync(string objectPath)
    {
        try
        {
            await DeleteObjectAsync(objectPath, CancellationToken.None);
        }
        catch
        {
            // The caller sees a failed upload and creates no message. Do not
            // log the path if provider cleanup is down too.
        }
    }

    /// <summary>
    /// Refuses to touch the bucket unless the provider confirms it is private.
    /// Without this, someone flipping the bucket to public in the Supabase
    /// dashboard would silently turn every "private" chat photo into a
    /// permanent public URL again, and nothing in the code would notice.
    /// </summary>
    private async Task EnsurePrivateBucketAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Get, $"bucket/{Uri.EscapeDataString(_bucket)}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new ChatImageStorageException(
                    $"Private chat image bucket check failed with status {(int)response.StatusCode}.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            if (!payload.RootElement.TryGetProperty("public", out var publicProperty)
                || publicProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new ChatImageStorageException("Chat image storage bucket metadata is invalid.");
            }

            if (publicProperty.GetBoolean())
            {
                throw new ChatImageStorageException("Chat image storage bucket must be private.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ChatImageStorageException("Chat image storage bucket check timed out.");
        }
        catch (HttpRequestException)
        {
            throw new ChatImageStorageException("Private chat image storage is unavailable.");
        }
        catch (JsonException)
        {
            throw new ChatImageStorageException("Chat image storage bucket metadata is invalid.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        EnsureStorageConfigured();
        var request = new HttpRequestMessage(method, $"{_supabaseUrl}/storage/v1/{relativePath}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _serviceRoleKey);
        request.Headers.Add("apikey", _serviceRoleKey);
        return request;
    }

    private void EnsureStorageConfigured()
    {
        if (!Uri.TryCreate(_supabaseUrl, UriKind.Absolute, out var url)
            || (url.Scheme != Uri.UriSchemeHttp && url.Scheme != Uri.UriSchemeHttps)
            || string.IsNullOrWhiteSpace(_serviceRoleKey)
            || !BucketNamePattern().IsMatch(_bucket))
        {
            // Deliberately generic: no provider URL, key, bucket or path.
            throw new ChatImageStorageException("Private chat image storage is not configured.");
        }
    }

    private string EncodeStorageTarget(string objectPath)
    {
        // Re-validated here as well as at the caller. This is the last point
        // before a string becomes a URL path, so "../" or a stray extension
        // must not get past it even if a future caller forgets to check.
        if (!ChatImageReference.TryGetObjectPathForCleanup(ChatImageReference.Scheme + objectPath, out _))
        {
            throw new ChatImageStorageException("Invalid private chat image storage path.");
        }

        return $"{Uri.EscapeDataString(_bucket)}/{string.Join("/", objectPath.Split('/').Select(Uri.EscapeDataString))}";
    }

    private static bool TryGetSignedUrl(JsonElement root, out string value)
    {
        if ((root.TryGetProperty("signedURL", out var property)
             || root.TryGetProperty("signedUrl", out property))
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(property.GetString()))
        {
            value = property.GetString()!;
            return true;
        }

        value = string.Empty;
        return false;
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketNamePattern();
}
