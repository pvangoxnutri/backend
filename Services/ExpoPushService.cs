using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace sidequest.backend.Services;

public record ExpoPushMessage(string To, string Title, string Body, Dictionary<string, string>? Data = null);

// TicketId is null when Success is false at send time (Expo rejected the
// message outright, e.g. malformed token) — there's nothing to check a
// receipt for in that case.
public record ExpoPushResult(string To, bool Success, string? TicketId, string? ErrorCode, string? ErrorMessage);

// Found=false means Expo hasn't produced a receipt for this ticket yet
// (still processing, or it's outside the ~1 day window receipts are kept
// for) — callers should treat that as "still pending", not as a failure.
public record ExpoReceiptResult(string TicketId, bool Found, bool Success, string? ErrorCode, string? ErrorMessage);

public interface IExpoPushService
{
    Task<List<ExpoPushResult>> SendAsync(List<ExpoPushMessage> messages, CancellationToken ct = default);
    Task<List<ExpoReceiptResult>> GetReceiptsAsync(List<string> ticketIds, CancellationToken ct = default);
}

// Talks directly to Expo's push HTTP API. There's no official Expo SDK for
// .NET, and both endpoints are single JSON POSTs, so a raw HttpClient is
// simpler and lighter than pulling in a dependency for it.
//
// Two-phase delivery, per Expo's own guidance: sending a message only gets
// you a "ticket" (Expo accepted the request) — the real APNs/FCM-level
// outcome, including DeviceNotRegistered, frequently only shows up later via
// GetReceiptsAsync. Callers MUST check receipts; a ticket with status "ok"
// is not proof of delivery.
public class ExpoPushService : IExpoPushService
{
    private const string PushEndpoint = "https://exp.host/--/api/v2/push/send";
    private const string ReceiptsEndpoint = "https://exp.host/--/api/v2/push/getReceipts";
    private const int BatchSize = 100; // Expo's documented max per send request.
    private const int ReceiptBatchSize = 300; // Expo allows up to 1000; kept conservative.

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

    public async Task<List<ExpoReceiptResult>> GetReceiptsAsync(List<string> ticketIds, CancellationToken ct = default)
    {
        var results = new List<ExpoReceiptResult>();
        if (ticketIds.Count == 0) return results;

        foreach (var batch in Chunk(ticketIds, ReceiptBatchSize))
        {
            results.AddRange(await GetReceiptsBatchAsync(batch, ct));
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

            // 429/5xx are exactly the "retry later" cases the caller's
            // backoff logic needs to see distinctly — surface the status
            // code as the error code so it can decide.
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Expo push API returned {Status}: {Body}", response.StatusCode, responseBody);
                var code = ((int)response.StatusCode == 429 || (int)response.StatusCode >= 500) ? "http_retryable" : "http_error";
                return batch.Select(m => new ExpoPushResult(m.To, false, null, code, $"Expo API {response.StatusCode}")).ToList();
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
                    results.Add(new ExpoPushResult(batch[i].To, false, null, "no_ticket", "No ticket returned for this message."));
                    continue;
                }

                if (ticket.Status == "ok")
                {
                    results.Add(new ExpoPushResult(batch[i].To, true, ticket.Id, null, null));
                }
                else
                {
                    results.Add(new ExpoPushResult(batch[i].To, false, null, ticket.Details?.Error, ticket.Message));
                }
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Expo push API.");
            return batch.Select(m => new ExpoPushResult(m.To, false, null, "exception", ex.Message)).ToList();
        }
    }

    private async Task<List<ExpoReceiptResult>> GetReceiptsBatchAsync(List<string> batch, CancellationToken ct)
    {
        try
        {
            var json = JsonSerializer.Serialize(new ExpoReceiptsRequest { Ids = batch });
            using var request = new HttpRequestMessage(HttpMethod.Post, ReceiptsEndpoint)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json"),
            };
            request.Headers.Add("Accept", "application/json");

            using var response = await _httpClient.SendAsync(request, ct);
            var responseBody = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Expo receipts API returned {Status}: {Body}", response.StatusCode, responseBody);
                // Not found, not failed — we just couldn't check this time.
                // The caller's age-based fallback handles tickets that never
                // resolve.
                return new List<ExpoReceiptResult>();
            }

            var parsed = JsonSerializer.Deserialize<ExpoReceiptsResponse>(responseBody);
            var receiptsById = parsed?.Data ?? new Dictionary<string, ExpoPushTicket>();

            var results = new List<ExpoReceiptResult>();
            foreach (var id in batch)
            {
                if (!receiptsById.TryGetValue(id, out var receipt))
                {
                    continue; // No receipt yet — still pending, not a failure.
                }

                results.Add(receipt.Status == "ok"
                    ? new ExpoReceiptResult(id, true, true, null, null)
                    : new ExpoReceiptResult(id, true, false, receipt.Details?.Error, receipt.Message));
            }

            return results;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to call Expo receipts API.");
            return new List<ExpoReceiptResult>();
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

    private class ExpoReceiptsRequest
    {
        [JsonPropertyName("ids")] public List<string> Ids { get; set; } = new();
    }

    private class ExpoReceiptsResponse
    {
        [JsonPropertyName("data")] public Dictionary<string, ExpoPushTicket>? Data { get; set; }
    }

    private class ExpoPushTicket
    {
        [JsonPropertyName("status")] public string Status { get; set; } = string.Empty;
        [JsonPropertyName("id")] public string? Id { get; set; }
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
