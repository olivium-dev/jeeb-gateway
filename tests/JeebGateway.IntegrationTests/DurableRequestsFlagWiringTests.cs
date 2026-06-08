using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// SPINE-FOUNDATION / ADR-006: proves the cutover flag selects the right
/// <see cref="IRequestsStore"/> implementation and that the DEFAULT (flag
/// unset) keeps today's green in-memory create path — the S05 H3 / S01–S04
/// non-regression guard (E6: flag default false, in-memory stays the rollback
/// lever).
/// </summary>
public sealed class DurableRequestsFlagWiringTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public DurableRequestsFlagWiringTests(WebApplicationFactory<Program> factory) => _factory = factory;

    [Fact]
    public void Default_resolves_in_memory_store_unchanged_path()
    {
        // No flag override = production default (Enabled:false).
        using var scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();

        store.Should().BeOfType<InMemoryRequestsStore>(
            "the default create path must stay the in-memory store so S05 H3 (201) and S01–S04 are unchanged");
    }

    [Fact]
    public void Flag_on_resolves_durable_decorator()
    {
        var factory = _factory.WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:DurableRequests:Enabled", "true");
            // A base URL so the saga-bundle HttpClient registers cleanly; the
            // recorder is never invoked in this DI-resolution assertion.
            builder.UseSetting("JeebStateService:BaseUrl", "http://localhost:10073");
        });

        using var scope = factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IRequestsStore>();

        store.Should().BeOfType<DurableRequestsStore>(
            "with the flag on the durable decorator must front the create path");
    }
}
