namespace DailyPosterGenerator.Models;

/// <summary>
/// Supported business sectors. Templates and default settings can be tailored
/// per sector so the product works for education, restaurants, politics,
/// sports clubs, retail shops and more.
/// </summary>
public static class SectorCatalog
{
    public const string General = "general";
    public const string Education = "education";
    public const string Restaurant = "restaurant";
    public const string Politics = "politics";
    public const string Sports = "sports";
    public const string Retail = "retail";

    public static readonly string[] All =
    {
        General, Education, Restaurant, Politics, Sports, Retail
    };

    public static string Label(string? sector) => sector switch
    {
        Education => "Education / School",
        Restaurant => "Restaurant / Café",
        Politics => "Politics / Campaign",
        Sports => "Sports / Club",
        Retail => "Retail / Shop",
        _ => "General / Other"
    };

    public static string Normalize(string? sector) =>
        All.Contains(sector?.Trim().ToLowerInvariant())
            ? sector!.Trim().ToLowerInvariant()
            : General;
}