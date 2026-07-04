using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEBV4-58 (PP-7) regression guard. The six salehly-mirror upstream clients —
/// Chat, Notification, Push, Feedback, Catalog, and Wallet/Earnings (the
/// money-read client) — were registered inline in Program.cs with ONLY a
/// BaseAddress: default 100-second HttpClient timeout, no retry, no circuit
/// breaker. A slow (not down) upstream froze the calling app screen for 100s
/// per call (and, per PP-12, a hung upstream = a hung screen).
///
/// These tests resolve the REAL registrations from the gateway host and assert:
///
///  1. every one of the six named clients now carries a resilience handler in
///     its real handler chain (retry+breaker+timeout for the read-safe four;
///     breaker+timeout only — "standard-no-retry" — for the two carrying
///     non-idempotent, non-idempotency-keyed POSTs: Push dispatch and the
///     Wallet money-mutation client);
///  2. every one of the six has an explicit sub-100s HttpClient.Timeout (30s),
///     so even outside the pipeline a single dispatched request can never pin
///     a request thread for the default 100s.
///
/// They would FAIL against the pre-fix bare registrations (no resilience
/// handler in the chain; Timeout == 100s default).
/// </summary>
public class UpstreamClientResiliencePipelineTests
{
    /// <summary>
    /// The six PP-7 stragglers plus the money-read earnings BFF client.
    /// Retry posture (documented here, enforced by the registrations in
    /// Program.cs): Chat/Notification/Feedback/Catalog/Earnings ride the full
    /// standard pipeline (retry+breaker+timeout — reads and idempotent
    /// writes); Push and Wallet get breaker+timeout ONLY ("standard-no-retry")
    /// because their POSTs (push dispatch; wallet holder/add + deactivate) are
    /// non-idempotent and carry no idempotency key.
    /// </summary>
    public static IEnumerable<object[]> StragglerClients() => new[]
    {
        new object[] { "ServiceChatClient" },
        new object[] { "ServiceNotificationClient" },
        new object[] { "ServiceFeedbackClient" },
        new object[] { "ServiceCatalogClient" },
        new object[] { "ServicePushNotificationClient" },
        new object[] { "ServiceWalletClient" },
        new object[] { JeebGateway.Controllers.JeebEarningsBffController.WalletHttpClientName },
    };

    [Theory]
    [MemberData(nameof(StragglerClients))]
    public void Straggler_Client_Carries_Resilience_Handler(string clientName)
    {
        using var factory = new WebApplicationFactory<Program>();
        var handlerFactory = factory.Services.GetRequiredService<IHttpMessageHandlerFactory>();

        var chain = WalkHandlerChain(handlerFactory.CreateHandler(clientName));

        chain.Should().Contain(
            h => h.GetType().FullName!.Contains("Resilience", StringComparison.Ordinal),
            $"named client '{clientName}' must carry a resilience handler (JEBV4-58 / PP-7: " +
            "no more default-100s-timeout, zero-retry, zero-breaker registrations)");
    }

    [Theory]
    [MemberData(nameof(StragglerClients))]
    public void Straggler_Client_Has_Sub100s_Timeout(string clientName)
    {
        using var factory = new WebApplicationFactory<Program>();
        var clientFactory = factory.Services.GetRequiredService<IHttpClientFactory>();

        using var client = clientFactory.CreateClient(clientName);

        client.Timeout.Should().Be(TimeSpan.FromSeconds(30),
            $"named client '{clientName}' must have the explicit sub-100s timeout " +
            "(JEBV4-58 AC: at absolute minimum a sub-100s explicit Timeout)");
    }

    private static IReadOnlyList<HttpMessageHandler> WalkHandlerChain(HttpMessageHandler root)
    {
        var chain = new List<HttpMessageHandler>();
        var current = root;
        while (current is not null)
        {
            chain.Add(current);
            current = current is DelegatingHandler d ? d.InnerHandler : null;
        }
        return chain;
    }
}
