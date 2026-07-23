using System;

namespace JeebGateway.Partner;

/// <summary>
/// Raised when a partner-wallet precondition the gateway can check cheaply is not met — e.g. the
/// partner or jeeber has no provisioned wallet for the configured currency. Distinct from an
/// upstream <c>ServiceWallet.ApiException</c> (which controllers map via their own UpstreamProblem
/// helper): this is a gateway-side 409/422 the caller can act on, carrying NO upstream body.
/// </summary>
public sealed class PartnerWalletException : Exception
{
    public PartnerWalletException(string message) : base(message)
    {
    }
}
