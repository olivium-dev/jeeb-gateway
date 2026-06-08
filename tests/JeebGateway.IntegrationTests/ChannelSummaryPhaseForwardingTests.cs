using FluentAssertions;
using JeebGateway.service.ServiceChat;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S05 H9b / JEB-50: the gateway must FORWARD chat-service's conversation
/// <c>phase</c> through <c>GET /api/Chat/channels/{id}/summary</c>.
///
/// <para>
/// chat-service computes the phase (ChannelSummaryService.ResolvePhase —
/// "broadcasting" for an order-broadcasting channel, "direct" otherwise) and
/// returns it on the summary JSON. The gateway's chat client
/// (<see cref="ServiceChatClient"/>) deserializes that JSON into
/// <see cref="ChannelSummaryResponse"/> and the controller returns it verbatim.
/// </para>
/// <para>
/// BEFORE this change the DTO had no <c>Phase</c> property, so Newtonsoft
/// SILENTLY DROPPED chat's <c>phase</c> field and the gateway never forwarded it.
/// These tests lock the round-trip: chat's <c>phase</c> survives deserialization
/// and is re-serialized back out — i.e. the gateway forwards exactly what
/// chat-service computed, holding no phase state of its own.
/// </para>
/// </summary>
public sealed class ChannelSummaryPhaseForwardingTests
{
    [Theory]
    [InlineData("broadcasting")]
    [InlineData("direct")]
    public void Phase_from_chat_json_survives_deserialization(string phase)
    {
        // Exactly the shape chat-service's summary endpoint emits.
        var chatJson =
            "{" +
            "\"channelId\":\"channel-42\"," +
            "\"name\":\"order-req-1\"," +
            "\"members\":[]," +
            "\"lastActivityDateTime\":\"2026-06-08T00:00:00Z\"," +
            "\"seenBy\":[]," +
            $"\"phase\":\"{phase}\"" +
            "}";

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<ChannelSummaryResponse>(chatJson);

        dto.Should().NotBeNull();
        // The regression: without the Phase property this was always the default.
        dto!.Phase.Should().Be(phase);
    }

    [Fact]
    public void Phase_is_reserialized_so_the_gateway_forwards_it_to_callers()
    {
        var dto = new ChannelSummaryResponse
        {
            ChannelId = "channel-42",
            Name = "order-req-1",
            Phase = "broadcasting",
        };

        var json = Newtonsoft.Json.JsonConvert.SerializeObject(dto);

        // The wire field must be lowercase "phase" (chat-service's contract),
        // carrying the value the gateway received — proving forward, not rename.
        json.Should().Contain("\"phase\":\"broadcasting\"");
    }

    [Fact]
    public void Phase_defaults_to_direct_when_chat_omits_it()
    {
        // Backward-compat: an older chat-service that does not emit phase must not
        // break the gateway — the field is present and defaults to "direct",
        // preserving the prior (no-phase) behaviour for every existing channel.
        var legacyJson =
            "{" +
            "\"channelId\":\"channel-42\"," +
            "\"name\":\"dm\"," +
            "\"members\":[]," +
            "\"lastActivityDateTime\":\"2026-06-08T00:00:00Z\"," +
            "\"seenBy\":[]" +
            "}";

        var dto = Newtonsoft.Json.JsonConvert.DeserializeObject<ChannelSummaryResponse>(legacyJson);

        dto.Should().NotBeNull();
        dto!.Phase.Should().Be("direct");
    }
}
