using DailyPosterGenerator.Models;

namespace DailyPosterGenerator.Services;

/// <summary>
/// Offline fallback source for "today" events used when the Wikipedia API is
/// unreachable. Contains a curated international-day calendar plus a deterministic
/// generic generator so the app always has content for any date.
/// </summary>
public static class OfflineEventCalendar
{
    // MM-DD -> (text, year)  (year 0 = unknown)
    private static readonly Dictionary<string, (string Text, int Year)[]> Calendar = new()
    {
        ["01-01"] = new[] { ("New Year's Day, celebrated around the world.", 0), ("1960: Cameroon gained independence from France.", 1960) },
        ["01-26"] = new[] { ("International Customs Day.", 0), ("1950: India became a republic.", 1950) },
        ["02-02"] = new[] { ("World Wetlands Day.", 0), ("World's first groundhog day celebration in Punxsutawney.", 0) },
        ["02-14"] = new[] { ("Valentine's Day, a celebration of love and friendship.", 0), ("1876: Alexander Graham Bell applied for the telephone patent.", 1876) },
        ["02-28"] = new[] { ("Rare Disease Day.", 0) },
        ["03-08"] = new[] { ("International Women's Day.", 0) },
        ["03-14"] = new[] { ("Pi Day, honoring the mathematical constant π.", 0), ("1879: Albert Einstein was born.", 1879) },
        ["03-21"] = new[] { ("International Day for the Elimination of Racial Discrimination.", 0) },
        ["03-22"] = new[] { ("World Water Day.", 0) },
        ["04-07"] = new[] { ("World Health Day.", 0), ("1948: The World Health Organization was founded.", 1948) },
        ["04-22"] = new[] { ("Earth Day.", 0), ("1970: The first Earth Day was held.", 1970) },
        ["04-23"] = new[] { ("World Book and Copyright Day.", 0), ("1564: William Shakespeare was born.", 1564) },
        ["05-01"] = new[] { ("International Workers' Day (Labour Day).", 0) },
        ["05-04"] = new[] { ("Star Wars Day — May the 4th be with you.", 0) },
        ["05-08"] = new[] { ("World Red Cross and Red Crescent Day.", 0) },
        ["05-12"] = new[] { ("International Nurses Day.", 0), ("1820: Florence Nightingale was born.", 1820) },
        ["05-15"] = new[] { ("International Day of Families.", 0) },
        ["05-17"] = new[] { ("World Telecommunication and Information Society Day.", 0), ("1990: The WHO removed homosexuality from its list of mental disorders.", 1990) },
        ["05-21"] = new[] { ("World Day for Cultural Diversity.", 0) },
        ["05-22"] = new[] { ("International Day for Biological Diversity.", 0) },
        ["06-01"] = new[] { ("International Children's Day.", 0) },
        ["06-05"] = new[] { ("World Environment Day.", 0) },
        ["06-08"] = new[] { ("World Oceans Day.", 0) },
        ["06-14"] = new[] { ("World Blood Donor Day.", 0) },
        ["06-20"] = new[] { ("World Refugee Day.", 0) },
        ["06-23"] = new[] { ("International Widows' Day.", 0) },
        ["07-01"] = new[] { ("International Joke Day.", 0), ("1867: Canada became a self-governing dominion.", 1867) },
        ["07-11"] = new[] { ("World Population Day.", 0) },
        ["07-15"] = new[] { ("World Youth Skills Day.", 0) },
        ["07-30"] = new[] { ("International Day of Friendship.", 0) },
        ["08-06"] = new[] { ("International Friendship Day.", 0), ("1945: The United States dropped an atomic bomb on Hiroshima, Japan.", 1945) },
        ["08-09"] = new[] { ("International Day of the World's Indigenous Peoples.", 0) },
        ["08-12"] = new[] { ("International Youth Day.", 0) },
        ["08-19"] = new[] { ("World Humanitarian Day.", 0) },
        ["08-29"] = new[] { ("International Day against Nuclear Tests.", 0) },
        ["09-05"] = new[] { ("International Day of Charity.", 0) },
        ["09-08"] = new[] { ("International Literacy Day.", 0), ("1966: Star Trek premiered on American television.", 1966) },
        ["09-15"] = new[] { ("International Day of Democracy.", 0) },
        ["09-21"] = new[] { ("International Day of Peace.", 0), ("1937: The Hobbit by J.R.R. Tolkien was first published.", 1937) },
        ["09-27"] = new[] { ("World Tourism Day.", 0) },
        ["10-01"] = new[] { ("International Day of Older Persons.", 0), ("1949: The People's Republic of China was proclaimed.", 1949) },
        ["10-05"] = new[] { ("World Teachers' Day.", 0) },
        ["10-16"] = new[] { ("World Food Day.", 0) },
        ["10-24"] = new[] { ("United Nations Day.", 0), ("1945: The United Nations Charter came into force.", 1945) },
        ["10-31"] = new[] { ("Halloween, celebrated with costumes and treats.", 0) },
        ["11-02"] = new[] { ("International Day to End Impunity for Crimes against Journalists.", 0) },
        ["11-10"] = new[] { ("World Science Day for Peace and Development.", 0) },
        ["11-11"] = new[] { ("Armistice Day, marking the end of World War I in 1918.", 1918) },
        ["11-14"] = new[] { ("World Diabetes Day.", 0), ("1889: Jawaharlal Nehru was born.", 1889) },
        ["11-16"] = new[] { ("International Day for Tolerance.", 0) },
        ["11-19"] = new[] { ("World Toilet Day.", 0) },
        ["11-20"] = new[] { ("World Children's Day.", 0) },
        ["11-25"] = new[] { ("International Day for the Elimination of Violence against Women.", 0) },
        ["12-01"] = new[] { ("World AIDS Day.", 0) },
        ["12-03"] = new[] { ("International Day of Persons with Disabilities.", 0) },
        ["12-05"] = new[] { ("World Soil Day.", 0), ("1901: Walt Disney was born.", 1901) },
        ["12-10"] = new[] { ("Human Rights Day.", 0) },
        ["12-20"] = new[] { ("International Human Solidarity Day.", 0) },
        ["12-25"] = new[] { ("Christmas Day, celebrated by billions around the world.", 0), ("1642: Isaac Newton was born.", 1642) },
        ["12-31"] = new[] { ("New Year's Eve — the last day of the year.", 0) },
    };

    private static readonly string[] HolidayNouns =
    {
        "Kindness", "Smiles", "Puzzles", "Chocolate", "Music", "Poetry", "Dance", "Laughter",
        "Coffee", "Tea", "Reading", "Storytelling", "Gardening", "Baking", "Photography",
        "Cycling", "Hiking", "Board Games", "Compliments", "Handwriting", "Dreams", "Gratitude",
        "Adventure", "Curiosity", "Innovation", "Teamwork", "Wellness", "Sustainability"
    };

    public static List<EventItem> GetEvents(DateTime date, int max)
    {
        var result = new List<EventItem>();
        var key = date.ToString("MM-dd");

        if (Calendar.TryGetValue(key, out var curated))
        {
            foreach (var (text, year) in curated)
            {
                result.Add(new EventItem { Text = text, Year = year == 0 ? null : year, Kind = "holiday" });
            }
        }

        var seed = date.Year * 10000 + date.Month * 100 + date.Day;
        var noun = HolidayNouns[Math.Abs(seed) % HolidayNouns.Length];
        result.Add(new EventItem
        {
            Text = $"{noun} Appreciation Day — a chance to celebrate {noun.ToLowerInvariant()} in your life.",
            Kind = "holiday"
        });

        result.Add(new EventItem
        {
            Text = $"{date.DayOfWeek} reflection: a great day to set one small goal and take the first step.",
            Kind = "event"
        });

        return result.Take(max).ToList();
    }
}
