namespace DailyPosterGenerator.Models;

public enum SubscriptionStatus
{
    Trialing = 0,
    Active = 1,
    PastDue = 2,
    Cancelled = 3,
    Expired = 4
}

public enum BillingCycle
{
    Monthly = 0,
    Yearly = 1
}

public enum InvoiceStatus
{
    Pending = 0,
    Paid = 1,
    Overdue = 2,
    Void = 3
}

public enum PaymentStatus
{
    Initiated = 0,
    Succeeded = 1,
    Failed = 2,
    Refunded = 3
}

public enum CouponType
{
    Percent = 0,
    FixedAmount = 1,
    Credits = 2
}

public enum PromoType
{
    PercentOff = 0,
    FixedOff = 1,
    FreeMonths = 2
}

public enum EmailStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}
