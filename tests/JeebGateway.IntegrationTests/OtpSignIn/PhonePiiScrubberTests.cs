// SPDX-License-Identifier: Proprietary
// JEB-471 / T-BE-001 — AC-PhonePIIHash: the hardest gate.
// Ported from updated-requirements/qa-scaffolding/JEB-467/.

using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.IntegrationTests.OtpSignIn.Fixtures;
using Xunit;
using Xunit.Abstractions;

namespace JeebGateway.IntegrationTests.OtpSignIn;

[Collection("Otp")]
[Trait("Story", "JEB-37")]
[Trait("AC", "AC-PhonePIIHash")]
public sealed class PhonePiiScrubberTests : IAsyncLifetime
{
    private const string Phone           = "+96179998877";
    private const string SubscriberDigits = "79998877";

    private readonly OtpServiceWebAppFactory _factory;
    private readonly HttpClient _client;
    private readonly ITestOutputHelper _output;

    public PhonePiiScrubberTests(OtpServiceWebAppFactory factory, ITestOutputHelper output)
    {
        _factory = factory;
        _client  = _factory.CreateAuthClient();
        _output  = output;
    }

    public Task InitializeAsync()
    {
        _factory.ResetState();
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }

    [Fact(DisplayName = "AC-PhonePIIHash: no log record contains the raw phone")]
    public async Task NoLogRecord_ContainsRawPhoneSubstring()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var code = _factory.OtpClient.PeekCode(Phone)!;
        await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone = Phone, code });

        var records = _factory.LogCapture.Records.ToList();
        records.Should().NotBeEmpty();

        foreach (var rec in records)
        {
            (rec.Message ?? "").Should().NotContain("+961",
                because: $"AC-PhonePIIHash: log '{rec.CategoryName}' message must not contain '+961'. Message: '{rec.Message}'");
            (rec.Message ?? "").Should().NotContain(SubscriberDigits,
                because: $"AC-PhonePIIHash: log '{rec.CategoryName}' message must not contain subscriber digits. Message: '{rec.Message}'");

            foreach (var kv in rec.State)
            {
                var v = kv.Value?.ToString() ?? "";
                v.Should().NotContain("+961");
                v.Should().NotContain(SubscriberDigits);
            }

            foreach (var scope in rec.Scopes)
            {
                var s = scope.ToString() ?? "";
                s.Should().NotContain(SubscriberDigits);
            }
        }

        _output.WriteLine($"Scanned {records.Count} log records — all clean.");
    }

    [Fact(DisplayName = "AC-PhonePIIHash: no OTel span attribute contains the raw phone")]
    public async Task NoOTelSpanAttribute_ContainsRawPhoneSubstring()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var code = _factory.OtpClient.PeekCode(Phone)!;
        await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone = Phone, code });

        var spans = _factory.SpanExporter.Spans.ToList();
        spans.Should().NotBeEmpty(
            because: "AC6 requires OTel spans 'auth.otp.request' and 'auth.otp.verify' to be emitted");

        foreach (var span in spans)
        {
            foreach (var tag in span.Tags)
            {
                var v = tag.Value ?? "";
                v.Should().NotContain("+961");
                v.Should().NotContain(SubscriberDigits);
            }
        }
    }

    [Fact(DisplayName = "AC-PhonePIIHash: phone.hash (bcrypt $2[ab]$...) IS present in at least one log record OR span")]
    public async Task BcryptHash_IsPresent_InAtLeastOneSinkRecord()
    {
        await _client.PostAsJsonAsync("/v1/auth/otp/request", new { phone = Phone });
        var code = _factory.OtpClient.PeekCode(Phone)!;
        await _client.PostAsJsonAsync("/v1/auth/otp/verify", new { phone = Phone, code });

        bool LooksLikeBcrypt(string? s) =>
            !string.IsNullOrEmpty(s) && (s.Contains("$2a$") || s.Contains("$2b$") || s.Contains("$2y$"));

        var logHasHash = _factory.LogCapture.Records.Any(rec =>
            LooksLikeBcrypt(rec.Message) ||
            rec.State.Any(kv => LooksLikeBcrypt(kv.Value?.ToString())));

        var spanHasHash = _factory.SpanExporter.Spans.Any(span =>
            span.Tags.Any(t => LooksLikeBcrypt(t.Value)));

        (logHasHash || spanHasHash).Should().BeTrue();
    }

    [Fact(DisplayName = "AC-PhonePIIHash: ProblemDetails 4xx body never echoes the raw phone")]
    public async Task ProblemDetailsBody_DoesNotEchoRawPhone()
    {
        var response = await _client.PostAsJsonAsync("/v1/auth/otp/verify",
            new { phone = Phone, code = "000000" });

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("+961");
        body.Should().NotContain(SubscriberDigits);
    }
}
