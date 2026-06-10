using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Conversations;
using JeebGateway.Conversations.Client;
using JeebGateway.Conversations.Offers;
using JeebGateway.service.ServiceChat;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB-1488 — boundary remediation guards for the T-BE-031d chat slice.
///
/// <list type="bullet">
///   <item><b>Correction #1 (tighten AC2)</b>: the gateway computes the abstract
///     permission-tag set from the Jeeb role; the membership/role payload that
///     crosses to the shared chat-service contains NO Jeeb role name and NO
///     <c>jeeb:</c>-prefixed value.</item>
///   <item><b>Correction #3</b>: the structured-offer envelope is built AND
///     validated ONLY in jeeb-gateway (<see cref="JeebOfferEnvelope"/>); the
///     gateway→chat append contract carries the payload as an OPAQUE element with no
///     typed offer/settlement fields (chat-service stores it verbatim).</item>
/// </list>
/// These assertions fail the build if a Jeeb role name or offer-validation symbol
/// ever crosses the upstream boundary again.
/// </summary>
public sealed class ConversationUpstreamBoundaryTests
{
    // -----------------------------------------------------------------
    // Correction #1 — the role→tag mapping + no Jeeb role names upstream.
    // -----------------------------------------------------------------

    [Theory]
    [InlineData(ConversationParticipantTag.JeebOwnerRole, ConversationParticipantTag.Owner)]
    [InlineData(ConversationParticipantTag.JeebOffererRole, ConversationParticipantTag.Participant)]
    [InlineData(ConversationParticipantTag.JeebWinnerRole, ConversationParticipantTag.PrimaryParticipant)]
    public void FromJeebRole_MapsJeebRole_ToGenericTag_WithNoForbiddenToken(string jeebRole, string expectedTag)
    {
        var tag = ConversationParticipantTag.FromJeebRole(jeebRole);

        tag.Should().Be(expectedTag);
        ConversationParticipantTag.IsForbiddenUpstreamToken(tag)
            .Should().BeFalse("the generic permission tag must carry no Jeeb token upstream");
    }

    [Theory]
    [InlineData(ConversationParticipantTag.Owner, ConversationParticipantTag.JeebOwnerRole)]
    [InlineData(ConversationParticipantTag.Participant, ConversationParticipantTag.JeebOffererRole)]
    [InlineData(ConversationParticipantTag.PrimaryParticipant, ConversationParticipantTag.JeebWinnerRole)]
    public void ToJeebRole_ReDerivesJeebRole_FromGenericTag_OnReadBack(string tag, string expectedJeebRole)
    {
        ConversationParticipantTag.ToJeebRole(tag).Should().Be(expectedJeebRole);
    }

    [Fact]
    public void ToJeebRole_PassesThroughUnknownValues_NonBreaking()
    {
        // A legacy/unknown tag is never mangled (GR1 additive/non-breaking).
        ConversationParticipantTag.ToJeebRole("support").Should().Be("support");
    }

    [Fact]
    public void AddParticipant_WirePayload_CarriesGenericTag_NoJeebRoleName()
    {
        // The default seat role (offer-submit) and an explicit map both serialize a
        // GENERIC tag — never "jeeber_offerer".
        var add = new AddJeebParticipantRequest { UserId = "u-1" };

        add.RoleInConvo.Should().Be(ConversationParticipantTag.Participant);
        AssertWirePayloadHasNoForbiddenToken(add);
    }

    [Fact]
    public void AdvancePhase_WirePayload_CarriesGenericTag_NoJeebRoleName()
    {
        var advance = new AdvanceJeebPhaseRequest { WinnerUserId = "u-win" };

        advance.WinnerRoleInConvo.Should().Be(ConversationParticipantTag.PrimaryParticipant);
        AssertWirePayloadHasNoForbiddenToken(advance);
    }

    [Fact]
    public void CreateConversation_WirePayload_CarriesGenericOwnerTag_NoJeebRoleName()
    {
        var create = new CreateJeebConversationRequest { RequestId = "req-1", ClientUserId = "u-1" };

        create.OwnerRoleInConvo.Should().Be(ConversationParticipantTag.Owner);
        AssertWirePayloadHasNoForbiddenToken(create);
    }

    [Fact]
    public async Task AcceptAdvance_MintsWinningMember_UnderGenericTag_NoJeebRoleName()
    {
        // The legacy channels provisioner mints the winning member under a GENERIC
        // member Type — never the Jeeb role "jeeber".
        var chat = new RecordingChatClient { MemberIdToMint = "winner-member-1" };

        var services = new ServiceCollection();
        services.AddScoped<ServiceChatClient>(_ => chat);
        using var sp = services.BuildServiceProvider();

        var provisioner = new ChatServiceConversationProvisioner(
            sp.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new ConversationProvisionOptions { Enabled = true }),
            NullLogger<ChatServiceConversationProvisioner>.Instance);

        await provisioner.AdvanceToAcceptedAsync(
            conversationId: "conv-1",
            winningJeeberId: "jeeber-win",
            losingMemberIds: System.Array.Empty<string>(),
            CancellationToken.None);

        chat.LastMemberCreate.Should().NotBeNull();
        chat.LastMemberCreate!.Type.Should().Be(ConversationParticipantTag.PrimaryParticipant);
        ConversationParticipantTag.IsForbiddenUpstreamToken(chat.LastMemberCreate.Type)
            .Should().BeFalse("the minted winning member's Type must carry no Jeeb token upstream");
    }

    // -----------------------------------------------------------------
    // Correction #3 — offer envelope built+validated ONLY in the gateway.
    // -----------------------------------------------------------------

    [Fact]
    public void OfferEnvelope_BuildsAndValidates_InTheGateway()
    {
        var envelope = JeebOfferEnvelope.Build(
            offerId: "off-1", priceAmount: 35m, etaMinutes: 25, note: "On my way");

        envelope.Subtype.Should().Be(JeebOfferEnvelope.SubtypeOffer);
        envelope.Payload.Should().ContainKey("offer_id");
        envelope.Payload["offer_id"].Should().Be("off-1");
    }

    [Theory]
    [InlineData("", 35, 25)]          // missing offer id
    [InlineData("off-1", 0, 25)]      // fee below floor
    [InlineData("off-1", 35, 0)]      // non-positive ETA
    public void OfferEnvelope_RejectsInvalidOffer_InTheGateway(string offerId, decimal fee, int eta)
    {
        var act = () => JeebOfferEnvelope.Build(offerId, fee, eta);
        act.Should().Throw<JeebOfferEnvelopeValidationException>();
    }

    [Fact]
    public void OfferEnvelope_And_Validation_Live_Only_In_The_Gateway_Assembly()
    {
        // The offer vocabulary + validation are gateway-owned, co-located with the
        // BFF — never part of the shared chat-service contract assembly.
        typeof(JeebOfferEnvelope).Assembly
            .Should().BeSameAs(typeof(JeebConversationsController).Assembly);
    }

    [Fact]
    public void ChatAppendContract_CarriesPayload_AsOpaqueElement_NoTypedOfferFields()
    {
        // chat-service receives the structured payload as an OPAQUE JSON element —
        // there is no typed offer/settlement field on the gateway→chat append DTO,
        // so chat-service cannot apply offer/settlement-aware validation.
        var payloadProp = typeof(AppendJeebMessageRequest).GetProperty(nameof(AppendJeebMessageRequest.Payload));

        payloadProp.Should().NotBeNull();
        payloadProp!.PropertyType.Should().Be(typeof(JsonElement?),
            "the append payload must remain an opaque JSON element, never a typed offer model");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    private static void AssertWirePayloadHasNoForbiddenToken<T>(T request)
    {
        // Serialize with the SAME Newtonsoft serializer JeebConversationClient marshals
        // the wire with, then assert the full role/membership payload carries no Jeeb
        // token (jeeb / jeeber / jeeb:).
        var json = JsonConvert.SerializeObject(request);
        ConversationParticipantTag.IsForbiddenUpstreamToken(json)
            .Should().BeFalse($"the upstream role/membership payload must carry no Jeeb token: {json}");
    }

    private sealed class RecordingChatClient : ServiceChatClient
    {
        public RecordingChatClient() : base("http://chat.test/", new HttpClient()) { }

        public string? MemberIdToMint { get; set; }
        public CreateMemberRequest? LastMemberCreate { get; private set; }

        public override Task<IdentityResponse> MembersPOST2Async(
            CreateMemberRequest body, CancellationToken cancellationToken)
        {
            LastMemberCreate = body;
            return Task.FromResult(new IdentityResponse { Id = MemberIdToMint });
        }

        public override Task<IdentityResponse> MembersPOSTAsync(
            string channelId, AddChannelMembersRequest body, CancellationToken cancellationToken)
            => Task.FromResult(new IdentityResponse { Id = channelId });

        public override Task<IdentityResponse> Deactivate2Async(string memberId, CancellationToken cancellationToken)
            => Task.FromResult(new IdentityResponse { Id = memberId });
    }
}
