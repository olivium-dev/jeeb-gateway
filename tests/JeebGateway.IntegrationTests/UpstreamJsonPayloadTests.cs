using FluentAssertions;
using JeebGateway.Push;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Xunit;
using Stj = System.Text.Json;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P45 (same defect class as the chat-attachment husk) — the push BFF binds its
/// <c>payload</c> with System.Text.Json (<c>[FromBody] … object Payload</c>) and
/// hands it to the NSwag <c>ServicePushNotificationClient</c>, which serializes
/// with <b>Newtonsoft</b>. Without a bridge, Newtonsoft reflects over the STJ
/// <see cref="Stj.JsonElement"/> struct and push-notification-service receives
/// <c>{"payload":{"ValueKind":1}}</c> — the caller's notification data destroyed
/// exactly the way the chat attachment's <c>object_ref</c> was.
///
/// These tests assert the bridge on the property that matters: what Newtonsoft
/// actually WRITES for the bridged value.
/// </summary>
public sealed class UpstreamJsonPayloadTests
{
    private static Stj.JsonElement Bind(string json)
    {
        // Mirrors what the ASP.NET System.Text.Json model binder yields for `object`.
        using var doc = Stj.JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static string NewtonsoftWire(object? value) =>
        JsonConvert.SerializeObject(new Envelope { Payload = value });

    private sealed class Envelope
    {
        [JsonProperty("payload")]
        public object? Payload { get; set; }
    }

    [Fact]
    public void ObjectPayload_IsWrittenVerbatim_NotAsTheJsonElementStruct()
    {
        const string json = "{\"title\":\"Offer accepted\",\"deepLink\":\"jeeb://orders/42\",\"badge\":3}";

        var wire = NewtonsoftWire(UpstreamJsonPayload.ToNewtonsoftSafe(Bind(json)));

        wire.Should().NotContain("ValueKind", "the JsonElement struct shape must never reach the upstream wire");
        var payload = JObject.Parse(wire)["payload"]!;
        JToken.DeepEquals(JObject.Parse(json), payload).Should().BeTrue();
    }

    [Fact]
    public void UnbridgedObjectPayload_ProvesTheHusk_SoTheseTestsAreNotVacuous()
    {
        // The pre-fix behaviour, asserted explicitly: hand Newtonsoft the raw
        // JsonElement and it emits the struct husk. If this ever stops being true
        // the bridge is no longer load-bearing and the tests above would pass for
        // the wrong reason.
        var wire = NewtonsoftWire(Bind("{\"title\":\"Offer accepted\"}"));

        wire.Should().Contain("ValueKind");
        wire.Should().NotContain("Offer accepted");
    }

    [Fact]
    public void ArrayPayload_IsWrittenVerbatim()
    {
        var wire = NewtonsoftWire(UpstreamJsonPayload.ToNewtonsoftSafe(Bind("[1,2,3]")));

        wire.Should().NotContain("ValueKind");
        JToken.DeepEquals(JArray.Parse("[1,2,3]"), JObject.Parse(wire)["payload"])
            .Should().BeTrue();
    }

    [Theory]
    [InlineData("\"just-a-string\"", "\"payload\":\"just-a-string\"")]
    [InlineData("42", "\"payload\":42")]
    [InlineData("true", "\"payload\":true")]
    [InlineData("null", "\"payload\":null")]
    public void ScalarPayloads_AreWrittenAsScalars(string boundJson, string expectedFragment)
    {
        var wire = NewtonsoftWire(UpstreamJsonPayload.ToNewtonsoftSafe(Bind(boundJson)));

        wire.Should().Contain(expectedFragment);
        wire.Should().NotContain("ValueKind");
    }

    [Fact]
    public void NonJsonElementValues_ArePassedThroughUntouched()
    {
        // The bridge must be a no-op for anything that is already Newtonsoft-safe,
        // so it is safe to apply at every STJ -> Newtonsoft hand-off.
        UpstreamJsonPayload.ToNewtonsoftSafe(null).Should().BeNull();
        UpstreamJsonPayload.ToNewtonsoftSafe("plain").Should().Be("plain");

        var token = JObject.Parse("{\"a\":1}");
        UpstreamJsonPayload.ToNewtonsoftSafe(token).Should().BeSameAs(token);
    }
}
