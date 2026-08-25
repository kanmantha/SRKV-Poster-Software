using SkiaSharp;

namespace DailyPosterGenerator.Services;

/// <summary>Helpers for parsing and formatting hex colours (e.g. "#FF6600").</summary>
public static class HexColor
{
    public static bool TryParse(string? hex, out SKColor color)
    {
        color = default;
        if (string.IsNullOrWhiteSpace(hex))
        {
            return false;
        }

        var h = hex.Trim().TrimStart('#');
        if (h.Length != 6)
        {
            return false;
        }

        if (!byte.TryParse(h.Substring(0, 2), System.Globalization.NumberStyles.HexNumber, null, out var r)
            || !byte.TryParse(h.Substring(2, 2), System.Globalization.NumberStyles.HexNumber, null, out var g)
            || !byte.TryParse(h.Substring(4, 2), System.Globalization.NumberStyles.HexNumber, null, out var b))
        {
            return false;
        }

        color = new SKColor(r, g, b);
        return true;
    }

    public static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    public static float Luminance(SKColor color) =>
        0.299f * color.Red + 0.587f * color.Green + 0.114f * color.Blue;

    public static SKColor AutoTextColor(SKColor background) =>
        Luminance(background) < 150 ? SKColors.White : new SKColor(31, 31, 31);
}