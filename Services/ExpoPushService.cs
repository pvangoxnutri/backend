using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sidequest.backend.Services;

public record ExpoPushMessage(string To, string Title, string Body, Dictionary<string, string>? Data = null);

public record ExpoPushResult(string To, bool Success, string? ErrorCode, string? ErrorMessage);

public interface IExpoPushService
{
    Task<List<ExpoPushResult>> SendAsync(List<ExpoPushMessage> messages, CancellationToken ct = default);
}

// Talks directly to Expo's push HTTP API (https://exp.host/--/api/v2/push/send).
// There's no official Expo SDK for .NET, and the API is a single JSON POST, so
// a raw HttpClient is simpler and lighter than pulling in a dependency for it.
public class ExpoPushService : IExpoPushService
{
    private const string PushEndpoint = "https://exp.host/--/api/v2/push/send";
    private const int BatchSize = 100; // Expo's documented max per request.

    private readonly HttpClient _httpClient;
    private readonly ILogger<ExpoPushService> _logger;

    public ExpoPushService(HttpClient httpClient, ILogger<ExpoPushService> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
    }

    public async Task<List<ExpoPushResult>> SendAsync(List<ExpoPushMessage> messages, CancellationToken ct = default)
    {
        var results = new List<ExpoPushResult>();
        if (messages.Count == 0) return results;

        foreach (var batch in Chunk(messages, BatchSize))
        {
            results.AddRange(await SendBatchAsync(batch, ct));
        }

        return results;
    }

    private async Task<List<ExpoPushResult>> SendBatchAsync(List<ExpoPushMessage> batch, CancellationToken ct)
    {
        var payload = batch.Select(m => new ExpoPushRequestItem
        {
            To = m.To,
            Title = m.Title,
            Body = m.Body,
            Sound = "default",
            Data = m.Data,
        }).ToList();

        try
        {
            var json = JsonSerializer.Serialize(payload);
            using var request = new HttpRequestMessage(HttpMethod.Post, PushEndpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Accept", "application/json");
            request.Headers.Add("Accept-Encoding", "gzip, deflate");

            using var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Expo push API returned {Status}: {Body}", response.StatusCode, responseBody);
                return batch.Select(m => new ExpoPushResult(m.To, false, "http_error", $"Expo API {response.StatusCode}")).ToList();
            }

            var parsed = JsonSerializer.Deserialize<ExpoPushResponse>(responseBody);
            var tickets = parsed?.Data ?? new List<ExpoPushTicket>();

            // Expo returns tickets in the same order as the request array.
            var results = new List<ExpoPushResult>();
            for (var i = 0; i < batch.Count; i++)
            {
                var ticket = i < tickets.Count ? tickets[i] : null;
                if (ticket == null)
                {
                    results.Add(new ExpoPushResult(batch[i].To, false, "no_ticket", "No ticket returned for this message."));
                    continue;
                }

                if (ticket.Status == "ok")
                {
                    results.Add(new ExpoPushResult(batch[i].To, true, null, null));
                }
                else
                {
                    var errorCode = ticket.Details?.Error;
                    results.Add(new ExpoPushResult(batch[i].To, false, errorCode, ticket.Message));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Expo push API.");
            return batch.Select(m => new ExpoPushResult(m.To, false, "exception", ex.Message)).ToList();
        }
    }

    private static IEnumerable<List<T>> Chunk<T>(List<T> source, int size)
    {
        for (var i = 0; i < source.Count; i += size)
            yield return source.GetRange(i, Math.Min(size, source.Count - i));
    }

    private class ExpoPushRequestItem
    {
        [JsonPropertyName("to")] public string To { get; set; } = string.Empty;
        [JsonPropertyName("title")] public string Title { get; set; } = string.Empty;
        [JsonPropertyName("body")] public string Body { get; set; } = string.Empty;
        [JsonPropertyName("sound")] public string? Sound { get; set; }
        [JsonPropertyName("data")] public Dictionary<string, string>? Data { get; set; }
    }

    private class ExpoPushResponse
    {
        [JsonPropertyName("data")] public List<ExpoPushTicket>? Data { get; set; }
    }

    private class ExpoPushTicket
    {
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("message")] public string? Message { get; set; }
        [JsonPropertyName("details")] public ExpoPushTicketDetails? Details { get; set; }
    }

    private class ExpoPushTicketDetails
    {
        // "DeviceNotRegistered" is the one we actively act on (deactivate the
        // token). Others ("MessageTooBig", "MessageRateExceeded",
        // "InvalidCredentials") are logged but the token stays active.
        [JsonPropertyName("error")] public string? Error { get; set; }
    }
}
