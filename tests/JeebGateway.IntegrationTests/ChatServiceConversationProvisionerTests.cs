using FluentAssertions;
using JeebGateway.Conversations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-50 (S05 H7): the chat-backed conversation provisioner is a no-op when the
/// ConversationAutoCreate flag is OFF — it returns null WITHOUT opening a scope
/// or touching chat-service, so today's green create path is byte-for-byte
/// unchanged until the flag is flipped. (The enabled happy/degrade paths run over
/// the live chat-service and are exercised end-to-end by the S05 H9b runner step;
/// the durable-store wiring + degrade-don't-fail contract is unit-covered in
/// DurableRequestsStoreTests via a fake provisioner.)
/// </summary>
public sealed class ChatServiceConversationProvisionerTests
{
    [Fact]
    public async Task Disabled_flag_returns_null_without_resolving_a_scope()
    {
        // A scope factory that throws if used — proves the disabled path never
        // opens a scope or reaches the chat client.
        var provisioner = new ChatServiceConversationProvisioner(
            new ThrowingScopeFactory(),
            Options.Create(new ConversationProvisionOptions { Enabled = false }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);

        var result = await provisioner.CreateBroadcastingConversationAsync(
            requestId: "req-1", clientId: "client-1", CancellationToken.None);

        result.Should().BeNull();
    }

    private sealed class ThrowingScopeFactory : IServiceScopeFactory
    {
        public IServiceScope CreateScope() =>
            throw new InvalidOperationException("disabled provisioner must not open a DI scope");
    }
}
