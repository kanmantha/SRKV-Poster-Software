using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services.Email;

public class SmtpOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string From { get; set; } = "DailyPosterGenerator <noreply@example.com>";
    public bool EnableSsl { get; set; } = true;
}

public interface IEmailSender
{
    Task SendAsync(EmailMessage message, CancellationToken ct = default);
}

/// <summary>
/// Sends via SMTP (MailKit). When SMTP is not configured the message is written to
/// the application log and the activity log so the pipeline works without a real server.
/// </summary>
public class SmtpEmailSender : IEmailSender
{
    private readonly SmtpOptions _options;
    private readonly ILogger<SmtpEmailSender> _logger;
    private readonly IActivityLog _log;

    public SmtpEmailSender(IConfiguration configuration, ILogger<SmtpEmailSender> logger, IActivityLog log)
    {
        _options = configuration.GetSection("Smtp").Get<SmtpOptions>() ?? new SmtpOptions();
        _logger = logger;
        _log = log;
    }

    public async Task SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            _logger.LogInformation(
                "Email not delivered (SMTP not configured) -> To={To} Subject={Subject}",
                message.To, message.Subject);
            _log.Add("email", $"Email queued for {message.To}: {message.Subject}");
            return;
        }

        var mail = new MimeMessage();
        mail.From.Add(MailboxAddress.Parse(_options.From));
        mail.To.Add(MailboxAddress.Parse(message.To));
        mail.Subject = message.Subject;
        mail.Body = message.IsHtml
            ? new BodyBuilder { HtmlBody = message.Body }.ToMessageBody()
            : new BodyBuilder { TextBody = message.Body }.ToMessageBody();

        using var client = new SmtpClient();
        await client.ConnectAsync(_options.Host, _options.Port, _options.EnableSsl ? SecureSocketOptions.StartTls : SecureSocketOptions.None, ct);

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            await client.AuthenticateAsync(_options.Username, _options.Password, ct);
        }

        await client.SendAsync(mail, ct);
        await client.DisconnectAsync(true, ct);
    }
}
