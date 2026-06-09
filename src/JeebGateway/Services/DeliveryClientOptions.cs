namespace JeebGateway.Services;

/// <summary>
/// S07 / BR-10 — gateway-side tunables for the delivery-service typed client.
/// Bound from the existing <c>Services:Delivery</c> config block (which already
/// carries the upstream <c>BaseUrl</c>) so ops can adjust the active-delivery cap
/// without a redeploy.
/// </summary>
public sealed class DeliveryClientOptions
{
    public const string SectionName = "Services:Delivery";

    /// <summary>
    /// BR-10: the maximum number of concurrent ACTIVE deliveries a single jeeber
    /// may hold. Accepting an offer whose jeeber is already AT this limit must be
    /// rejected with a 409 <c>too-many-active-deliveries</c> rather than creating a
    /// third active delivery. delivery-service owns the authoritative active count
    /// (status NOT IN terminal); the gateway BFF only enforces the cap pre-forward
    /// using that count.
    ///
    /// Default 2 mirrors the historical
    /// <c>OffersController.ActiveDeliveriesLimit</c> literal, so the behaviour is
    /// unchanged when the key is absent. Set
    /// <c>Services:Delivery:ActiveDeliveriesLimit</c> to override.
    /// </summary>
    public int ActiveDeliveriesLimit { get; set; } = 2;

    /// <summary>
    /// Tenant the gateway scopes delivery-service rows under. delivery-service
    /// keys its lookups on <c>(id, tenant_id)</c>, so the post-accept delivery
    /// assignment (S07 N7) must re-POST under the SAME tenant the row was seeded
    /// with at create time. Mirrors <c>DeliveryRowMirror</c>'s resolution; defaults
    /// to "default" when <c>Services:Delivery:TenantId</c> is unset.
    /// </summary>
    public string TenantId { get; set; } = "default";
}
