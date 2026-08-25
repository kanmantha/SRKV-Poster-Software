using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class EmailMessage
{
    public int Id { get; set; }

    [Required, StringLength(300)]
    public string To { get; set; } = string.Empty;

    [Required, StringLength(300)]
    public string Subject { get; set; } = string.Empty;

    [Required]
    public string Body { get; set; } = string.Empty;

    public bool IsHtml { get; set; }

    public EmailStatus Status { get; set; } = EmailStatus.Pending;

    [StringLength(500)]
    public string? Error { get; set; }

    public int Attempts { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? SentAt { get; set; }
}
