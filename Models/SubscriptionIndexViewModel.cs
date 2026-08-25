namespace DailyPosterGenerator.Models;

public class SubscriptionIndexViewModel
{
    public List<SubscriptionPlan> Plans { get; set; } = new();

    public Subscription? Subscription { get; set; }

    public int CreditsAvailable { get; set; }

    public int CreditsAllowance { get; set; }
}
