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
/// <para><b>D10 (Phase V run 2, 2026-08-16) — this suite used to prove nothing.</b> The fixture
/// below suspended by calling <c>IUsersStore.SuspendAsync</c>, and the gate read
/// <c>IUsersStore.GetForModerationAsync</c>. In production <c>IUsersStore</c> is
/// <c>InMemoryUsersStore</c>, so the test wrote a process-local dictionary and then read the same
/// dictionary back: a closed RAM loop that is green by construction. The product's real suspend
/// action goes <c>PATCH /admin/users/{id}/suspend</c> -&gt; <c>OwnerComposedAdminUsers.SuspendAsync</c>
/// -&gt; <c>IBanServiceClient.ApplyTerminalBanAsync</c>, i.e. ban-service, which that dictionary
/// never hears about. Suspended accounts logged in on live while this file stayed green.</para>
///
/// <para>The fixture now suspends through the SAME call the admin path makes, against a stateful
/// ban-service double, so the write and the read are joined by the product's own wiring rather
/// than by the test's assumption. S5 pins that join and its negative half.</para>
///
/// <para>These tests pin the fix AND the design decisions it rests on:
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
    // S6 (D16) — the raw i18n template ban-service really ships must never
    //            reach the client as prose; its CODE must reach it instead.
    // -----------------------------------------------------------------

    /// <summary>The literal string in ban-service's shipped config/banning-rule.json, red stage 1.</summary>
    private const string ShippedBanTemplate = "Label{{Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS}}";

    [Fact]
    public async Task S6_Suspension_Reason_That_Is_An_I18n_Template_Is_Not_Rendered_Raw()
    {
        const string phone = "+9613000106";
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, configuredBanMessage: ShippedBanTemplate);
        var http = factory.CreateClient();

        await SuspendAsync(factory, phone);

        var resp = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{phone}}", "code": "1234" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var raw = await resp.Content.ReadAsStringAsync();

        // The measured D16 body: detail and reason both carried the template verbatim.
        raw.Should().NotContain("Label{{",
            "Phase V run 3 measured this exact string on the wire; a suspended user reads it");
        raw.Should().NotContain("}}");

        using var doc = JsonDocument.Parse(raw);
        var root = doc.RootElement;

        root.GetProperty("detail").GetString().Should().Be("Contact support.");
        root.GetProperty("reason").GetString().Should().Be("Contact support.");

        // The client cannot localize what it cannot identify: the KEY must survive, because
        // the app ships en + ar and only the app knows which one the viewer reads.
        root.GetProperty("reasonCode").GetString().Should()
            .Be("Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS");
    }

    /// <summary>
    /// DISCRIMINATING CONTROL for S6. Every assertion above would also be satisfied by a
    /// gateway that threw the moderation reason away entirely and always said "Contact
    /// support." — which would be a worse defect wearing the fix's clothes. So the SAME
    /// probe, pointed at an operator's typed prose, must see that prose reach the client
    /// UNCHANGED and must see NO reasonCode (there is no key to look up).
    /// </summary>
    [Fact]
    public async Task S6b_Control_Operator_Prose_Still_Reaches_The_Client_Verbatim_With_No_Code()
    {
        const string phone = "+9613000107";
        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub, configuredBanMessage: SuspensionReason);
        var http = factory.CreateClient();

        await SuspendAsync(factory, phone);

        var resp = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{phone}}", "code": "1234" }
            """));

        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        root.GetProperty("detail").GetString().Should().Be(SuspensionReason,
            "the fix must strip TEMPLATES, not moderation reasons");
        root.GetProperty("reason").GetString().Should().Be(SuspensionReason);
        root.TryGetProperty("reasonCode", out _).Should().BeFalse(
            "absence is the signal that there is nothing for the app to look up");
    }

    /// <summary>
    /// D16, the operator half. The CMS user panel reads the SAME ban message through
    /// OwnerBackedUsersStore. An admin must not be shown a broken template either — but
    /// "Contact support." would be a downgrade for them, so the key survives instead.
    /// </summary>
    [Fact]
    public void S6c_Operator_Projection_Degrades_A_Template_To_Its_Key_Not_To_Boilerplate()
    {
        JeebGateway.Users.Moderation.ModerationReason.ForOperator(ShippedBanTemplate)
            .Should().Be("Ban.Label.YOU_ARE_BANNED_FOR_3_DAYS");

        // CONTROL: prose is untouched, so this is not "always show a key".
        JeebGateway.Users.Moderation.ModerationReason.ForOperator(SuspensionReason)
            .Should().Be(SuspensionReason);

        // And the two audiences genuinely differ — otherwise one of them is wrong.
        JeebGateway.Users.Moderation.ModerationReason.Humanize(ShippedBanTemplate)
            .Should().NotBe(
                JeebGateway.Users.Moderation.ModerationReason.ForOperator(ShippedBanTemplate));
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
            .Be("https://problems.jeeb.lb/auth/identity_unavailable",
                "the account-state authority is part of identity minting and uncertainty is retriable, not a suspension");
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
    // S5 — the store the product WRITES is the store login READS, and the
    //      store it no longer reads demonstrably does not enforce.
    // -----------------------------------------------------------------

    /// <summary>
    /// Both halves matter. (a) is the fix: a ban written by the admin path's own call refuses the
    /// login. (b) is the falsification that (a) is not an accident of the fixture: flipping
    /// IsSuspended on the users store — the store the gate USED to read, and the analogue of the
    /// Phase V probe's direct user-management edit — changes nothing. If (b) ever goes red, the
    /// gate has drifted back onto a store the product does not write.
    /// </summary>
    [Fact]
    public async Task S5_Login_Enforces_The_Store_The_Admin_Suspend_Writes_And_Only_That_Store()
    {
        const string banned = "+9613000107";
        const string usersStoreOnly = "+9613000108";

        var stub = new StubServiceOtpClient();
        using var factory = MakeFactory(stub);
        var http = factory.CreateClient();

        // (a) written through IBanServiceClient.ApplyTerminalBanAsync — the admin path's own call.
        await SuspendAsync(factory, banned);
        var refused = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{banned}}", "code": "1234" }
            """));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden,
            "the login gate must read the store the product's suspend action writes");

        // (b) written to IUsersStore only — process RAM no administrator can reach.
        var store = factory.Services.GetRequiredService<IUsersStore>();
        var profile = await store.GetOrCreateAsync(usersStoreOnly, CancellationToken.None);
        var updated = await store.SuspendAsync(profile.Id, SuspensionReason, "admin-test", CancellationToken.None);
        updated!.IsSuspended.Should().BeTrue("the fixture's own write must land, or (b) proves nothing");

        var admitted = await http.PostAsync("/v1/auth/otp/verify", JsonBody($$"""
            { "phone": "{{usersStoreOnly}}", "code": "1234" }
            """));
        admitted.StatusCode.Should().Be(HttpStatusCode.OK,
            "the gateway's in-process users projection is NOT the suspension authority; a test that "
            + "suspends there and sees a 403 is reading its own write back out of RAM — which is "
            + "exactly how D10 shipped green");
    }

    // -----------------------------------------------------------------
    // helpers
    // -----------------------------------------------------------------

    /// Suspends through the EXACT call the admin path makes — OwnerComposedAdminUsers.SuspendAsync
    /// invokes IBanServiceClient.ApplyTerminalBanAsync — so the fixture cannot drift from the
    /// product's real write. Returns the suspended user id.
    private static async Task<string> SuspendAsync(WebApplicationFactory<Program> factory, string phone)
    {
        var userId = await ResolveUserIdAsync(factory, phone);
        var ban = factory.Services.GetRequiredService<IBanServiceClient>();
        var status = await ban.ApplyTerminalBanAsync(userId, "red", CancellationToken.None);
        status.IsCurrentlyBanned.Should().BeTrue("the fixture must reproduce the measured live state");
        return userId;
    }

    /// The controller resolves identity through user-management, so the fixture asks the same
    /// authority for the canonical id the controller will use.
    private static async Task<string> ResolveUserIdAsync(
        WebApplicationFactory<Program> factory, string phone)
    {
        var users = factory.Services.GetRequiredService<IUserManagementDualRoleClient>();
        return (await users.PhoneFindOrCreateAsync(phone, CancellationToken.None)).UserId;
    }

    private static async Task SeedActiveAsync(WebApplicationFactory<Program> factory, string phone)
    {
        var users = factory.Services.GetRequiredService<IUserManagementDualRoleClient>();
        await users.PhoneFindOrCreateAsync(phone, CancellationToken.None);
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
        IServiceOTPClient stub,
        bool throwOnModerationLookup = false,
        string? configuredBanMessage = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<IServiceOTPClient>();
                services.AddSingleton(stub);
                services.RemoveAll<IUserManagementDualRoleClient>();
                services.AddSingleton<IUserManagementDualRoleClient,
                    Fakes.TestUserManagementDualRoleClient>();
                services.Configure<UpstreamFeatureFlags>(f =>
                {
                    f.Otp = true;
                    f.UserManagement = true;
                });
                services.Configure<OtpSignInOptions>(o =>
                {
                    o.ApplicationId = AppId;
                    o.TtlSeconds = 300;
                });

                // ban-service is the suspension authority; the double is stateful so a ban
                // written by the admin call is the same row the login gate reads.
                services.RemoveAll<IBanServiceClient>();
                services.AddSingleton<IBanServiceClient>(
                    new FakeBanService
                    {
                        ThrowOnStatus = throwOnModerationLookup,
                        ConfiguredMessage = configuredBanMessage ?? SuspensionReason,
                    });
            });
        });

    private static StringContent JsonBody(string json)
        => new(json, Encoding.UTF8, "application/json");

    /// <summary>
    /// Stateful stand-in for ban-service. ApplyTerminalBanAsync records a currently-banned row
    /// (what the admin suspend writes) and GetStatusAsync returns it (what the login gate and
    /// [RequireActiveUser] read) — one store, both directions, so the test cannot pass by
    /// writing and reading two different places.
    /// </summary>
    private sealed class FakeBanService : IBanServiceClient
    {
        private readonly Dictionary<string, BanStatusItem> _bans = new();

        /// Simulates ban-service being unreachable on the read leg.
        public bool ThrowOnStatus { get; init; }

        /// D16: the message ban-service actually stores. Defaults to operator prose; the
        /// template arm supplies the string the shipped banning-rule.json really holds.
        public string ConfiguredMessage { get; init; } = SuspensionReason;

        public Task<BanStatusesResult> GetStatusAsync(string userId, CancellationToken ct)
        {
            if (ThrowOnStatus) throw new HttpRequestException("ban-service unreachable");
            return Task.FromResult(new BanStatusesResult
            {
                UserId = userId,
                BanStatuses = _bans.TryGetValue(userId, out var row)
                    ? new[] { row }
                    : Array.Empty<BanStatusItem>(),
            });
        }

        public Task<BanStatusItem> ApplyTerminalBanAsync(string userId, string policyKey, CancellationToken ct)
        {
            var row = new BanStatusItem
            {
                UserId = userId,
                BanType = policyKey,
                CurrentStage = 3,
                Status = "BAN",
                Message = ConfiguredMessage,
                BannedUntil = null,
                LastUpdated = DateTimeOffset.UtcNow,
                IsCurrentlyBanned = true,
            };
            _bans[userId] = row;
            return Task.FromResult(row);
        }

        public Task<BanStatusItem> ApplyBanAsync(string userId, string banType, CancellationToken ct)
            => ApplyTerminalBanAsync(userId, banType, ct);

        public Task<BanResetResult> ForceResetAsync(string userId, CancellationToken ct)
        {
            _bans.Remove(userId, out var old);
            return Task.FromResult(new BanResetResult { OldStatus = old, NewStatus = null, Updated = old is not null });
        }
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
