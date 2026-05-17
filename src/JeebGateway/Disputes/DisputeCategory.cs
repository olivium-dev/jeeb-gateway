namespace JeebGateway.Disputes;

/// <summary>
/// Allowed dispute categories (T-backend-025 / JEEB-43). The set is fixed
/// at the protocol layer so admin dashboards can pivot on a stable taxonomy
/// — adding a category is a contract change that needs the mobile + admin
/// surface updated in lock-step.
/// </summary>
public static class DisputeCategory
{
    public const string DamagedGoods = "damaged_goods";
    public const string WrongDelivery = "wrong_delivery";
    public const string Overcharged = "overcharged";
    public const string NoDelivery = "no_delivery";
    public const string SafetyConcern = "safety_concern";
    public const string ProhibitedItem = "prohibited_item";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.Ordinal)
    {
        DamagedGoods,
        WrongDelivery,
        Overcharged,
        NoDelivery,
        SafetyConcern,
        ProhibitedItem
    };

    public static bool IsValid(string? category) =>
        !string.IsNullOrWhiteSpace(category) && All.Contains(category);
}
