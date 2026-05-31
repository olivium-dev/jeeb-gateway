using FluentAssertions;
using JeebGateway.Extensions;
using JeebGateway.Services.Clients;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Chat;

/// <summary>
/// Config-presence swap for the chat topology map (mirrors the wallet pattern):
///   - Redis:ConnectionString present  -> RedisChatTopologyMap (durable, multi-replica)
///   - absent (dev/test)               -> InMemoryChatTopologyMap (no Redis needed)
///
/// We assert on the registered ServiceDescriptor's implementation type rather than
/// RESOLVING the service, because resolving the Redis impl would eagerly call
/// ConnectionMultiplexer.Connect(192.168.2.50:6379) — a real connection the suite
/// must never make. Inspecting the descriptor verifies the selection branch without
/// touching the network, which is exactly the seam this test guards.
/// </summary>
public sealed class ChatTopologyMapSelectionTests
{
    private static Type RegisteredTopologyImpl(string? redisConnectionString)
    {
        var settings = new Dictionary<string, string?>
        {
            // Minimal upstream URLs so AddDownstreamClients binds without throwing.
            ["Services:Chat:BaseUrl"] = "http://chat.test",
        };
        if (redisConnectionString is not null)
            settings["Redis:ConnectionString"] = redisConnectionString;

        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDownstreamClients(config);

        var descriptor = services.Single(d => d.ServiceType == typeof(IChatTopologyMap));
        descriptor.Lifetime.Should().Be(ServiceLifetime.Singleton);

        // Implementation type is on ImplementationType for type-mapped singletons.
        return descriptor.ImplementationType
            ?? descriptor.ImplementationInstance?.GetType()
            ?? throw new Xunit.Sdk.XunitException(
                "IChatTopologyMap was registered via a factory; expected a type mapping.");
    }

    [Fact]
    public void Selects_Redis_Impl_When_ConnectionString_Present()
    {
        var impl = RegisteredTopologyImpl(redisConnectionString: "192.168.2.50:6379");
        impl.Should().Be<RedisChatTopologyMap>();
    }

    [Fact]
    public void Falls_Back_To_InMemory_When_ConnectionString_Absent()
    {
        var impl = RegisteredTopologyImpl(redisConnectionString: null);
        impl.Should().Be<InMemoryChatTopologyMap>();
    }

    [Fact]
    public void Falls_Back_To_InMemory_When_ConnectionString_Blank()
    {
        // Whitespace is treated as absent (string.IsNullOrWhiteSpace guard).
        var impl = RegisteredTopologyImpl(redisConnectionString: "   ");
        impl.Should().Be<InMemoryChatTopologyMap>();
    }
}
