using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class SettingsViewModel
{
    public bool AiEnabled { get; set; }

    [StringLength(500)]
    public string? AiEndpoint { get; set; }

    [StringLength(500)]
    public string? AiApiKey { get; set; }

    [StringLength(200)]
    public string? AiChatModel { get; set; }

    [StringLength(200)]
    public string? AiImageModel { get; set; }

    public bool AiGenerateImages { get; set; }

    [Range(10, 300)]
    public int AiTimeoutSeconds { get; set; } = 90;

    public bool SchedulerEnabled { get; set; }

    [RegularExpression(@"^([01]\d|2[0-3]):[0-5]\d$", ErrorMessage = "Time must be in HH:mm format.")]
    public string? SchedulerTime { get; set; }

    public string? PosterTheme { get; set; }

    public string? OrganizationName { get; set; }

    public string? OrganizationCity { get; set; }

    public string? OrganizationTagline { get; set; }

    public string? OrganizationFacebook { get; set; }

    public string? OrganizationInstagram { get; set; }

    public string? OrganizationPhones { get; set; }

    public bool OrganizationShowValues { get; set; } = true;

    public string? OrganizationValues { get; set; }

    public bool AiActuallyConfigured { get; set; }

    public List<Platform> Platforms { get; set; } = new();

    public string? CaptionPreview { get; set; }

    public string? HashtagsPreview { get; set; }
}
