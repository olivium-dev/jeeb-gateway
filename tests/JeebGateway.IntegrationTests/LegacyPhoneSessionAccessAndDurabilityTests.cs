using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using JeebGateway.Observability;
using JeebGateway.StateService.Idempotency;
using JeebGateway.Tokens;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class LegacyPhoneSessionAccessAndDurabilityTests
{
    private const string CanaryRoute = "/form-builder/languages";
    private const string LegacySubject = "+99999999";
    private const string OtherLegacySubject = "+88888888";
    private const string CanonicalGuid = "11111111-2222-3333-4444-555555555555";

    [Fact]
    public async Task GatewayBearer_LegacySubject_MatchesGeneric401_AndRevokesOnlyExactSubject()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<IRefreshTokenStore>();
        var exact = RefreshRecord("exact", "hash-exact", LegacySubject);
        var exactSibling = RefreshRecord("exact-sibling", "hash-exact-sibling", LegacySubject);
        var other = RefreshRecord("other", "hash-other", OtherLegacySubject);
        await store.AddAsync(exact, CancellationToken.None);
        await store.AddAsync(exactSibling, CancellationToken.None);
        await store.AddAsync(other, CancellationToken.None);

        var ordinaryInvalid = await SendOrdinaryInvalidBearer(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintGatewayBearer(factory, LegacySubject));

        var rejected = await client.GetAsync(CanaryRoute);

        await AssertSameGenericUnauthorizedContract(ordinaryInvalid, rejected);
        (await store.FindByHashAsync(exact.TokenHash, CancellationToken.None))!
            .RevokedReason.Should().Be(RevocationReason.LegacyPhoneSubject.ToString());
        (await store.FindByHashAsync(exactSibling.TokenHash, CancellationToken.None))!
            .RevokedReason.Should().Be(RevocationReason.LegacyPhoneSubject.ToString());
        (await store.FindByHashAsync(other.TokenHash, CancellationToken.None))!
            .RevokedAt.Should().BeNull("revocation must be scoped to the exact subject");
    }

    [Theory]
    [InlineData(CanonicalGuid)]
    [InlineData("oidc_operator")]
    [InlineData("partner:example")]
    public async Task GatewayBearer_NonLegacySubject_RemainsAccepted(string subject)
    {
        await using var factory = new WebApplicationFactory<Program>();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintGatewayBearer(factory, subject));

        var response = await client.GetAsync(CanaryRoute);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "the retirement rule must not become a GUID-only or opaque-subject deny rule");
    }

    [Fact]
    public async Task UserManagementBearer_DoesNotExecuteGatewayBearerRetirementHook()
    {
        await using var factory = new WebApplicationFactory<Program>();
        var store = factory.Services.GetRequiredService<IRefreshTokenStore>();
        var refresh = RefreshRecord("um-subject", "hash-um-subject", LegacySubject);
        await store.AddAsync(refresh, CancellationToken.None);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", MintUserManagementBearer(factory, LegacySubject));

        var response = await client.GetAsync(CanaryRoute);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "UM bearer remains dormant on gateway client routes");
        (await store.FindByHashAsync(refresh.TokenHash, CancellationToken.None))!
            .RevokedAt.Should().BeNull(
                "the legacy-subject hook is registered only on GatewayBearer, never UserManagement");
    }

    [Fact]
    public async Task GatewayBearer_RevocationFailure_StillReturnsGeneric401_WithBoundedTelemetryAndNoPiiLogs()
    {
        var logs = new CapturingLoggerProvider();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureLogging(logging => logging.AddProvider(logs));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IRefreshTokenStore>();
                services.AddSingleton<IRefreshTokenStore, ThrowingRevokeStore>();
            });
        });
        var measurements = new ConcurrentQueue<IReadOnlyDictionary<string, object?>>();
        using var listener = ListenForLegacySessionMeasurements(measurements);
        var rawToken = MintGatewayBearer(factory, LegacySubject);
        var ordinaryInvalid = await SendOrdinaryInvalidBearer(factory);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", rawToken);

        var rejected = await client.GetAsync(CanaryRoute);

        await AssertSameGenericUnauthorizedContract(ordinaryInvalid, rejected);
        measurements.Select(tags => tags["reason"]).Should().Contain(
            "access_legacy_subject", "revocation_failure");
        foreach (var tags in measurements)
            tags.Keys.Should().Equal("reason");
        var renderedLogs = string.Join('\n', logs.Messages);
        renderedLogs.Should().NotContain(LegacySubject);
        renderedLogs.Should().NotContain(rawToken);
        renderedLogs.Should().NotContain("hash-exact");
        renderedLogs.Should().NotContain("family");
    }

    [Fact]
    public async Task Refresh_LegacySubject_DurablyRevokesFamilies_AcrossRetryAndColdInstance()
    {
        var kv = new RecordingDurableStore();
        var firstStore = NewDurableRefreshStore(kv);
        const string raw = "durable-legacy-refresh";
        const string siblingRaw = "durable-legacy-sibling";
        const string otherRaw = "durable-other-refresh";
        await firstStore.AddAsync(
            RefreshRecord("legacy-head", TokenService.HashToken(raw), LegacySubject),
            CancellationToken.None);
        await firstStore.AddAsync(
            RefreshRecord("legacy-sibling", TokenService.HashToken(siblingRaw), LegacySubject),
            CancellationToken.None);
        await firstStore.AddAsync(
            RefreshRecord("other", TokenService.HashToken(otherRaw), CanonicalGuid),
            CancellationToken.None);
        var baselineWrites = kv.InsertedWrites;
        var firstService = NewTokenService(firstStore, new ThrowingUsersStoreAdapter());

        var firstAttempt = await firstService.RefreshAsync(raw, CancellationToken.None);

        firstAttempt.Outcome.Should().Be(RefreshOutcome.Revoked);
        firstAttempt.Tokens.Should().BeNull();
        kv.InsertedWrites.Should().Be(baselineWrites + 2,
            "both active refresh records for the exact legacy subject are durably revoked");

        var coldStore = NewDurableRefreshStore(kv);
        (await coldStore.FindByHashAsync(TokenService.HashToken(raw), CancellationToken.None))!
            .RevokedReason.Should().Be(RevocationReason.LegacyPhoneSubject.ToString());
        (await coldStore.FindByHashAsync(TokenService.HashToken(siblingRaw), CancellationToken.None))!
            .RevokedReason.Should().Be(RevocationReason.LegacyPhoneSubject.ToString());
        (await coldStore.FindByHashAsync(TokenService.HashToken(otherRaw), CancellationToken.None))!
            .RevokedAt.Should().BeNull("a different canonical subject remains active");

        var writesAfterFirstRetirement = kv.InsertedWrites;
        var retryService = NewTokenService(coldStore, new ThrowingUsersStoreAdapter());
        var retry = await retryService.RefreshAsync(raw, CancellationToken.None);

        retry.Outcome.Should().Be(RefreshOutcome.Revoked);
        retry.Tokens.Should().BeNull();
        kv.InsertedWrites.Should().Be(writesAfterFirstRetirement,
            "a restart and reattempt must observe persisted revocation and add no status revision");
    }

    private static StateServiceRefreshTokenStore NewDurableRefreshStore(IIdempotencyStore kv) =>
        new(kv, Microsoft.Extensions.Logging.Abstractions.NullLogger<StateServiceRefreshTokenStore>.Instance);

    private static TokenService NewTokenService(
        IRefreshTokenStore store,
        IUsersStoreAdapter users) =>
        new(
            store,
            users,
            Options.Create(new JwtOptions
            {
                Issuer = "jeeb-gateway",
                Audience = "jeeb-clients",
                SigningKey = "durable-legacy-session-test-key-at-least-32-bytes",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 30,
            }),
            TimeProvider.System);

    private static RefreshToken RefreshRecord(string id, string hash, string subject) => new()
    {
        TokenId = id,
        UserId = subject,
        TokenHash = hash,
        IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-5),
        ExpiresAt = DateTimeOffset.UtcNow.AddDays(1),
    };

    private static string MintGatewayBearer(
        WebApplicationFactory<Program> factory,
        string subject)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        return MintBearer(
            config["Jwt:Issuer"]!,
            config["Jwt:Audience"]!,
            config["Jwt:SigningKey"]!,
            subject);
    }

    private static string MintUserManagementBearer(
        WebApplicationFactory<Program> factory,
        string subject)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var configuredKey = config["UmJwt:SigningKey"];
        return MintBearer(
            config["UmJwt:Issuer"] ?? "user-management",
            config["UmJwt:Audience"] ?? "user-management",
            string.IsNullOrWhiteSpace(configuredKey)
                ? config["Jwt:SigningKey"]!
                : configuredKey,
            subject);
    }

    private static async Task<HttpResponseMessage> SendOrdinaryInvalidBearer(
        WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var token = MintBearer(
            "untrusted-issuer",
            config["Jwt:Audience"]!,
            config["Jwt:SigningKey"]!,
            CanonicalGuid);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await client.GetAsync(CanaryRoute);
    }

    private static string MintBearer(
        string issuer,
        string audience,
        string signingKey,
        string subject)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken(
            issuer,
            audience,
            [
                new Claim("sub", subject),
                new Claim(ClaimTypes.Sid, subject),
                new Claim("roles", "client"),
            ],
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(30),
            credentials);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    private static async Task AssertSameGenericUnauthorizedContract(
        HttpResponseMessage anonymous,
        HttpResponseMessage rejected)
    {
        rejected.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        rejected.StatusCode.Should().Be(anonymous.StatusCode);
        rejected.Headers.WwwAuthenticate.Select(value => value.ToString()).Should().Equal(
            new[] { "Bearer error=\"invalid_token\"" },
            "the standard JwtBearer invalid-token challenge is reused without a custom error or detail");
        rejected.Content.Headers.ContentType?.ToString().Should().Be(
            anonymous.Content.Headers.ContentType?.ToString());
        NormalizeTrace(await rejected.Content.ReadAsStringAsync()).Should().Be(
            NormalizeTrace(await anonymous.Content.ReadAsStringAsync()),
            "the existing generic 401 ProblemDetails contract is preserved apart from per-request traceId");
    }

    private static string NormalizeTrace(string json)
    {
        var body = JsonNode.Parse(json)!.AsObject();
        body.Remove("traceId");
        return body.ToJsonString();
    }

    private static MeterListener ListenForLegacySessionMeasurements(
        ConcurrentQueue<IReadOnlyDictionary<string, object?>> measurements)
    {
        var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == BusinessOutcomeTelemetry.MeterName
                && instrument.Name == "auth.session.legacy_phone_rejections")
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((_, _, tags, _) =>
            measurements.Enqueue(tags.ToArray().ToDictionary(tag => tag.Key, tag => tag.Value)));
        listener.Start();
        return listener;
    }

    private sealed class ThrowingRevokeStore : IRefreshTokenStore
    {
        public Task AddAsync(RefreshToken token, CancellationToken ct) => Task.CompletedTask;
        public Task<RefreshToken?> FindByHashAsync(string tokenHash, CancellationToken ct) =>
            Task.FromResult<RefreshToken?>(null);
        public Task<bool> RotateAsync(string oldTokenId, RefreshToken replacement, CancellationToken ct) =>
            Task.FromResult(false);
        public Task RevokeAsync(string tokenId, RevocationReason reason, CancellationToken ct) =>
            Task.CompletedTask;
        public Task<int> RevokeAllForUserAsync(
            string userId,
            RevocationReason reason,
            CancellationToken ct) =>
            Task.FromException<int>(new InvalidOperationException("simulated durable revocation failure"));
        public Task<int> RevokeChainAsync(
            string startTokenId,
            RevocationReason reason,
            CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class ThrowingUsersStoreAdapter : IUsersStoreAdapter
    {
        public Task<IReadOnlyList<string>> GetRolesAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("legacy refresh must reject before role lookup");
        public Task<string> GetActiveRoleAsync(string userId, CancellationToken ct) =>
            throw new InvalidOperationException("legacy refresh must reject before active-role lookup");
    }

    private sealed class RecordingDurableStore : IIdempotencyStore
    {
        private readonly ConcurrentDictionary<string, string> _rows = new(StringComparer.Ordinal);
        public int InsertedWrites { get; private set; }

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key,
            int statusCode,
            string responseBodyJson,
            int ttlSeconds,
            CancellationToken ct)
        {
            var inserted = _rows.TryAdd(key, responseBodyJson);
            if (inserted) InsertedWrites++;
            return Task.FromResult(new IdempotencyOutcome
            {
                Inserted = inserted,
                StatusCode = statusCode,
                ResponseBodyJson = _rows[key],
            });
        }

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
            Task.FromResult(_rows.TryGetValue(key, out var body)
                ? new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = body,
                }
                : null);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix,
            CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<IdempotencyOutcome>>(_rows
                .Where(row => row.Key.StartsWith(prefix, StringComparison.Ordinal))
                .Select(row => new IdempotencyOutcome
                {
                    Inserted = false,
                    StatusCode = 200,
                    ResponseBodyJson = row.Value,
                })
                .ToArray());
    }

    private sealed class CapturingLoggerProvider : ILoggerProvider
    {
        public ConcurrentQueue<string> Messages { get; } = new();
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
        public void Dispose() { }

        private sealed class CapturingLogger(ConcurrentQueue<string> messages) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) =>
                messages.Enqueue(formatter(state, exception));
        }
    }
}
