namespace JeebGateway.service.ServiceWallet;

/// <summary>
/// Wallet-service supports an explicit fee-policy switch that is absent from the pinned generated
/// schema. Cash handed to a partner must never create a configured fee leg, so the gateway sends
/// this field explicitly instead of relying on wallet-service's default.
/// </summary>
public partial class TransactionRequest
{
    [Newtonsoft.Json.JsonProperty(
        "applyConfiguredFees",
        Required = Newtonsoft.Json.Required.Default,
        NullValueHandling = Newtonsoft.Json.NullValueHandling.Ignore)]
    public bool? ApplyConfiguredFees { get; set; }
}
