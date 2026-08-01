using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// REGRESSION GUARD for the 2026-08-01 wrong-aggregate close defect.
///
/// <para><b>What went wrong, and why the existing suite could not see it.</b>
/// <c>DeliveryCompleteChatAutoCloseTests</c> swaps <see cref="IConversationProvisioner"/>
/// itself for a recording fake, so it asserts only that the completion hook FIRES — never
/// which upstream route the real provisioner then calls. The real
/// <see cref="ChatServiceConversationProvisioner"/> was calling the legacy CHANNEL verb
/// (<c>PATCH /api/channels/{id}/deactivate</c>) with a CONVERSATION id, which chat-service
/// cannot resolve (different Firestore collection) — an unhandled 500, retried 4x, then
/// swallowed by the provisioner's degrade-don't-fail catch. Every completed delivery left
/// its conversation open, silently. A test that stubs out the unit under test cannot fail.</para>
///
/// <para>These tests therefore drive the REAL provisioner and assert the outbound HTTP
/// request line it actually produces. Negative control: revert
/// <c>CloseConversationAsync</c> to <c>ServiceChatClient.DeactivateAsync</c> and
/// <see cref="Close_Targets_ConversationPhase_NotChannelDeactivate"/> goes red on the very
/// first assertion (path <c>api/channels/{id}/deactivate</c>).</para>
/// </summary>
public class ConversationCloseTargetsConversationAggregateTests
{
    private const string ConversationId = "158efb52-30f6-4eb6-ae4e-ccab859e481f";

    /// <summary>
    /// KEYSTONE: the close drives the CONVERSATION aggregate's phase verb, and never
    /// touches the legacy channels subsystem.
    /// </summary>
    [Fact]
    public async Task Close_Targets_ConversationPhase_NotChannelDeactivate()
    {
        var recorder = new RecordingHandler(HttpStatusCode.OK, ConversationPhaseBody("closed"));
        var provisioner = ProvisionerOver(recorder);

        await provisioner.CloseConversationAsync(ConversationId, CancellationToken.None);

        recorder.Requests.Should().HaveCount(1, "the close is exactly one composed upstream call");
        var sent = recorder.Requests.Single();

        sent.Path.Should().Be($"api/conversations/{ConversationId}/phase",
            "the conversation id must be handed to the CONVERSATION aggregate, not the channel aggregate");
        sent.Method.Should().Be("PATCH");

        // The legacy channels subsystem must not be reached at all.
        sent.Path.Should().NotContain("api/channels");
        sent.Path.Should().NotContain("deactivate");
    }

    /// <summary>
    /// The close is a phase transition ONLY. Left at <see cref="AdvanceJeebPhaseRequest"/>'s
    /// accept-shaped defaults (<c>remove_others: true</c>) chat-service would soft-remove
    /// every Restricted-role participant — a removed participant loses read access to the
    /// thread it is about to be asked to rate.
    /// </summary>
    [Fact]
    public async Task Close_SendsClosedPhase_AndDoesNotMutateRoster()
    {
        var recorder = new RecordingHandler(HttpStatusCode.OK, ConversationPhaseBody("closed"));
        var provisioner = ProvisionerOver(recorder);

        await provisioner.CloseConversationAsync(ConversationId, CancellationToken.None);

        var body = recorder.Requests.Single().Body;
        body.Should().Contain("\"phase\":\"closed\"");
        body.Should().Contain("\"remove_others\":false");
        body.Should().NotContain("\"winner_user_id\":\"");
    }

    /// <summary>
    /// DEGRADE-DON'T-FAIL is preserved: an upstream fault on the close must not escape
    /// into the committed, settled completion.
    /// </summary>
    [Fact]
    public async Task Close_WhenChatServiceFaults_DoesNotThrow()
    {
        var recorder = new RecordingHandler(HttpStatusCode.InternalServerError, "boom");
        var provisioner = ProvisionerOver(recorder);

        var close = async () =>
            await provisioner.CloseConversationAsync(ConversationId, CancellationToken.None);

        await close.Should().NotThrowAsync("a chat blip must never fail a committed completion");
        recorder.Requests.Should().HaveCount(1);
    }

    /// <summary>A null/empty conversation id is a no-op — no upstream call at all.</summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Close_WithNoConversationId_MakesNoUpstreamCall(string? conversationId)
    {
        var recorder = new RecordingHandler(HttpStatusCode.OK, ConversationPhaseBody("closed"));
        var provisioner = ProvisionerOver(recorder);

        await provisioner.CloseConversationAsync(conversationId, CancellationToken.None);

        recorder.Requests.Should().BeEmpty();
    }

    /// <summary>When auto-create is off the order never got a conversation — no call.</summary>
    [Fact]
    public async Task Close_WhenFeatureDisabled_MakesNoUpstreamCall()
    {
        var recorder = new RecordingHandler(HttpStatusCode.OK, ConversationPhaseBody("closed"));
        var provisioner = ProvisionerOver(recorder, enabled: false);

        await provisioner.CloseConversationAsync(ConversationId, CancellationToken.None);

        recorder.Requests.Should().BeEmpty();
    }

    // ----------------------------------------------------------------------
    // Harness
    // ----------------------------------------------------------------------

    private static string ConversationPhaseBody(string phase)
        => $"{{\"conversation_id\":\"{ConversationId}\",\"correlation_key\":\"req-1\",\"phase\":\"{phase}\",\"participants\":[]}}";

    /// <summary>
    /// The REAL provisioner, over a real <see cref="JeebConversationClient"/>, over a real
    /// <see cref="HttpClient"/> whose transport is the recorder. Only the socket is
    /// replaced — every layer whose behaviour is under test is the production type.
    ///
    /// <para>The legacy <c>ServiceChatClient</c> (channels subsystem) is registered over the
    /// SAME recorder ON PURPOSE. It is what the defective implementation resolved, so
    /// leaving it out would make the negative control fail for the wrong reason (a DI
    /// resolution error). With both registered, reverting the fix produces a RECORDED
    /// request to <c>api/channels/{id}/deactivate</c> and the keystone assertion fails
    /// naming the wrong path — the regression this file exists to catch.</para>
    /// </summary>
    private static ChatServiceConversationProvisioner ProvisionerOver(
        RecordingHandler handler, bool enabled = true)
    {
        const string baseUrl = "http://chat.test/";
        var services = new ServiceCollection();
        services.AddScoped<IJeebConversationClient>(_ => new JeebConversationClient(
            new HttpClient(handler) { BaseAddress = new Uri(baseUrl) }));
        services.AddScoped(_ => new JeebGateway.service.ServiceChat.ServiceChatClient(
            baseUrl, new HttpClient(handler) { BaseAddress = new Uri(baseUrl) }));

        return new ChatServiceConversationProvisioner(
            services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationProvisionOptions { Enabled = enabled }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);
    }

    private sealed record SentRequest(string Method, string Path, string Body);

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public RecordingHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public List<SentRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            Requests.Add(new SentRequest(
                request.Method.Method,
                request.RequestUri!.AbsolutePath.TrimStart('/'),
                body));

            return new HttpResponseMessage(_status) { Content = new StringContent(_body) };
        }
    }
}
