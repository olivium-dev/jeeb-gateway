using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JeebGateway.Services.Bff;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests.Bff;

/// <summary>
/// JEB-67 / T-BE-031 AC3 — ServiceAuthSigningHandler unit tests.
///
/// Asserts:
///   * outbound X-Service-Auth header is attached
///   * header is well-formed: caller:ts:nonce:base64hmac
///   * HMAC is HMAC-SHA256 over "caller|ts|nonce|VERB path" and verifies
///     against the configured signing key
///   * verb + path are bound into the signature (replay against a different
///     verb fails)
///   * Enabled=false is a no-op
///   * missing key throws when Enabled=true
/// </summary>
public class ServiceAuthSigningHandlerTests
{
    private const string TestKey = "test-signing-key-32-chars-or-longer-abcdef";

    [Fact]
    public async Task Attaches_Well_Formed_Header_With_Verifiable_Signature()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var opts = Options.Create(new ServiceAuthOptions
        {
            Caller = "jeeb-gateway",
            SigningKey = TestKey,
            Enabled = true,
        });

        var handler = new ServiceAuthSigningHandler(opts, time)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/api/wallet/ledger");

        await invoker.SendAsync(req, CancellationToken.None);

        var header = capturing.LastRequest!.Headers
            .GetValues(ServiceAuthSigningHandler.HeaderName).Single();

        var parts = header.Split(':');
        parts.Should().HaveCount(4);
        parts[0].Should().Be("jeeb-gateway");
        parts[1].Should().Be("1700000000");
        parts[2].Should().HaveLength(16); // 8 bytes hex
        parts[3].Should().NotBeNullOrEmpty();

        var payload = $"{parts[0]}|{parts[1]}|{parts[2]}|POST /api/wallet/ledger";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestKey));
        var expected = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)));

        parts[3].Should().Be(expected, "the signature must verify against the configured key");
    }

    [Fact]
    public async Task Signature_Binds_Verb_So_Replay_To_Different_Verb_Fails_Verification()
    {
        var time = new FakeTimeProvider(DateTimeOffset.FromUnixTimeSeconds(1_700_000_000));
        var opts = Options.Create(new ServiceAuthOptions
        {
            Caller = "jeeb-gateway",
            SigningKey = TestKey,
            Enabled = true,
        });

        var handler = new ServiceAuthSigningHandler(opts, time)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var postReq = new HttpRequestMessage(HttpMethod.Post, "http://wallet.test/api/wallet/ledger");

        await invoker.SendAsync(postReq, CancellationToken.None);

        var header = capturing.LastRequest!.Headers
            .GetValues(ServiceAuthSigningHandler.HeaderName).Single();
        var parts = header.Split(':');

        // Verifier on the downstream replays the same signed string but with
        // a different verb. The HMAC must NOT match.
        var attackerPayload = $"{parts[0]}|{parts[1]}|{parts[2]}|DELETE /api/wallet/ledger";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(TestKey));
        var attackerSig = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(attackerPayload)));

        attackerSig.Should().NotBe(parts[3]);
    }

    [Fact]
    public async Task NoOp_When_Disabled()
    {
        var opts = Options.Create(new ServiceAuthOptions
        {
            Caller = "jeeb-gateway",
            SigningKey = "",
            Enabled = false,
        });

        var handler = new ServiceAuthSigningHandler(opts, TimeProvider.System)
        {
            InnerHandler = new CapturingHandler(),
        };

        var capturing = (CapturingHandler)handler.InnerHandler!;
        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x.test/y");

        await invoker.SendAsync(req, CancellationToken.None);

        capturing.LastRequest!.Headers.Contains(ServiceAuthSigningHandler.HeaderName).Should().BeFalse();
    }

    [Fact]
    public async Task Throws_When_Enabled_But_Signing_Key_Missing()
    {
        var opts = Options.Create(new ServiceAuthOptions
        {
            Caller = "jeeb-gateway",
            SigningKey = "",
            Enabled = true,
        });

        var handler = new ServiceAuthSigningHandler(opts, TimeProvider.System)
        {
            InnerHandler = new CapturingHandler(),
        };

        using var invoker = new HttpMessageInvoker(handler);
        using var req = new HttpRequestMessage(HttpMethod.Get, "http://x.test/y");

        var act = async () => await invoker.SendAsync(req, CancellationToken.None);

        await act.Should()
            .ThrowAsync<InvalidOperationException>()
            .WithMessage("*SigningKey*");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
