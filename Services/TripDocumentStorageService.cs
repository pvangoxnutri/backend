using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace sidequest.backend.Services;

public sealed record SignedTripDocumentUrl(string Url, DateTime ExpiresAt);

public interface ITripDocumentStorageService
{
    Task UploadAsync(
        Stream content,
        string contentType,
        string storagePath,
        CancellationToken cancellationToken = default);

    Task<SignedTripDocumentUrl> CreateSignedReadUrlAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a private document into memory, server-side.
    ///
    /// Deliberately separate from <see cref="CreateSignedReadUrlAsync"/>. That
    /// one hands a short-lived URL to the app so a person can open their own
    /// file. This one is for the backend to process the bytes itself — and the
    /// URL must never leave the process, because a signed URL to somebody's
    /// booking confirmation is a bearer token for that document.
    /// </summary>
    Task<byte[]> DownloadAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default);

    Task<bool> DeleteManyAsync(
        IEnumerable<string> storagePaths,
        CancellationToken cancellationToken = default);
}

public sealed class TripDocumentStorageException : Exception
{
    public TripDocumentStorageException(string message) : base(message) { }
}

public sealed partial class TripDocumentStorageService : ITripDocumentStorageService
{
    public const int SignedUrlLifetimeSeconds = 60;

    private readonly HttpClient _httpClient;
    private readonly string _supabaseUrl;
    private readonly string _serviceRoleKey;
    private readonly string _bucket;

    public TripDocumentStorageService(HttpClient httpClient, IConfiguration configuration)
    {
        _httpClient = httpClient;
        // Configuration is validated only when an operation actually touches
        // storage. Constructing a controller for a read-only metadata GET must
        // not fail merely because document storage has not been provisioned.
        _supabaseUrl = configuration["Supabase:Url"]?.Trim().TrimEnd('/') ?? string.Empty;
        _serviceRoleKey = configuration["Supabase:ServiceRoleKey"]
            ?? Environment.GetEnvironmentVariable("SUPABASE_SERVICE_ROLE_KEY")
            ?? string.Empty;
        _bucket = configuration["Supabase:DocumentsStorageBucket"]?.Trim() ?? "trip-documents";
    }

    public async Task UploadAsync(
        Stream content,
        string contentType,
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        await EnsurePrivateBucketAsync(cancellationToken);

        using var request = CreateRequest(HttpMethod.Post, $"object/{EncodeStorageTarget(storagePath)}");
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
                throw new TripDocumentStorageException(
                    $"Private document upload failed with status {(int)response.StatusCode}.");
            }
        }
        catch (OperationCanceledException)
        {
            await BestEffortDeleteAfterAmbiguousUploadAsync(storagePath);
            if (cancellationToken.IsCancellationRequested) throw;
            throw new TripDocumentStorageException("Private document upload timed out.");
        }
        catch (HttpRequestException)
        {
            await BestEffortDeleteAfterAmbiguousUploadAsync(storagePath);
            throw new TripDocumentStorageException("Private document storage is unavailable.");
        }
    }

    /// <summary>
    /// Fetches the object with the service-role credential, in-process.
    ///
    /// Uses the authenticated object endpoint directly rather than signing a
    /// URL and following it: a signed URL is a bearer token for a private
    /// document, and minting one that only this method will use creates a
    /// credential with no purpose and a window in which it could leak.
    /// </summary>
    public async Task<byte[]> DownloadAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        using var request = CreateRequest(HttpMethod.Get, $"object/{EncodeStorageTarget(storagePath)}");

        try
        {
            using var response = await _httpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                // Status only. The message must not carry the object path —
                // that is the private location of somebody's document.
                throw new TripDocumentStorageException(
                    $"Private document read failed with status {(int)response.StatusCode}.");
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TripDocumentStorageException("Private document read timed out.");
        }
        catch (HttpRequestException)
        {
            throw new TripDocumentStorageException("Private document storage is unavailable.");
        }
    }

    public async Task<SignedTripDocumentUrl> CreateSignedReadUrlAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        await EnsurePrivateBucketAsync(cancellationToken);

        using var request = CreateRequest(HttpMethod.Post, $"object/sign/{EncodeStorageTarget(storagePath)}");
        request.Content = new StringContent(
            JsonSerializer.Serialize(new { expiresIn = SignedUrlLifetimeSeconds }),
            Encoding.UTF8,
            "application/json");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TripDocumentStorageException(
                    $"Private document signing failed with status {(int)response.StatusCode}.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);

            if (!TryGetSignedUrl(payload.RootElement, out var signedUrl))
            {
                throw new TripDocumentStorageException("Private document signing returned an invalid response.");
            }

            return new SignedTripDocumentUrl(
                ComposeSignedUrl(_supabaseUrl, signedUrl),
                DateTime.UtcNow.AddSeconds(SignedUrlLifetimeSeconds));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripDocumentStorageException("Private document signing timed out.");
        }
        catch (HttpRequestException)
        {
            throw new TripDocumentStorageException("Private document storage is unavailable.");
        }
        catch (JsonException)
        {
            throw new TripDocumentStorageException("Private document signing returned an invalid response.");
        }
    }

    public async Task<bool> DeleteAsync(
        string storagePath,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await EnsurePrivateBucketAsync(cancellationToken);
            return await DeleteObjectAsync(storagePath, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Callers keep the database row when deletion is uncertain and can
            // retry. Never include the private key or provider response in logs.
            return false;
        }
    }

    public async Task<bool> DeleteManyAsync(
        IEnumerable<string> storagePaths,
        CancellationToken cancellationToken = default)
    {
        var distinctPaths = storagePaths.Distinct(StringComparer.Ordinal).ToList();
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
        foreach (var storagePath in distinctPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (!await DeleteObjectAsync(storagePath, cancellationToken))
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

    private async Task<bool> DeleteObjectAsync(
        string storagePath,
        CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Delete, $"object/{EncodeStorageTarget(storagePath)}");
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        return response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound;
    }

    private async Task BestEffortDeleteAfterAmbiguousUploadAsync(string storagePath)
    {
        try
        {
            await DeleteObjectAsync(storagePath, CancellationToken.None);
        }
        catch
        {
            // The caller receives a failed upload and never creates metadata.
            // Do not log the private path if provider cleanup is also down.
        }
    }

    private async Task EnsurePrivateBucketAsync(CancellationToken cancellationToken)
    {
        using var request = CreateRequest(
            HttpMethod.Get,
            $"bucket/{Uri.EscapeDataString(_bucket)}");

        try
        {
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                throw new TripDocumentStorageException(
                    $"Private document bucket check failed with status {(int)response.StatusCode}.");
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var payload = await JsonDocument.ParseAsync(responseStream, cancellationToken: cancellationToken);
            if (!payload.RootElement.TryGetProperty("public", out var publicProperty)
                || publicProperty.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
            {
                throw new TripDocumentStorageException("Document storage bucket metadata is invalid.");
            }

            if (publicProperty.GetBoolean())
            {
                throw new TripDocumentStorageException("Document storage bucket must be private.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TripDocumentStorageException("Document storage bucket check timed out.");
        }
        catch (HttpRequestException)
        {
            throw new TripDocumentStorageException("Private document storage is unavailable.");
        }
        catch (JsonException)
        {
            throw new TripDocumentStorageException("Document storage bucket metadata is invalid.");
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string relativePath)
    {
        EnsureStorageConfigured();
        var request = new HttpRequestMessage(
            method,
            $"{_supabaseUrl}/storage/v1/{relativePath}");
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
            // Deliberately generic: never include a provider URL, key, bucket,
            // object path, or any other private storage detail in the error.
            throw new TripDocumentStorageException(
                "Private document storage is not configured.");
        }
    }

    private string EncodeStorageTarget(string storagePath)
    {
        ValidateStoragePath(storagePath);
        return $"{Uri.EscapeDataString(_bucket)}/{string.Join("/", storagePath.Split('/').Select(Uri.EscapeDataString))}";
    }

    private static void ValidateStoragePath(string storagePath)
    {
        var segments = storagePath.Split('/');
        if (segments.Length != 3
            || !Guid.TryParseExact(segments[0], "N", out _)
            || !Guid.TryParseExact(segments[1], "N", out var documentId))
        {
            throw new TripDocumentStorageException("Invalid private document storage path.");
        }

        var extension = Path.GetExtension(segments[2]);
        var baseName = Path.GetFileNameWithoutExtension(segments[2]);
        if (!baseName.Equals(documentId.ToString("N"), StringComparison.Ordinal)
            || extension is not (".pdf" or ".jpg" or ".png" or ".webp"))
        {
            throw new TripDocumentStorageException("Invalid private document storage path.");
        }
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

    internal static string ComposeSignedUrl(string supabaseUrl, string signedUrl)
    {
        var projectBase = new Uri($"{supabaseUrl.TrimEnd('/')}/", UriKind.Absolute);

        if (Uri.TryCreate(signedUrl, UriKind.Absolute, out var absolute)
            && (absolute.Scheme == Uri.UriSchemeHttp || absolute.Scheme == Uri.UriSchemeHttps))
        {
            if (absolute.Scheme != projectBase.Scheme
                || !absolute.Host.Equals(projectBase.Host, StringComparison.OrdinalIgnoreCase)
                || absolute.Port != projectBase.Port
                || !string.IsNullOrEmpty(absolute.UserInfo))
            {
                throw new TripDocumentStorageException("Private document signing returned an invalid URL.");
            }
            return absolute.ToString();
        }

        var relative = signedUrl.Trim();
        if (relative.StartsWith("/storage/v1/", StringComparison.Ordinal))
        {
            return new Uri(projectBase, relative.TrimStart('/')).ToString();
        }
        if (relative.StartsWith("storage/v1/", StringComparison.Ordinal))
        {
            return new Uri(projectBase, relative).ToString();
        }
        if (relative.StartsWith("/object/", StringComparison.Ordinal)
            || relative.StartsWith("object/", StringComparison.Ordinal))
        {
            var storageBase = new Uri(projectBase, "storage/v1/");
            return new Uri(storageBase, relative.TrimStart('/')).ToString();
        }

        throw new TripDocumentStorageException("Private document signing returned an invalid URL.");
    }

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,99}$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketNamePattern();
}
