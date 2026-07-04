using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using FluentAssertions;
using JeebGateway.Calls;
using JeebGateway.Services.Dispatch;
using JeebGateway.service.ServiceWallet;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-63 (contract-audit finding 4): pins the RFC7807 error shape for the
/// remaining three non-ProblemDetails surfaces — WalletController's upstream-fail
/// relay, and the two ad-hoc <c>{ error }</c> object sites in
/// JeebNotificationsController and CallsController. UserController and
/// UserPreferencesController have their own dedicated test coverage; this file
/// covers the controllers that had none.
/// </summary>
public class ErrorShapeNormalizationTests
{
    // ----- WalletController: upstream WalletApiException relay -----

    [Fact]
    public async Task Wallet_GetSystemWallet_Relays_Upstream_Failure_As_ProblemDetails()
    {
        var stub = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.BadGateway)
            {
                Content = new StringContent("wallet-service unreachable", Encoding.UTF8, "text/plain")
            });

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceWalletClient>();
                services.AddScoped(_ =>
                {
                    var http = new HttpClient(stub) { BaseAddress = new Uri("http://wallet.test/") };
                    return new ServiceWalletClient("http://wallet.test/", http);
                });
            });
        });

        var client = AdminClient(factory);

        var resp = await client.GetAsync("/api/Wallet/system-wallet");

        // JEBV4-63: was a bare StatusCode(ex.StatusCode, ex.Message) string body.
        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Status.Should().Be((int)HttpStatusCode.BadGateway);
        problem.Title.Should().Be("Upstream wallet-service error");
    }

    // ----- JeebNotificationsController: ad-hoc { error } -> ProblemDetails -----

    [Fact]
    public async Task DispatchNotification_DLQ_Returns_ProblemDetails_Not_AdHoc_Error_Object()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IJeebNotificationDispatcher>();
                services.AddSingleton<IJeebNotificationDispatcher>(new DlqDispatcher());
                services.RemoveAll<INotificationDispatchOutbox>();
                services.AddSingleton<INotificationDispatchOutbox>(new NoopOutbox());
            });
        });

        // The dispatch route carries an L1 [Authorize] (unlike the capability-only Wallet
        // routes), so edge headers alone don't authenticate — mint a real admin JWT.
        var client = JwtAdminClient(factory);

        var resp = await client.PostAsJsonAsync("/api/notifications", new
        {
            templateKey = "jeeb.request.received",
            locale = "en",
            parameters = new Dictionary<string, string>(),
            recipientUserId = Guid.NewGuid(),
        });

        // JEBV4-63: was `BadRequest(new { error = ... })` — now the same RFC7807
        // envelope as every other 4xx on this surface. (Class-level
        // [Produces("application/json")] keeps the header application/json; the
        // BODY is the ProblemDetails envelope.)
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("template render failed (simulated DLQ)");
        problem.Status.Should().Be((int)HttpStatusCode.BadRequest);
        problem.Type.Should().Be("https://jeeb.dev/errors/notification-dispatch-failed");
    }

    // ----- CallsController: ad-hoc { error } -> ProblemDetails -----

    [Fact]
    public async Task CreateCallSession_MaskedCallsDisabled_Returns_ProblemDetails_Not_AdHoc_Error_Object()
    {
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IMaskedCallService>();
                services.AddSingleton<IMaskedCallService>(new DisabledMaskedCallService());
            });
        });

        var client = ClientFor(factory, "client-jebv4-63", "client");

        var resp = await client.PostAsJsonAsync("/api/calls/session", new
        {
            deliveryId = "delivery-1",
            calleeUserId = "jeeber-1",
        });

        // JEBV4-63: was `NotFound(new { error = "Masked calls are not enabled" })`.
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Content.Headers.ContentType?.MediaType.Should().Be("application/problem+json");
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Title.Should().Be("Masked calls are not enabled");
    }

    // ----- helpers -----

    private static HttpClient AdminClient(WebApplicationFactory<Program> factory)
        => ClientFor(factory, "admin-jebv4-63", "admin");

    /// <summary>
    /// Mints an HS256 admin JWT against the host's effective Jwt config (same pattern as
    /// UserPreferencesEndpointTests) for routes that carry an L1 [Authorize] and therefore
    /// need an authenticated principal, not just trusted edge headers.
    /// </summary>
    private static HttpClient JwtAdminClient(WebApplicationFactory<Program> factory)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var creds = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: new[] { new Claim("sub", "admin-jebv4-63"), new Claim("roles", "admin") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId, string roles)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", roles);
        return client;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder) => _responder = responder;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(_responder(request));
    }

    private sealed class DlqDispatcher : IJeebNotificationDispatcher
    {
        public Task<NotificationDispatchResult> DispatchAsync(
            string templateKey, string locale, Dictionary<string, string> parameters,
            Guid recipientUserId, string? idempotencyKey = null, CancellationToken ct = default)
            => Task.FromResult(new NotificationDispatchResult(
                Guid.NewGuid(), false, NotificationDispatchStatus.DLQ, "template render failed (simulated DLQ)"));
    }

    private sealed class NoopOutbox : INotificationDispatchOutbox
    {
        public Task<bool> ExistsAsync(string idempotencyKey, CancellationToken ct = default) => Task.FromResult(false);
        public Task<NotificationDispatchEntry> AddAsync(NotificationDispatchEntry entry, CancellationToken ct = default) => Task.FromResult(entry);
        public Task<IReadOnlyList<NotificationDispatchEntry>> GetDueAsync(DateTimeOffset now, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(Array.Empty<NotificationDispatchEntry>());
        public Task MarkDeliveredAsync(Guid entryId, CancellationToken ct = default) => Task.CompletedTask;
        public Task RecordFailureAsync(Guid entryId, string error, int maxAttempts, TimeSpan retryDelay, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<NotificationDispatchEntry>> GetDlqAsync(CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<NotificationDispatchEntry>>(Array.Empty<NotificationDispatchEntry>());
        public int PendingCount => 0;
    }

    private sealed class DisabledMaskedCallService : IMaskedCallService
    {
        public Task<MaskedCallSession?> CreateSessionAsync(string deliveryId, string callerUserId, string calleeUserId, CancellationToken ct)
            => Task.FromResult<MaskedCallSession?>(null);
        public Task<bool> EndSessionAsync(string sessionId, CancellationToken ct) => Task.FromResult(false);
    }
}
