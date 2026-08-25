using System.Net;
using System.Text;
using DailyPosterGenerator.Data;
using DailyPosterGenerator.Models;
using Microsoft.EntityFrameworkCore;

namespace DailyPosterGenerator.Services.Email;

public interface IEmailService
{
    Task SendAsync(string to, string subject, string body, bool isHtml = true, CancellationToken ct = default);

    Task SendVerificationAsync(AppUser user, string link, CancellationToken ct = default);

    Task SendPasswordResetAsync(AppUser user, string link, CancellationToken ct = default);

    Task SendInvoiceAsync(AppUser user, Invoice invoice, CancellationToken ct = default);
}

/// <summary>
/// Persists every outgoing message in the EmailMessages outbox, attempts immediate
/// delivery through IEmailSender and records the outcome for later Hangfire retries.
/// </summary>
public class EmailService : IEmailService
{
    private readonly IDbContextFactory<DailyPosterDbContext> _dbFactory;
    private readonly IEmailSender _sender;
    private readonly ILogger<EmailService> _logger;

    public EmailService(
        IDbContextFactory<DailyPosterDbContext> dbFactory,
        IEmailSender sender,
        ILogger<EmailService> logger)
    {
        _dbFactory = dbFactory;
        _sender = sender;
        _logger = logger;
    }

    public async Task SendAsync(string to, string subject, string body, bool isHtml = true, CancellationToken ct = default)
    {
        var message = new EmailMessage { To = to, Subject = subject, Body = body, IsHtml = isHtml };
        await DeliverAsync(message, ct);
    }

    public async Task SendVerificationAsync(AppUser user, string link, CancellationToken ct = default)
    {
        var body = $"""
            <h2>Welcome to Daily Poster Generator</h2>
            <p>Hi {WebUtility.HtmlEncode(user.DisplayName)},</p>
            <p>Please confirm your email address by clicking the link below:</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">Verify my email</a></p>
            <p>If you did not create an account, you can safely ignore this email.</p>
            """;

        await SendAsync(user.Email, "Verify your email", body, true, ct);
    }

    public async Task SendPasswordResetAsync(AppUser user, string link, CancellationToken ct = default)
    {
        var body = $"""
            <h2>Reset your password</h2>
            <p>Hi {WebUtility.HtmlEncode(user.DisplayName)},</p>
            <p>We received a request to reset your password. Click the link below (valid for 1 hour):</p>
            <p><a href="{WebUtility.HtmlEncode(link)}">Reset password</a></p>
            <p>If you did not request this, you can safely ignore this email.</p>
            """;

        await SendAsync(user.Email, "Reset your password", body, true, ct);
    }

    public async Task SendInvoiceAsync(AppUser user, Invoice invoice, CancellationToken ct = default)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"<h2>Invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)}</h2>");
        sb.AppendLine($"<p>Hi {WebUtility.HtmlEncode(user.DisplayName)},</p>");
        sb.AppendLine($"<p>Thank you for your payment. Invoice {WebUtility.HtmlEncode(invoice.InvoiceNumber)} is attached below:</p>");
        sb.AppendLine($"<table border='1' cellpadding='8' cellspacing='0' style='border-collapse:collapse'>");
        sb.AppendLine("<tr><td>Subtotal</td><td>" + invoice.Subtotal.ToString("0.00") + " " + invoice.Currency + "</td></tr>");
        if (invoice.Discount > 0)
        {
            sb.AppendLine("<tr><td>Discount</td><td>-" + invoice.Discount.ToString("0.00") + " " + invoice.Currency + "</td></tr>");
        }
        sb.AppendLine("<tr><td>GST (" + invoice.TaxRate.ToString("0.##") + "%)</td><td>" + invoice.TaxAmount.ToString("0.00") + " " + invoice.Currency + "</td></tr>");
        sb.AppendLine("<tr><td><strong>Total</strong></td><td><strong>" + invoice.Total.ToString("0.00") + " " + invoice.Currency + "</strong></td></tr>");
        sb.AppendLine("</table>");

        await SendAsync(user.Email, $"Your invoice {invoice.InvoiceNumber}", sb.ToString(), true, ct);
    }

    private async Task DeliverAsync(EmailMessage message, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct);
        db.EmailMessages.Add(message);

        try
        {
            await _sender.SendAsync(message, ct);
            message.Status = EmailStatus.Sent;
            message.SentAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            message.Status = EmailStatus.Failed;
            message.Error = ex.Message;
            message.Attempts++;
            _logger.LogWarning(ex, "Email delivery failed for {To}: {Subject}", message.To, message.Subject);
        }

        await db.SaveChangesAsync(ct);
    }
}
