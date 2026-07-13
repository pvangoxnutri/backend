using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace sidequest.backend.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken);
}

// Sends through Resend's REST API — the SAME Resend account and verified
// sidequesttravel.app domain the website's auth/approval/whitelist mails
// already use (see website: app/api/admin/send-approval-email/route.ts).
// Reads the same RESEND_API_KEY variable; no separate SMTP setup exists.
public class ResendEmailSender : IEmailSender
{
    // Mirrors the website's send routes exactly.
    private const string FromAddress = "SideQuest <noreply@sidequesttravel.app>";
    private const string ReplyTo = "hello@sidequesttravel.app";

    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly ILogger<ResendEmailSender> _logger;

    public ResendEmailSender(HttpClient httpClient, IConfiguration configuration, ILogger<ResendEmailSender> logger)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        var apiKey = _configuration["RESEND_API_KEY"];
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            _logger.LogWarning(
                "RESEND_API_KEY is not configured. Intended email to {ToEmail}. Subject: {Subject}. Text body: {TextBody}",
                toEmail,
                subject,
                textBody);
            return;
        }

        var payload = JsonSerializer.Serialize(new
        {
            from = FromAddress,
            to = new[] { toEmail },
            subject,
            html = htmlBody,
            text = textBody,
            reply_to = ReplyTo,
        });

        using var request = new HttpRequestMessage(HttpMethod.Post, "https://api.resend.com/emails");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(payload, Encoding.UTF8, "application/json");

        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException($"Resend send failed ({(int)response.StatusCode}): {body}");
        }
    }
}
