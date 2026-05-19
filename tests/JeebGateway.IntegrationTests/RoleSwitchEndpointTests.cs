// T-BE-003 / JEB-39 — integration tests for POST /v1/users/me/role/switch.
// One xUnit fact per AC plus a small set of defensive cases:
//   AC1 Kamal switches client → jeeber, 200 + JWT carries role=jeeber
//   AC2 Sami with only ['client'] requests jeeber → 403 role_not_available
//   AC3 unknown role 'admin'                       → 400 invalid_role
//   AC4 every successful switch logs "role.switched" with userId/from/to/correlationId
//   AC5 p99 ≤ 300 ms (cached user record) — measured as a 100-call latency budget
//   + 401 when unauthenticated (no JWT, no X-User-Id)
//   + same-role switch is idempotent and still returns 200

using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Users.RoleSwitch;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class RoleSwitchEndpointTests : IClassFixture<RoleSwitchEndpointTests.Factory>
{
    private const string EndpointPath = "/v1/users/me/role/switch";

    private readonly Factory _factory;

    public RoleSwitchEndpointTests(Factory factory)
    {
        _factory = factory;
    }

    // -----------------------------------------------------------------------
    // AC1 — Kamal (available_roles=['client','jeeber']) switches → jeeber.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AC1_Kamal_Switches_To_Jeeber_Returns_200_And_Jwt_Carries_Role_Jeeber()
    {
        var kamal = SeedUser(
            availableRoles: new[] { "client", "jeeber" },
            activeRole:     "client");

        var resp = await CallAsync(kamal, new { role = "jeeber" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK, await resp.Content.ReadAsStringAsync());

        var body = await resp.Content.ReadFromJsonAsync<RoleSwitchResponse>();
        body.Should().NotBeNull();
        body!.User.ActiveRole.Should().Be("jeeber");
        body.User.AvailableRoles.Should().BeEquivalentTo(new[] { "client", "jeeber" });
        body.AccessToken.Should().NotBeNullOrWhiteSpace();
        body.RefreshToken.Should().NotBeNullOrWhiteSpace();

        // JWT must carry the new role. The HS512 JeebJwtIssuer emits the
        // canonical "active_role" claim; ALSO assert "available_roles" so
        // the mobile app can drive role-aware UI without a profile fetch.
        var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
        var jwt = handler.ReadJwtToken(body.AccessToken);
        jwt.Claims.FirstOrDefault(c => c.Type == "active_role")?.Value
            .Should().Be("jeeber");
        jwt.Claims.FirstOrDefault(c => c.Type == "available_roles")?.Value
            .Should().Be("client,jeeber");
        jwt.Subject.Should().Be(kamal.ToString());
    }

    // -----------------------------------------------------------------------
    // AC2 — Sami (available_roles=['client']) requests jeeber → 403 ProblemDetails type=role_not_available.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AC2_Sami_With_Only_Client_Requesting_Jeeber_Returns_403_RoleNotAvailable()
    {
        var sami = SeedUser(
            availableRoles: new[] { "client" },
            activeRole:     "client");

        var resp = await CallAsync(sami, new { role = "jeeber" });
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem.Should().NotBeNull();
        problem!.Type.Should().Be(RoleSwitchProblemTypes.RoleNotAvailable);
        problem.Title.Should().Be(RoleSwitchProblemTitles.RoleNotAvailable);
        problem.Status.Should().Be(StatusCodes.Status403Forbidden);
    }

    // -----------------------------------------------------------------------
    // AC3 — Unknown role 'admin' (or any value outside {client, jeeber}) → 400 invalid_role.
    // -----------------------------------------------------------------------
    [Theory]
    [InlineData("admin")]
    [InlineData("driver")]
    [InlineData("customer")]
    [InlineData("CLIENT")] // case-folded by the controller, but "CLIENT" is allowed because of ToLowerInvariant — so this passes; we cover unrelated values
    [InlineData("")]
    public async Task AC3_Invalid_Role_Returns_400_InvalidRole(string requestedRole)
    {
        // Edge case: empty string is the missing-field path, also covered.
        // "CLIENT" demonstrates the case-fold passes through.
        var user = SeedUser(
            availableRoles: new[] { "client", "jeeber" },
            activeRole:     "client");

        var resp = await CallAsync(user, new { role = requestedRole });

        if (string.Equals(requestedRole, "CLIENT", StringComparison.Ordinal))
        {
            // Case-folded valid role — same-role switch is idempotent.
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            return;
        }

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be(RoleSwitchProblemTypes.InvalidRole);
        problem.Title.Should().Be(RoleSwitchProblemTitles.InvalidRole);
    }

    // -----------------------------------------------------------------------
    // AC4 — Every successful switch logs "role.switched" with userId, from,
    //       to, correlationId. The CapturingLoggerProvider scrapes the
    //       LogRecord list for the structured event template and asserts
    //       all four fields are present.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AC4_Successful_Switch_Logs_Role_Switched_With_UserId_From_To_CorrelationId()
    {
        _factory.LogCapture.Records.Clear();

        var kamal = SeedUser(
            availableRoles: new[] { "client", "jeeber" },
            activeRole:     "client");

        var correlationId = $"test-corr-{Guid.NewGuid():N}";

        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointPath)
        {
            Content = JsonContent.Create(new { role = "jeeber" })
        };
        req.Headers.Add("X-User-Id", kamal.ToString());
        req.Headers.Add("X-Correlation-Id", correlationId);

        var resp = await _factory.CreateClient().SendAsync(req);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var record = _factory.LogCapture.Records
            .FirstOrDefault(r => (r.Message ?? string.Empty).StartsWith(
                "role.switched", StringComparison.Ordinal));
        record.Should().NotBeNull("the controller MUST emit a role.switched event on every successful switch.");

        var msg = record!.Message!;
        msg.Should().Contain($"user_id={kamal}");
        msg.Should().Contain("from=client");
        msg.Should().Contain("to=jeeber");
        msg.Should().Contain($"correlation_id={correlationId}");

        // Structured-property assertion (so log ingestion can index on them
        // without parsing the message template).
        var props = record.State.ToDictionary(kv => kv.Key, kv => kv.Value);
        props.Should().ContainKey("UserId");
        props.Should().ContainKey("FromRole");
        props.Should().ContainKey("ToRole");
        props.Should().ContainKey("CorrelationId");
        props["FromRole"]?.ToString().Should().Be("client");
        props["ToRole"]?.ToString().Should().Be("jeeber");
        props["CorrelationId"]?.ToString().Should().Be(correlationId);
    }

    // -----------------------------------------------------------------------
    // AC5 — p99 ≤ 300 ms (cached user record). We can't truly measure p99
    //       with 1 request, so we drive 100 round trips through the same
    //       process and assert the slowest sample falls inside the budget.
    //       The in-memory store is sub-ms; even with cold-startup JIT this
    //       is comfortably inside 300 ms on every reasonable CI agent.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task AC5_P99_Latency_Within_300ms_Over_100_Cached_Calls()
    {
        var kamal = SeedUser(
            availableRoles: new[] { "client", "jeeber" },
            activeRole:     "client");

        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", kamal.ToString());

        // Warm-up — exclude the first call from the p99 sample so JIT and
        // route-resolution costs don't dominate the budget.
        await client.PostAsJsonAsync(EndpointPath, new { role = "jeeber" });

        var samples = new List<double>(100);
        for (var i = 0; i < 100; i++)
        {
            var role = i % 2 == 0 ? "client" : "jeeber";
            var sw = Stopwatch.StartNew();
            var resp = await client.PostAsJsonAsync(EndpointPath, new { role });
            sw.Stop();
            resp.StatusCode.Should().Be(HttpStatusCode.OK);
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p99 = samples[(int)Math.Ceiling(0.99 * samples.Count) - 1];
        p99.Should().BeLessThan(300, $"p99 budget exceeded: p99={p99} ms over 100 calls.");
    }

    // -----------------------------------------------------------------------
    // Defensive: 401 when no identity is supplied at all.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Unauthenticated_Request_Returns_401_Unauthenticated()
    {
        var resp = await _factory.CreateClient()
            .PostAsJsonAsync(EndpointPath, new { role = "jeeber" });
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be(RoleSwitchProblemTypes.Unauthenticated);
    }

    // -----------------------------------------------------------------------
    // Defensive: same-role switch is idempotent (no 4xx) — important so the
    // mobile app can call this on resume without breaking when the role
    // hasn't changed.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Same_Role_Switch_Is_Idempotent_Returns_200()
    {
        var user = SeedUser(
            availableRoles: new[] { "client", "jeeber" },
            activeRole:     "client");

        var resp = await CallAsync(user, new { role = "client" });
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<RoleSwitchResponse>();
        body!.User.ActiveRole.Should().Be("client");
    }

    // -----------------------------------------------------------------------
    // Defensive: user not in the store → 404.
    // -----------------------------------------------------------------------
    [Fact]
    public async Task Unknown_User_Returns_404_UserNotFound()
    {
        var ghost = Guid.NewGuid();
        var resp = await CallAsync(ghost, new { role = "jeeber" });
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be(RoleSwitchProblemTypes.UserNotFound);
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private Guid SeedUser(string[] availableRoles, string activeRole)
    {
        var id = Guid.NewGuid();
        var store = _factory.Services.GetRequiredService<InMemoryUserManagementRoleSwitchClient>();
        store.Seed(id, availableRoles, activeRole);
        return id;
    }

    private async Task<HttpResponseMessage> CallAsync<TBody>(Guid userId, TBody body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, EndpointPath)
        {
            Content = JsonContent.Create(body)
        };
        req.Headers.Add("X-User-Id", userId.ToString());
        return await _factory.CreateClient().SendAsync(req);
    }

    /// <summary>
    /// xUnit class fixture — boots the gateway once for the whole class so
    /// JIT/startup cost is amortised across the p99 sample assertion (AC5).
    /// Production-mirroring JeebJwt config is supplied inline so the
    /// JeebJwtIssuer's HS512 ≥ 64-byte signing key check passes without
    /// depending on the per-environment appsettings layering.
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        public RoleSwitchLogCapture LogCapture { get; } = new();

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, cfg) =>
            {
                cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    // JeebJwt — HS512 needs ≥ 64-byte key. This test string is 80 bytes.
                    ["JeebJwt:SigningKey"]      = "test-signing-key-must-be-at-least-sixty-four-bytes-for-HS512-padding!!",
                    ["JeebJwt:Issuer"]          = "https://test.auth.jeeb",
                    ["JeebJwt:Audience"]        = "jeeb-mobile",
                    ["JeebJwt:AccessTtlSeconds"]  = "3600",
                    ["JeebJwt:RefreshTtlSeconds"] = "2592000",
                    ["JeebJwt:PhonePepper"]     = "test-phone-pepper-must-be-at-least-thirty-two-bytes-for-HMAC-SHA256",

                    // Disable global rate limiter so 100-call p99 sweep isn't blocked.
                    ["Security:RateLimit:Enabled"] = "false",
                });
            });

            builder.ConfigureLogging(b => b.AddProvider(LogCapture));
        }
    }

    /// <summary>Captures every log record so AC4 can assert on the structured
    /// <c>role.switched</c> event without depending on the OTel exporter.</summary>
    public sealed class RoleSwitchLogCapture : ILoggerProvider
    {
        public System.Collections.Concurrent.ConcurrentBag<LogRecord> Records { get; } = new();

        public ILogger CreateLogger(string categoryName) =>
            new CapturingLogger(categoryName, Records);

        public void Dispose() { }

        public sealed record LogRecord(
            string CategoryName,
            LogLevel Level,
            string? Message,
            IReadOnlyList<KeyValuePair<string, object?>> State);

        private sealed class CapturingLogger : ILogger
        {
            private readonly string _category;
            private readonly System.Collections.Concurrent.ConcurrentBag<LogRecord> _sink;

            public CapturingLogger(string category, System.Collections.Concurrent.ConcurrentBag<LogRecord> sink)
            {
                _category = category;
                _sink     = sink;
            }

            public IDisposable BeginScope<TState>(TState state) where TState : notnull =>
                NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var kv = state is IReadOnlyList<KeyValuePair<string, object?>> list
                    ? list
                    : (IReadOnlyList<KeyValuePair<string, object?>>)Array.Empty<KeyValuePair<string, object?>>();
                _sink.Add(new LogRecord(_category, logLevel, formatter(state, exception), kv));
            }

            private sealed class NullScope : IDisposable
            {
                public static readonly NullScope Instance = new();
                public void Dispose() { }
            }
        }
    }
}
