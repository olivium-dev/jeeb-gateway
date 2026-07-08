namespace JeebGateway.Services;

/// <summary>
/// Gateway-side tunables for the delivery-service typed client.
/// Bound from the existing <c>Services:Delivery</c> config block (which already
/// carries the upstream <c>BaseUrl</c>).
/// </summary>
public sealed class DeliveryClientOptions
{
    public const string SectionName = "Services:Delivery";

    /// <summary>
    /// Retired BR-10 active-delivery cap. Kept for config compatibility; gateway
    /// accept routes no longer enforce this value.
    /// </summary>
    public int ActiveDeliveriesLimit { get; set; } = int.MaxValue;

    /// <summary>
    /// Tenant the gateway scopes delivery-service rows under. delivery-service
    /// keys its lookups on <c>(id, tenant_id)</c>, so the post-accept delivery
    /// assignment (S07 N7) must re-POST under the SAME tenant the row was seeded
    /// with at create time. Mirrors <c>DeliveryRowMirror</c>'s resolution; defaults
    /// to "default" when <c>Services:Delivery:TenantId</c> is unset.
    /// </summary>
    public string TenantId { get; set; } = "default";
}
