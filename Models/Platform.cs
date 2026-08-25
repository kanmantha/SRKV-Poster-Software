using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class Platform
{
    public int Id { get; set; }

    [Required, StringLength(100)]
    public string Name { get; set; } = string.Empty;

    public bool Enabled { get; set; }

    [StringLength(1000)]
    public string? WebhookUrl { get; set; }

    [StringLength(200)]
    public string? AccountHandle { get; set; }
}
