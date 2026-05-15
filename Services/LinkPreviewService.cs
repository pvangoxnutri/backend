using System.Text.RegularExpressions;
using HtmlAgilityPack;

namespace sidequest.backend.Services;

public class LinkPreviewData
{
    public string? Title { get; set; }
    public string? Description { get; set; }
    public string? ImageUrl { get; set; }
}

public class LinkPreviewService
{
    private readonly HttpClient _httpClient;
    private static readonly Dictionary<string, LinkPreviewData> _cache = new();
    private static readonly object _cacheLock = new();

    public LinkPreviewService(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    public async Task<LinkPreviewData?> GetPreviewAsync(string url, CancellationToken ct = default)
    {
        if (!IsValidUrl(url))
            return null;

        lock (_cacheLock)
        {
            if (_cache.TryGetValue(url, out var cached))
                return cached;
        }

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Add("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36");

            var response = await _httpClient.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return null;

            var html = await response.Content.ReadAsStringAsync(ct);
            var doc = new HtmlDocument();
            doc.LoadHtml(html);

            var preview = ExtractMetadata(doc);

            lock (_cacheLock)
            {
                _cache[url] = preview;
            }

            return preview;
        }
        catch
        {
            return null;
        }
    }

    private LinkPreviewData ExtractMetadata(HtmlDocument doc)
    {
        var data = new LinkPreviewData();

        var ogTitle = doc.DocumentNode.SelectSingleNode("//meta[@property='og:title']");
        if (ogTitle != null)
            data.Title = ogTitle.GetAttributeValue("content", null);

        var ogDescription = doc.DocumentNode.SelectSingleNode("//meta[@property='og:description']");
        if (ogDescription != null)
            data.Description = ogDescription.GetAttributeValue("content", null);

        var ogImage = doc.DocumentNode.SelectSingleNode("//meta[@property='og:image']");
        if (ogImage != null)
            data.ImageUrl = ogImage.GetAttributeValue("content", null);

        if (string.IsNullOrEmpty(data.Title))
        {
            var titleTag = doc.DocumentNode.SelectSingleNode("//title");
            if (titleTag != null)
                data.Title = titleTag.InnerText?.Trim();
        }

        if (string.IsNullOrEmpty(data.Description))
        {
            var metaDescription = doc.DocumentNode.SelectSingleNode("//meta[@name='description']");
            if (metaDescription != null)
                data.Description = metaDescription.GetAttributeValue("content", null);
        }

        return data;
    }

    private bool IsValidUrl(string url)
    {
        try
        {
            var uri = new Uri(url);
            return uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps;
        }
        catch
        {
            return false;
        }
    }
}
