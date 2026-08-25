using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class SystemSetting
{
    public int Id { get; set; }

    public int TenantId { get; set; } = 1;

    public Tenant Tenant { get; set; } = null!;

    [Required, StringLength(200)]
    public string Key { get; set; } = string.Empty;

    [StringLength(8000)]
    public string? Value { get; set; }
}
