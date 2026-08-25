using System.ComponentModel.DataAnnotations;

namespace DailyPosterGenerator.Models;

public class TodayEventItem : EventItem
{
    public bool Selected { get; set; } = true;
}

public class HomeViewModel
{
    public DateTime Today { get; set; }

    public Poster? TodaysPoster { get; set; }

    public List<Poster> RecentPosters { get; set; } = new();

    public int TotalCount { get; set; }

    public int PublishedCount { get; set; }

    public int ReadyCount { get; set; }

    public int FailedCount { get; set; }

    public int CalendarYear { get; set; }

    public int CalendarMonth { get; set; }

    public List<DateTime> CalendarPosterDates { get; set; } = new();

    public string? Notice { get; set; }
}

public class EventPreviewViewModel
{
    public DateTime Date { get; set; }

    public List<TodayEventItem> Events { get; set; } = new();

    public bool FromApi { get; set; }

    public string? Notice { get; set; }

    public List<PosterTemplate> Templates { get; set; } = new();

    public int? SelectedTemplateId { get; set; }
}

public class PosterDetailsViewModel
{
    public Poster Poster { get; set; } = null!;

    public List<Platform> EnabledPlatforms { get; set; } = new();

    public bool IsPublishedTo(Platform platform) =>
        !string.IsNullOrWhiteSpace(Poster.PublishedPlatforms) &&
        Poster.PublishedPlatforms.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Contains(platform.Name, StringComparer.OrdinalIgnoreCase);
}
