using System.Text.Json;
using FluentAssertions;
using JeebGateway.Conversations.Client;
using Newtonsoft.Json;
using Xunit;
using Stj = System.Text.Json;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S08 (H4) — proves the append-message audience/payload round-trip is FAITHFUL
/// across BOTH serializer legs the conversation BFF crosses:
///   • request leg:  STJ-bound JsonElement -> Newtonsoft write (-> chat-service wire)
///   • response leg: Newtonsoft read (chat-service body) -> STJ write (-> caller)
///
/// The H4 bug: a free <c>object?</c> holding an STJ <see cref="Stj.JsonElement"/>
/// serialized by Newtonsoft produced <c>{"audience":{"ValueKind":3}}</c>; and a
/// Newtonsoft <c>JObject</c> serialized by STJ produced <c>{"x":[]}</c>. Both legs
/// mangled the open shape so chat-service received/echoed a foreign message. These
/// tests fail on the pre-fix code and pass once audience/payload are pinned to
/// <see cref="Stj.JsonElement"/> + <see cref="RawJsonElementConverter"/>.
/// </summary>
public sealed class RawJsonElementConverterTests
{
    private static Stj.JsonElement Parse(string json)
    {
        using var doc = Stj.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void RequestLeg_NewtonsoftWritesStringAudience_AsRawValue_NotStructShape()
    {
        // audience = "all" arrives STJ-bound; the client serializes the request with Newtonsoft.
        var request = new AppendJeebMessageRequest
        {
            Kind = "text",
            Audience = Parse("\"all\""),
            AuthorId = "sami",
            Body = "hi",
        };

        var wire = JsonConvert.SerializeObject(request);

        // The H4 symptom was {"audience":{"ValueKind":3}} — assert it is the value "all".
        var reparsed = JObjectish(wire);
        reparsed.GetProperty("audience").GetString().Should().Be("all");
        wire.Should().NotContain("ValueKind");
        reparsed.GetProperty("author_id").GetString().Should().Be("sami");
    }

    [Fact]
    public void RequestLeg_NewtonsoftWritesStructuredPayload_Verbatim()
    {
        var request = new AppendJeebMessageRequest
        {
            Kind = "structured",
            Subtype = "jeeb.offer",
            Payload = Parse("{\"offerId\":\"off-1\",\"priceUsd\":35,\"etaMinutes\":25}"),
            AuthorId = "kamal",
        };

        var wire = JsonConvert.SerializeObject(request);
        var reparsed = JObjectish(wire);

        wire.Should().NotContain("ValueKind");
        var payload = reparsed.GetProperty("payload");
        payload.GetProperty("offerId").GetString().Should().Be("off-1");
        payload.GetProperty("priceUsd").GetInt32().Should().Be(35);
        payload.GetProperty("etaMinutes").GetInt32().Should().Be(25);
    }

    [Fact]
    public void ResponseLeg_NewtonsoftReadsChatBody_ThenStjWritesItVerbatim()
    {
        // chat-service's append body comes in over the Newtonsoft client.
        const string chatBody =
            "{\"message_id\":\"msg-9\",\"kind\":\"text\",\"author_id\":\"sami\","
            + "\"audience\":\"all\",\"payload\":{\"x\":1},\"body\":\"On my way\"}";

        var dto = JsonConvert.DeserializeObject<JeebMessageResponse>(chatBody)!;

        // Now ASP.NET serializes the DTO with System.Text.Json on the way to the caller.
        var outJson = Stj.JsonSerializer.Serialize(dto);
        var caller = Parse(outJson);

        // The pre-fix STJ-of-JObject produced {"x":[]}; assert the value survives.
        caller.GetProperty("author_id").GetString().Should().Be("sami");
        caller.GetProperty("audience").GetString().Should().Be("all");
        caller.GetProperty("payload").GetProperty("x").GetInt32().Should().Be(1);
        caller.GetProperty("body").GetString().Should().Be("On my way");
        caller.GetProperty("message_id").GetString().Should().Be("msg-9");
        outJson.Should().NotContain("ValueKind");
    }

    [Fact]
    public void NullAudience_RoundTrips_AsNull_BothLegs()
    {
        var request = new AppendJeebMessageRequest { Kind = "text", AuthorId = "sami", Body = "x" };
        var wire = JsonConvert.SerializeObject(request);
        JObjectish(wire).GetProperty("audience").ValueKind.Should().Be(Stj.JsonValueKind.Null);

        var dto = JsonConvert.DeserializeObject<JeebMessageResponse>(
            "{\"message_id\":\"m\",\"audience\":null}")!;
        dto.Audience.Should().BeNull();
        var outJson = Stj.JsonSerializer.Serialize(dto);
        Parse(outJson).GetProperty("audience").ValueKind.Should().Be(Stj.JsonValueKind.Null);
    }

    private static Stj.JsonElement JObjectish(string json)
    {
        using var doc = Stj.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }
}
