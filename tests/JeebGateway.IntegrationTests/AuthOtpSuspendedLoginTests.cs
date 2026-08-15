using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// LIVE DEFECT (hardware proof): an account suspended through the real admin API
/// (<c>PATCH /admin/users/{id}/suspend</c> → 200, <c>isSuspended=true</c> in the gateway AND
/// the user-management mirror) completed a REAL device login 46 s later —
/// <c>auth.otp.verify ok userId=2195edb0…</c>, a session was minted, the app reached HOME.
/// Suspension was enforced ONLY on <c>[RequireActiveUser]</c>-gated endpoints; the login path
/// (<c>auth.otp.request</c> → <c>auth.otp.verify</c> → token mint) never consulted it.
///
/// <para>These tests pin the fix AND the two design decisions it rests on:
/// <list type="bullet">
///   <item>S1 — a suspended account is REFUSED at verify, with a machine-readable moderation
///     reason, and NOTHING is minted.</item>
///   <item>S2 — CONTROL: an active account's login is unchanged (frozen contract shape + a real
///     JWT). Green before and after by construction — it exists to catch a regression, not to
///     go red.</item>
///   <item>S3 — the moderation lookup THROWS → FAIL CLOSED. No session is minted. A lookup
///     fault that silently admits a banned account is the same defect again.</item>
///   <item>S4 — the refusal is not an account-existence/status oracle: the pre-auth REQUEST leg
///     is byte-identical for a suspended, an active and a never-seen phone, a wrong code yields
///     the identical 401 for a suspended and an unknown phone, and the 403 is only reachable
///     AFTER the upstream validate proved control of the number.</item>
/// </list></para>
/// </summary>
public class AuthOtpSuspendedLoginTests
{
    private const string AppId = "jeeb-test-app";
    private const string SuspensionReason = "Policy violation — case 4471";

    // -----------------------------------------------------------------
    // S1 — suspended account → verify REFUSES with the moderation reason,
    //      no access/refresh token, after the OTP itself validated.
    // -----------------------------------------------------------------

    [Fact]
    public async Task S1_Verify_SuspendedAccount_Is_Refused_With_ModerationReason_AndMintsNothing()
    {
        const string phone = "+9613000101";
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        await SuspendAsync(factory, phone);

        var resp = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{phone}}", "code": "1234" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "a suspended account must be refused at the point the session is minted, "
            + "not silently admitted to HOME (the measured live defect)");

        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json",
            "the refusal must be machine-readable RFC 7807, not a bare status");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("accesstoken",
            "no session may be minted for a suspended account");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        root.GetProperty("status").GetInt32().Should().Be(403);
        root.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/account_suspended",
                "a DISTINCT machine-readable problem type — not a generic 401 — is what lets the "
                + "app render a moderation screen instead of dropping the user on HOME");
        root.GetProperty("detail").GetString().Should().Be(SuspensionReason,
            "the operator's moderation reason must reach the client");

        // Extensions the client keys on: `status` matches the app's account-status wire
        // vocabulary, `reason` is the renderable moderation text.
        root.GetProperty("accountStatus").GetString().Should().Be("suspended");
        root.GetProperty("reason").GetString().Should().Be(SuspensionReason);

        stub.ValidateCalls.Should().Be(1,
            "the refusal happens AFTER the OTP proved control of the number — so it can never "
            + "become a pre-auth enumeration oracle");
    }

    // -----------------------------------------------------------------
    // S2 — CONTROL: a non-suspended account's login is untouched.
    // -----------------------------------------------------------------

    [Fact]
    public async Task S2_Verify_ActiveAccount_HappyPath_Is_Unchanged()
    {
        const string phone = "+9613000102";
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{phone}}", "code": "1234" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        // Frozen contract shape — byte-identical to the pre-fix response.
        root.EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "accessToken", "refreshToken", "user" });
        root.GetProperty("user").EnumerateObject().Select(p => p.Name)
            .Should().BeEquivalentTo(new[] { "userId", "active_role", "available_roles" });

        root.GetProperty("accessToken").GetString()!.Split('.').Should()
            .HaveCount(3, "a real signed JWT is still minted for an active account");
        root.GetProperty("refreshToken").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("user").GetProperty("userId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    // -----------------------------------------------------------------
    // S3 — the moderation lookup throws → FAIL CLOSED (nothing minted).
    // -----------------------------------------------------------------

    [Fact]
    public async Task S3_Verify_ModerationLookupThrows_FailsClosed_AndMintsNothing()
    {
        const string phone = "+9613000103";
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, throwOnModerationLookup: true);
        var http = factory.CreateClient();

        var resp = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{phone}}", "code": "1234" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable,
            "a suspension lookup that ERRORS must refuse the login — admitting the caller on an "
            + "unclassified fault re-opens the exact hole this fixes");

        var raw = await resp.Content.ReadAsStringAsync();
        raw.ToLowerInvariant().Should().NotContain("accesstoken",
            "FAIL CLOSED means no session is minted when account status cannot be established");
        raw.ToLowerInvariant().Should().NotContain("refreshtoken");

        using var doc = JsonDocument.Parse(raw);
        doc.RootElement.GetProperty("type").GetString().Should()
            .Be("https://problems.jeeb.lb/auth/moderation_unavailable",
                "the fault is reported honestly as retriable — NOT dressed up as a suspension");
    }

    // -----------------------------------------------------------------
    // S4 — no account-existence / account-status oracle.
    // -----------------------------------------------------------------

    [Fact]
    public async Task S4_Refusal_Does_Not_Leak_Whether_The_Phone_Exists_Or_Is_Suspended()
    {
        const string suspended = "+9613000104";
        const string active = "+9613000105";
        const string unknown = "+9613000106";

        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        await SuspendAsync(factory, suspended);
        await SeedActiveAsync(factory, active);

        // (a) PRE-AUTH request leg: identical for suspended / active / never-seen.
        //     Refusing HERE would let anyone enumerate suspended (hence registered) numbers
        //     with no credential at all — which is why the refusal lives at verify.
        var reqSuspended = await ReadAsync(http, "/v1/auth/otp/request", $$"""{ "phone": "{{suspended}}" }""");
        var reqActive = await ReadAsync(http, "/v1/auth/otp/request", $$"""{ "phone": "{{active}}" }""");
        var reqUnknown = await ReadAsync(http, "/v1/auth/otp/request", $$"""{ "phone": "{{unknown}}" }""");

        reqSuspended.Should().Be(reqActive, "the pre-auth request leg must not reveal account status");
        reqSuspended.Should().Be(reqUnknown, "the pre-auth request leg must not reveal account existence");

        // (b) WRONG code: a suspended phone and a never-seen phone are indistinguishable.
        stub.ValidateThrows = new ApiException(
            "unauthorized", (int)HttpStatusCode.Unauthorized, "{}", EmptyHeaders, null);

        var badSuspended = await ReadAsync(http, "/v1/auth/otp/verify",
            $$"""{ "phone": "{{suspended}}", "code": "9999" }""");
        var badUnknown = await ReadAsync(http, "/v1/auth/otp/verify",
            $$"""{ "phone": "{{unknown}}", "code": "9999" }""");

        badSuspended.Should().Be(badUnknown,
            "without a valid code, a suspended account is indistinguishable from a non-existent one");

        // (c) The 403 is reachable ONLY after the upstream validate accepted the code.
        stub.ValidateThrows = null;
        var before = stub.ValidateCalls;
        var refused = await http.PostAsync("/v1/auth/otp/verify",
            JsonBody($$"""{ "phone": "{{suspended}}", "code": "1234" }"""));

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        stub.ValidateCalls.Should().Be(before + 1,
            "the suspension refusal sits BEHIND the OTP check, so it is only observable by someone "
            + "who already controls the number");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// Seeds a real suspension through the REAL store the admin suspend path writes.
    private static async Task SuspendAsync(WebApplicationFactory<Program> factory, string phone)
    {
        var store = factory.Services.GetRequiredService<IUsersStore>();
        var profile = await store.GetOrCreateAsync(phone, CancellationToken.None);
        var updated = await store.SuspendAsync(profile.Id, SuspensionReason, "admin-test", CancellationToken.None);
        updated.Should().NotBeNull();
        updated!.IsSuspended.Should().BeTrue("the fixture must reproduce the measured live state");
    }

    private static async Task SeedActiveAsync(WebApplicationFactory<Program> factory, string phone)
    {
        var store = factory.Services.GetRequiredService<IUsersStore>();
        await store.GetOrCreateAsync(phone, CancellationToken.None);
    }

    /// Status + body, so two responses can be compared as one opaque shape.
    private static async Task<string> ReadAsync(HttpClient http, string path, string json)
    {
        var resp = await http.PostAsync(path, JsonBody(json));
        return $"{(int)resp.StatusCode}|{await resp.Content.ReadAsStringAsync()}";
    }

    private static readonly IReadOnlyDictionary<string, IEnumerable<string>> EmptyHeaders =
        new Dictionary<string, IEnumerable<string>>();

    private static WebApplicationFactory<Program> MakeFactory(
        IServiceOTPClient stub, bool throwOnModerationLookup = false) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    // Identity resolves through the in-process store, so the fixture and the
                    // controller agree on the user id without standing up user-management.
                    f.UserManagement = false;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });

                if (throwOnModerationLookup)
                {
                    services.RemoveAll<IUsersStore>();
                    services.AddSingleton<IUsersStore>(sp =>
                        new ThrowingReadUsersStore(sp.GetRequiredService<InMemoryUsersStore>()));
                }
            });
        });

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    /// Real store in every respect EXCEPT the point-lookup the login gate performs,
    /// which faults — the "suspension lookup errored" condition.
    private sealed class ThrowingReadUsersStore : IUsersStore
    {
        private readonly IUsersStore _inner;

        public ThrowingReadUsersStore(IUsersStore inner) => _inner = inner;

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
            => throw new InvalidOperationException("moderation lookup unavailable");

        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
            => _inner.GetOrCreateAsync(userId, ct);

        public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
            => _inner.UpsertProjectionAsync(profile, ct);

        public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct)
            => _inner.UpdateProfileAsync(userId, patch, ct);

        public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct)
            => _inner.ListAddressesAsync(userId, ct);

        public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct)
            => _inner.GetAddressAsync(userId, addressId, ct);

        public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct)
            => _inner.CreateAddressAsync(userId, input, ct);

        public Task<SavedAddress?> UpdateAddressAsync(
            string userId, string addressId, AddressUpsert patch, CancellationToken ct)
            => _inner.UpdateAddressAsync(userId, addressId, patch, ct);

        public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct)
            => _inner.DeleteAddressAsync(userId, addressId, ct);

        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
            => _inner.SearchAsync(query, ct);

        public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct)
            => _inner.SuspendAsync(userId, reason, adminId, ct);

        public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct)
            => _inner.UnsuspendAsync(userId, adminId, ct);

        public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct)
            => _inner.SwitchRoleAsync(userId, newRole, ct);

        public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct)
            => _inner.GrantRoleAsync(userId, role, ct);

        public Task<UserProfile?> RevokeRoleAsync(string userId, string role, CancellationToken ct)
            => _inner.RevokeRoleAsync(userId, role, ct);

        public Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
            => _inner.PurgePiiAsync(userId, ct);
    }

    private sealed class StubServiceOtpClient : IServiceOTPClient
    {
        public int ValidateCalls { get; private set; }
        public ApiException? ValidateThrows { get; set; }

        public Task SendOTPAsync(SendOTPRequestUserID? body)
            => SendOTPAsync(body, CancellationToken.None);

        public Task SendOTPAsync(SendOTPRequestUserID? body, CancellationToken cancellationToken)
            => Task.CompletedTask;

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body)
            => ValidateOTPAsync(body, CancellationToken.None);

        public Task ValidateOTPAsync(ValidateOTPRequestModel? body, CancellationToken cancellationToken)
        {
            ValidateCalls++;
            if (ValidateThrows is not null) throw ValidateThrows;
            return Task.CompletedTask;
        }

        public Task UserAsync() => Task.CompletedTask;
        public Task UserAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
