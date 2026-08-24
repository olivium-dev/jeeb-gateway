using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Conversations.Realtime;
using JeebGateway.Operations.RealtimeProbe;
using JeebGateway.Realtime;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.Operations;

public sealed class StagingRealtimeProbeEndpointTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 8, 24, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task ValidSignedRequest_ReturnsExactCompleteDescriptorAndClaims()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var conversationId = "edge-probe-" + nonce;
        var topic = "jeeb:chat:" + conversationId;
        root.GetProperty("conversationId").GetString().Should().Be(conversationId);
        root.GetProperty("topic").GetString().Should().Be(topic);
        root.GetProperty("roleInConvo").GetString().Should().Be("client");
        root.GetProperty("socketUrl").GetString().Should().Be(
            RealtimeProbeDescriptorService.ExactPublicSocketUrl);
        root.GetProperty("expiresAt").GetDateTimeOffset().Should().Be(
            FixedNow.AddSeconds(120));

        var guardianToken = root.GetProperty("token").GetString()!;
        var guardian = DecodeCompactJwt(guardianToken);
        guardian.Header.GetProperty("alg").GetString().Should().Be("HS512");
        guardian.Payload.GetProperty("iss").GetString().Should().Be("live_comm");
        guardian.Payload.GetProperty("aud").GetString().Should().Be("live_comm");
        guardian.Payload.GetProperty("typ").GetString().Should().Be("access");
        guardian.Payload.GetProperty("sub").GetString().Should().Be(conversationId);
        guardian.Payload.GetProperty("role").GetString().Should().Be("user");
        JsonStrings(guardian.Payload, "scopes").Should().Equal("subscribe");
        JsonStrings(guardian.Payload, "topics").Should().Equal(topic);
        (guardian.Payload.GetProperty("exp").GetInt64()
            - guardian.Payload.GetProperty("iat").GetInt64()).Should().Be(120);
        VerifyHmacSignature(guardianToken, host.GuardianKey, SHA512.Create());

        var ticketText = root.GetProperty("ticket").GetString()!;
        var ticket = new JwtSecurityTokenHandler().ReadJwtToken(ticketText);
        ticket.Header.Alg.Should().Be(SecurityAlgorithms.HmacSha256);
        ticket.Issuer.Should().Be("jeeb-gateway");
        ticket.Audiences.Should().ContainSingle().Which.Should().Be("jeeb-realtime");
        ticket.Subject.Should().Be(conversationId);
        ticket.Claims.Should().ContainSingle(claim =>
            claim.Type == "conv" && claim.Value == conversationId);
        ticket.Claims.Should().ContainSingle(claim =>
            claim.Type == "role" && claim.Value == "client");
        var ticketIat = long.Parse(ticket.Claims.Single(c => c.Type == "iat").Value);
        (new DateTimeOffset(ticket.ValidTo).ToUnixTimeSeconds() - ticketIat).Should().Be(120);
        ValidateTicketSignature(ticketText, host.MembershipKey);

        host.Redis.Calls.Should().ContainSingle();
        host.Redis.Calls[0].Key.Should().Be(
            RedisRealtimeProbeReplayStore.KeyPrefix + nonce);
        host.Redis.Calls[0].Value.Should().Be("1");
        host.Redis.Calls[0].Expiry.Should().Be(TimeSpan.FromSeconds(120));
    }

    [Fact]
    public async Task MissingHeaders_Returns400ProblemDetails()
    {
        await using var host = await ProbeHost.StartAsync("Staging");

        using var response = await host.Client.PostAsync(
            StagingRealtimeProbeEndpoint.Route,
            content: null);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "https://jeeb.dev/errors/staging-realtime-probe-malformed");
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NonCanonicalNonceAndUppercaseSignature_Return400()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D").ToUpperInvariant();
        var timestamp = FixedNow.ToUnixTimeSeconds();
        var signature = host.Sign(timestamp, nonce).ToUpperInvariant();

        using var response = await host.SendAsync(nonce, timestamp.ToString(), signature);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task NonEmptyBody_Returns400BeforeAuthentication()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");
        var timestamp = FixedNow.ToUnixTimeSeconds();
        using var request = host.SignedRequest(nonce, timestamp);
        request.Content = new StringContent("{}", Encoding.UTF8, "application/json");

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        host.Redis.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(-61)]
    [InlineData(61)]
    public async Task ValidlySignedTimestampOutsideSixtySeconds_Returns401(int offsetSeconds)
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(
            nonce,
            FixedNow.AddSeconds(offsetSeconds).ToUnixTimeSeconds());

        await AssertProblemAsync(
            response,
            HttpStatusCode.Unauthorized,
            "https://jeeb.dev/errors/staging-realtime-probe-stale");
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task SignedNegativeTimestamp_Returns400AsNonCanonical()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, -1);

        await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "https://jeeb.dev/errors/staging-realtime-probe-malformed");
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task BadSignature_Returns403WithoutTouchingRedis()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");
        var timestamp = FixedNow.ToUnixTimeSeconds().ToString();
        var badSignature = new string('0', 64);

        using var response = await host.SendAsync(nonce, timestamp, badSignature);

        await AssertProblemAsync(
            response,
            HttpStatusCode.Forbidden,
            "https://jeeb.dev/errors/staging-realtime-probe-forbidden");
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task ReusingSignedNonce_Returns409()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        var nonce = Guid.NewGuid().ToString("D");
        var timestamp = FixedNow.ToUnixTimeSeconds();

        using var first = await host.SendSignedAsync(nonce, timestamp);
        using var replay = await host.SendSignedAsync(nonce, timestamp);

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        await AssertProblemAsync(
            replay,
            HttpStatusCode.Conflict,
            "https://jeeb.dev/errors/staging-realtime-probe-replay");
    }

    [Fact]
    public async Task RedisFault_Returns503AndNoDescriptor()
    {
        await using var host = await ProbeHost.StartAsync("Staging");
        host.Redis.ThrowOnSet = true;
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        await AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "https://jeeb.dev/errors/staging-realtime-probe-unavailable");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"token\"").And.NotContain("\"ticket\"");
    }

    [Fact]
    public async Task InexactPublicSocketUrl_Returns503BeforeRedisReservation()
    {
        await using var host = await ProbeHost.StartAsync(
            "Staging",
            publicSocketUrl: "wss://app.jeeb.fds-1.com/socket/websocket?unsafe=1");
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task InexactGuardianIssuer_Returns503BeforeRedisReservation()
    {
        await using var host = await ProbeHost.StartAsync(
            "Staging",
            guardianIssuer: "drifted-live-comm");
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task RuntimeCredentialAuthorityDrift_Returns503BeforeRedisReservation()
    {
        await using var host = await ProbeHost.StartAsync(
            "Staging",
            credentialConfigurationExact: false);
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        await AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "https://jeeb.dev/errors/staging-realtime-probe-unavailable");
        host.Redis.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task UnconfiguredGuardian_Returns503WithoutPartialDescriptor()
    {
        await using var host = await ProbeHost.StartAsync(
            "Staging",
            guardianConfigured: false);
        var nonce = Guid.NewGuid().ToString("D");

        using var response = await host.SendSignedAsync(nonce, FixedNow.ToUnixTimeSeconds());

        await AssertProblemAsync(
            response,
            HttpStatusCode.ServiceUnavailable,
            "https://jeeb.dev/errors/staging-realtime-probe-unavailable");
        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotContain("\"token\"").And.NotContain("\"ticket\"");
    }

    [Fact]
    public async Task RouteDoesNotExistOutsideStaging()
    {
        await using var host = await ProbeHost.StartAsync("Production");

        using var response = await host.Client.PostAsync(
            StagingRealtimeProbeEndpoint.Route,
            content: null);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        host.Redis.Calls.Should().BeEmpty();
    }

    private static async Task AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode status,
        string type)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("status").GetInt32().Should().Be((int)status);
        json.RootElement.GetProperty("type").GetString().Should().Be(type);
    }

    private static (JsonElement Header, JsonElement Payload) DecodeCompactJwt(string token)
    {
        var parts = token.Split('.');
        parts.Should().HaveCount(3);
        using var header = JsonDocument.Parse(Base64UrlDecode(parts[0]));
        using var payload = JsonDocument.Parse(Base64UrlDecode(parts[1]));
        return (header.RootElement.Clone(), payload.RootElement.Clone());
    }

    private static IEnumerable<string?> JsonStrings(JsonElement payload, string name)
        => payload.GetProperty(name).EnumerateArray().Select(value => value.GetString());

    private static byte[] Base64UrlDecode(string value)
    {
        var padded = value.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static void VerifyHmacSignature(
        string token,
        byte[] key,
        HashAlgorithm hash)
    {
        using (hash)
        {
            var parts = token.Split('.');
            var signingInput = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            byte[] expected = hash switch
            {
                SHA512 => HMACSHA512.HashData(key, signingInput),
                _ => throw new InvalidOperationException("Unsupported test hash."),
            };
            CryptographicOperations.FixedTimeEquals(expected, Base64UrlDecode(parts[2]))
                .Should().BeTrue();
        }
    }

    private static void ValidateTicketSignature(string token, byte[] key)
    {
        var parameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "jeeb-gateway",
            ValidateAudience = true,
            ValidAudience = "jeeb-realtime",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(key),
            ValidateLifetime = false,
        };
        var result = new JwtSecurityTokenHandler().ValidateToken(token, parameters, out _);
        result.Identity!.IsAuthenticated.Should().BeTrue();
    }

    private sealed class ProbeHost : IAsyncDisposable
    {
        private static readonly byte[] ProbeKey =
            Encoding.UTF8.GetBytes("probe-test-key-is-distinct-and-at-least-32-bytes");
        private static readonly byte[] TestGuardianKey = Encoding.UTF8.GetBytes(
            "guardian-test-key-is-distinct-and-at-least-sixty-four-bytes-0123456789");
        private static readonly byte[] TestMembershipKey = Encoding.UTF8.GetBytes(
            "membership-test-key-is-distinct-and-at-least-thirty-two-bytes");

        private readonly WebApplication _app;
        private readonly string _temporaryDirectory;

        private ProbeHost(
            WebApplication app,
            HttpClient client,
            RecordingRedisClient redis,
            string temporaryDirectory)
        {
            _app = app;
            Client = client;
            Redis = redis;
            _temporaryDirectory = temporaryDirectory;
        }

        internal HttpClient Client { get; }
        internal RecordingRedisClient Redis { get; }
        internal byte[] GuardianKey => TestGuardianKey;
        internal byte[] MembershipKey => TestMembershipKey;

        internal static async Task<ProbeHost> StartAsync(
            string environment,
            string publicSocketUrl = RealtimeProbeDescriptorService.ExactPublicSocketUrl,
            bool guardianConfigured = true,
            string guardianIssuer = RealtimeProbeDescriptorService.ExactGuardianIssuer,
            bool credentialConfigurationExact = true)
        {
            var tempDirectory = Path.Combine(
                Path.GetTempPath(),
                "jeeb-realtime-probe-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDirectory);
            var probeKeyFile = Path.Combine(tempDirectory, "probe.key");
            var membershipKeyFile = Path.Combine(tempDirectory, "membership.key");
            await File.WriteAllBytesAsync(probeKeyFile, ProbeKey);
            await File.WriteAllBytesAsync(membershipKeyFile, TestMembershipKey);

            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                EnvironmentName = environment,
            });
            builder.WebHost.UseTestServer();
            builder.Logging.ClearProviders();
            builder.Services.AddProblemDetails();

            var clock = new FakeTimeProvider(FixedNow);
            var redis = new RecordingRedisClient();
            var realtimeOptions = new RealtimeGuardianOptions
            {
                GuardianSecret = guardianConfigured
                    ? Encoding.UTF8.GetString(TestGuardianKey)
                    : null,
                MembershipTicketSigningKeyFile = membershipKeyFile,
                PublicSocketUrl = publicSocketUrl,
                GuardianIssuer = guardianIssuer,
                TenantPrefix = "jeeb",
            };
            builder.Services.AddSingleton<TimeProvider>(clock);
            builder.Services.AddSingleton<IOptions<RealtimeProbeOptions>>(
                Options.Create(new RealtimeProbeOptions { MintKeyFile = probeKeyFile }));
            builder.Services.AddSingleton<IOptions<RealtimeGuardianOptions>>(
                Options.Create(realtimeOptions));
            builder.Services.AddSingleton<IOptions<JwtOptions>>(Options.Create(new JwtOptions
            {
                SigningKey = "unused-test-session-key-at-least-32-bytes",
            }));
            builder.Services.AddSingleton<IRealtimeProbeRedisClient>(redis);
            builder.Services.AddSingleton<IRealtimeProbeReplayStore,
                RedisRealtimeProbeReplayStore>();
            builder.Services.AddSingleton<IRealtimeProbeRequestAuthenticator,
                RealtimeProbeRequestAuthenticator>();
            builder.Services.AddSingleton<IRealtimeGuardianTokenIssuer,
                RealtimeGuardianTokenIssuer>();
            builder.Services.AddSingleton<IRealtimeTicketIssuer,
                RealtimeTicketIssuer>();
            builder.Services.AddSingleton<IRealtimeProbeCredentialIssuer,
                RealtimeProbeCredentialIssuer>();
            builder.Services.AddSingleton<IRealtimeProbeCredentialConfigurationGuard>(
                new FixedCredentialConfigurationGuard(credentialConfigurationExact));
            builder.Services.AddSingleton<RealtimeTopicNames>();
            builder.Services.AddSingleton<IRealtimeProbeDescriptorService,
                RealtimeProbeDescriptorService>();

            var app = builder.Build();
            app.UseExceptionHandler();
            app.MapStagingRealtimeProbe();
            await app.StartAsync();
            return new ProbeHost(app, app.GetTestClient(), redis, tempDirectory);
        }

        internal HttpRequestMessage SignedRequest(string nonce, long timestamp)
            => Request(nonce, timestamp.ToString(), Sign(timestamp, nonce));

        internal async Task<HttpResponseMessage> SendSignedAsync(string nonce, long timestamp)
        {
            using var request = SignedRequest(nonce, timestamp);
            return await Client.SendAsync(request);
        }

        internal async Task<HttpResponseMessage> SendAsync(
            string nonce,
            string timestamp,
            string signature)
        {
            using var request = Request(nonce, timestamp, signature);
            return await Client.SendAsync(request);
        }

        internal string Sign(long timestamp, string nonce)
        {
            var canonical = RealtimeProbeRequestAuthenticator.BuildCanonical(
                timestamp.ToString(),
                nonce);
            return Convert.ToHexString(
                    HMACSHA256.HashData(ProbeKey, Encoding.UTF8.GetBytes(canonical)))
                .ToLowerInvariant();
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _app.DisposeAsync();
            Directory.Delete(_temporaryDirectory, recursive: true);
        }

        private static HttpRequestMessage Request(
            string nonce,
            string timestamp,
            string signature)
        {
            var request = new HttpRequestMessage(
                HttpMethod.Post,
                StagingRealtimeProbeEndpoint.Route);
            request.Headers.TryAddWithoutValidation(
                RealtimeProbeRequestAuthenticator.TimestampHeader,
                timestamp);
            request.Headers.TryAddWithoutValidation(
                RealtimeProbeRequestAuthenticator.NonceHeader,
                nonce);
            request.Headers.TryAddWithoutValidation(
                RealtimeProbeRequestAuthenticator.SignatureHeader,
                signature);
            return request;
        }
    }

    private sealed record FixedCredentialConfigurationGuard(bool IsExact)
        : IRealtimeProbeCredentialConfigurationGuard;

    private sealed class RecordingRedisClient : IRealtimeProbeRedisClient
    {
        private readonly HashSet<string> _keys = new(StringComparer.Ordinal);

        internal List<(string Key, string Value, TimeSpan Expiry)> Calls { get; } = new();
        internal bool ThrowOnSet { get; set; }

        public Task<bool> SetIfAbsentAsync(
            string key,
            string value,
            TimeSpan expiry,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls.Add((key, value, expiry));
            if (ThrowOnSet)
            {
                throw new InvalidOperationException("simulated Redis fault");
            }

            return Task.FromResult(_keys.Add(key));
        }
    }
}
