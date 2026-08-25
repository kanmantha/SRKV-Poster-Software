namespace DailyPosterGenerator.Models;

public class EventItem
{
    public string Text { get; set; } = string.Empty;

    public int? Year { get; set; }

    public string Kind { get; set; } = "event";

    public string? Url { get; set; }
}
