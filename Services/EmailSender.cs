using System.Net;
using System.Net.Mail;

namespace sidequest.backend.Services;

public interface IEmailSender
{
    Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken);
}

public class EmailSender : IEmailSender
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<EmailSender> _logger;

    public EmailSender(IConfiguration configuration, ILogger<EmailSender> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public async Task SendAsync(string toEmail, string subject, string htmlBody, string textBody, CancellationToken cancellationToken)
    {
        var host = _configuration["Email:SmtpHost"];
        var fromAddress = _configuration["Email:FromAddress"] ?? "noreply@sidequest.local";
        var fromName = _configuration["Email:FromName"] ?? "SideQuest";

        if (string.IsNullOrWhiteSpace(host))
        {
            _logger.LogWarning(
                "Email transport not configured. Intended email to {ToEmail}. Subject: {Subject}. Text body: {TextBody}",
                toEmail,
                subject,
                textBody);
            return;
        }

        var port = int.TryParse(_configuration["Email:SmtpPort"], out var configuredPort) ? configuredPort : 587;
        var enableSsl = !string.Equals(_configuration["Email:EnableSsl"], "false", StringComparison.OrdinalIgnoreCase);
        var username = _configuration["Email:Username"];
        var password = _configuration["Email:Password"];

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress, fromName),
            Subject = subject,
            Body = htmlBody,
            IsBodyHtml = true
        };

        message.To.Add(toEmail);
        message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(textBody, null, "text/plain"));

        using var client = new SmtpClient(host, port)
        {
            EnableSsl = enableSsl
        };

        if (!string.IsNullOrWhiteSpace(username))
        {
            client.Credentials = new NetworkCredential(username, password);
        }

        cancellationToken.ThrowIfCancellationRequested();
        await client.SendMailAsync(message, cancellationToken);
    }
}
