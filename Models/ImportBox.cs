using System.Text.Json.Serialization;

namespace DailyPosterGenerator.Models;

/// <summary>
/// A rectangle the user draws over an uploaded poster while building a template.
/// "erase" regions have their content removed (background continuation fill) and
/// become today's text zones; "erase-logo" regions are removed too but do NOT become
/// text zones; "keep" regions protect logos so they stay on the template background.
/// Coordinates are normalized 0..1 fractions of the canvas.
/// </summary>
public class ImportBox
{
    /// <summary>"erase", "erase-logo" or "keep".</summary>
    [JsonPropertyName("type")]
    public string Type { get; set; } = "erase";

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("w")]
    public float W { get; set; }

    [JsonPropertyName("h")]
    public float H { get; set; }

    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsKeep => string.Equals(Type, "keep", StringComparison.OrdinalIgnoreCase);

    /// <summary>True when the box marks a logo the user wants removed.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsLogoErase => string.Equals(Type, "erase-logo", StringComparison.OrdinalIgnoreCase);
}