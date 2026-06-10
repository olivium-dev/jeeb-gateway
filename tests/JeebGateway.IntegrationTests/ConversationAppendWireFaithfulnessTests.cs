using FluentAssertions;
using JeebGateway.Controllers;
using JeebGateway.Conversations.Client;
using Newtonsoft.Json;
using Xunit;
using Xunit.Abstractions;
using Stj = System.Text.Json;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 H4 / JEB-1475 — REGRESSION GUARD for the gateway's append SEND leg.
///
/// <para>
/// Context: a production QA found a structured <c>payload</c> stored/echoed by
/// chat-service as <c>{"valueKind":1}</c> while the sibling <c>audience</c> survived.
/// The suspected culprit was the gateway send path (da208cd's RawJsonElementConverter
/// being incomplete for payload). This test reproduces the EXACT gateway send path —
/// the System.Text.Json model-bound request body
/// (<see cref="AppendMessageBody"/>) mapped to <see cref="AppendJeebMessageRequest"/>
/// and serialized with Newtonsoft exactly as <c>JeebConversationClient.JsonContent</c>
/// does — and proves the gateway emits a BYTE-FAITHFUL payload (and audience), never
/// the JsonElement struct shape. i.e. da208cd is COMPLETE on the gateway leg; the
/// <c>{"valueKind":1}</c> mangling originates downstream (chat-service's Newtonsoft
/// response serializer reflecting a re-hydrated JsonElement), fixed in the
/// chat-service repo.
/// </para>
///
/// The existing JeebConversationsBffTests use a FAKE IJeebConversationClient, which
/// bypasses the real Newtonsoft wire serialization — so this guard closes that gap by
/// asserting the actual on-the-wire bytes.
/// </summary>
public sealed class ConversationAppendWireFaithfulnessTests
{
    private readonly ITestOutputHelper _out;
    public ConversationAppendWireFaithfulnessTests(ITestOutputHelper @out) => _out = @out;

    [Fact]
    public void GatewaySendLeg_ModelBoundBody_PayloadAndAudience_AreFaithfulOnTheWire()
    {
        // 1) The raw request body the mobile client POSTs (audience=string, payload=object).
        const string requestBody =
            "{\"kind\":\"structured\",\"subtype\":\"jeeb.offer\",\"audience\":\"all\","
            + "\"payload\":{\"offer_id\":\"off-1\",\"price_amount\":35,\"eta_minutes\":25,\"note\":\"hi\"}}";

        // 2) The ASP.NET System.Text.Json model binder deserializes it into AppendMessageBody.
        var body = Stj.JsonSerializer.Deserialize<AppendMessageBody>(requestBody)!;

        // 3) The controller maps it onto the upstream request DTO (author from bearer).
        var request = new AppendJeebMessageRequest
        {
            Kind = body.Kind,
            Subtype = body.Subtype,
            Audience = body.Audience,
            Body = body.Body,
            Payload = body.Payload,
            AuthorId = "kamal",
        };

        // 4) JeebConversationClient.JsonContent serializes the request with Newtonsoft.
        var wire = JsonConvert.SerializeObject(request);
        _out.WriteLine("GATEWAY -> CHAT-SERVICE WIRE = " + wire);

        // The gateway must NOT emit the JsonElement struct shape for EITHER field.
        wire.Should().NotContain("ValueKind");
        wire.Should().NotContain("valueKind");

        using var doc = Stj.JsonDocument.Parse(wire);
        var root = doc.RootElement;
        root.GetProperty("audience").GetString().Should().Be("all");
        var payload = root.GetProperty("payload");
        payload.GetProperty("offer_id").GetString().Should().Be("off-1");
        payload.GetProperty("price_amount").GetInt32().Should().Be(35);
        payload.GetProperty("eta_minutes").GetInt32().Should().Be(25);
        payload.GetProperty("note").GetString().Should().Be("hi");
    }
}
