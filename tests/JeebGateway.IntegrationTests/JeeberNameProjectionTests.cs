using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmUpdateRequest = JeebGateway.service.ServiceUserManagement.UpdateUserProfileRequest;
using UmUpdateResponse = JeebGateway.service.ServiceUserManagement.UpdateUserProfileResponse;
using UmProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// jeeberName data gap (feat/tier-unify-names lane B).
///
/// ROOT CAUSE under test: the deliveries GetById jeeberName enrichment reads the
/// gateway's LOCAL users projection (<see cref="IUsersStore"/>), but no flow ever
/// wrote a display name into it for real accounts —
/// <list type="bullet">
///   <item><description>the OTP-verify mint projects an IDENTITY-ONLY row
///     (user-management's phone find-or-create carries no name → Name = ""), and the
///     projection upsert REPLACED the whole row, so even a later-learned name was
///     wiped on the next re-login;</description></item>
///   <item><description>the profile-update proxy (PUT /api/User/profile) forwarded
///     <c>username</c> to user-management WITHOUT mirroring it locally; and</description></item>
///   <item><description>GET /v1/users/me read UM's <c>username</c> for its own response
///     but never landed it in the projection.</description></item>
/// </list>
///
/// The fixes verified here: (1) the projection upsert preserves locally-known display
/// fields when the incoming identity projection carries blank ones; (2) the profile
/// update mirrors username/profilePic/email into the projection; (3) the /me read
/// passively hydrates a MISSING local name from UM's username.
/// </summary>
public class JeeberNameProjectionTests
{
    // ---------------------------------------------------------------------
    // (1) Store — the projection upsert no longer wipes display fields.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task UpsertProjection_PreservesLocallyKnownDisplayFields_AcrossRelogin()
    {
        var store = new InMemoryUsersStore();

        // First OTP login — identity-only projection (exactly what AuthOtpController writes).
        await store.UpsertProjectionAsync(IdentityProjection("kamal-1", "+9613000001"), CancellationToken.None);

        // The profile-update mirror later lands a display name.
        await store.UpdateProfileAsync("kamal-1",
            new ProfilePatch { Name = "Kamal Haddad", AvatarUrl = "https://cdn/x.png" },
            CancellationToken.None);

        // RE-LOGIN — the same identity-only (blank-name) projection upsert again.
        await store.UpsertProjectionAsync(IdentityProjection("kamal-1", "+9613000001"), CancellationToken.None);

        var profile = await store.GetByIdAsync("kamal-1", CancellationToken.None);
        profile!.Name.Should().Be("Kamal Haddad",
            "a re-login's identity-only projection must not wipe the locally-known display name");
        profile.AvatarUrl.Should().Be("https://cdn/x.png");
        // Identity fields still come from the fresh projection.
        profile.Phone.Should().Be("+9613000001");
    }

    [Fact]
    public async Task UpsertProjection_NonBlankIncomingDisplayFields_StillWin()
    {
        var store = new InMemoryUsersStore();
        await store.UpdateProfileAsync("kamal-1", new ProfilePatch { Name = "Old Name" }, CancellationToken.None);

        var projection = IdentityProjection("kamal-1", "+9613000001");
        projection.Name = "Upstream Name";
        await store.UpsertProjectionAsync(projection, CancellationToken.None);

        (await store.GetByIdAsync("kamal-1", CancellationToken.None))!.Name.Should().Be("Upstream Name",
            "when the upstream projection actually supplies a display value it stays authoritative");
    }

    [Fact]
    public async Task UpsertProjection_AfterPiiPurge_DoesNotResurrectDisplayFields()
    {
        var store = new InMemoryUsersStore();
        await store.UpdateProfileAsync("gone-1", new ProfilePatch { Name = "To Be Purged" }, CancellationToken.None);
        (await store.PurgePiiAsync("gone-1", CancellationToken.None)).Should().BeTrue();

        // A post-purge re-login must not bring the name back (purge left it blank,
        // and preservation only carries forward NON-blank local values).
        await store.UpsertProjectionAsync(IdentityProjection("gone-1", string.Empty), CancellationToken.None);

        (await store.GetByIdAsync("gone-1", CancellationToken.None))!.Name.Should().BeEmpty();
    }

    // ---------------------------------------------------------------------
    // (2) PUT /api/User/profile mirrors the display fields into the projection.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task ProfileUpdate_MirrorsUsername_IntoLocalProjection()
    {
        var um = new StubUmClient();
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = AuthedClient(factory, userId);

        var resp = await http.PutAsJsonAsync("/api/User/profile", new
        {
            userId,
            username = "Kamal Haddad",
            profilePic = "https://cdn/kamal.png",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        um.UpdateCalls.Should().Be(1, "the proxy must still forward the update upstream");

        var store = factory.Services.GetRequiredService<IUsersStore>();
        var profile = await store.GetByIdAsync(userId, CancellationToken.None);
        profile.Should().NotBeNull("the mirror must create/patch the local projection row");
        profile!.Name.Should().Be("Kamal Haddad",
            "the deliveries jeeberName enrichment reads THIS store — the username must land here");
        profile.AvatarUrl.Should().Be("https://cdn/kamal.png");
    }

    [Fact]
    public async Task ProfileUpdate_ThenIdentityReloginUpsert_KeepsTheMirroredName()
    {
        // The full defect chain: profile update lands the name; a later OTP re-login's
        // identity-only projection upsert must not wipe it (regression pairing of the
        // controller mirror + the store preservation).
        var um = new StubUmClient();
        using var factory = MakeFactory(um);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = AuthedClient(factory, userId);

        (await http.PutAsJsonAsync("/api/User/profile", new { userId, username = "Sami K" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var store = factory.Services.GetRequiredService<IUsersStore>();
        await store.UpsertProjectionAsync(IdentityProjection(userId, "+9613000009"), CancellationToken.None);

        (await store.GetByIdAsync(userId, CancellationToken.None))!.Name.Should().Be("Sami K");
    }

    [Fact]
    public async Task ProfileUpdate_MirrorFault_NeverFlipsThe200()
    {
        // The upstream update succeeded; a local mirror fault must only log.
        var um = new StubUmClient();
        using var factory = MakeFactory(um, users: new ThrowingUsersStore());
        var userId = $"user-{Guid.NewGuid():n}";
        var http = AuthedClient(factory, userId);

        var resp = await http.PutAsJsonAsync("/api/User/profile", new { userId, username = "X" });

        resp.StatusCode.Should().Be(HttpStatusCode.OK,
            "the mirror is best-effort — a projection-store fault must never fail the update");
    }

    // ---------------------------------------------------------------------
    // (3) GET /v1/users/me passively hydrates a missing local name from UM.
    // ---------------------------------------------------------------------

    [Fact]
    public async Task GetMe_HydratesMissingLocalName_FromUmUsername()
    {
        var um = new StubUmClient { ProfileUsername = "Kamal Haddad" };
        using var factory = MakeFactory(um, umFlagOn: true);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = AuthedClient(factory, userId);

        var resp = await http.GetAsync("/v1/users/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var store = factory.Services.GetRequiredService<IUsersStore>();
        (await store.GetByIdAsync(userId, CancellationToken.None))!.Name.Should().Be("Kamal Haddad",
            "the app calls /me at login, so a UM-known username lands in the local projection "
            + "and the deliveries jeeberName enrichment can resolve it");
    }

    [Fact]
    public async Task GetMe_DoesNotOverwrite_AnAlreadyKnownLocalName()
    {
        var um = new StubUmClient { ProfileUsername = "UM Stale Name" };
        using var factory = MakeFactory(um, umFlagOn: true);
        var userId = $"user-{Guid.NewGuid():n}";
        var http = AuthedClient(factory, userId);

        var store = factory.Services.GetRequiredService<IUsersStore>();
        await store.UpdateProfileAsync(userId, new ProfilePatch { Name = "Locally Known" }, CancellationToken.None);

        (await http.GetAsync("/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);

        (await store.GetByIdAsync(userId, CancellationToken.None))!.Name.Should().Be("Locally Known",
            "the passive /me hydration only FILLS a missing name; it never overwrites one");
    }

    // ---------------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------------

    private static UserProfile IdentityProjection(string userId, string phone) => new()
    {
        // Mirrors AuthOtpController.VerifyOtp's UpsertProjectionAsync call exactly:
        // identity fields only, Name deliberately empty.
        Id = userId,
        Phone = phone,
        Name = string.Empty,
        Roles = new List<string> { Roles.Client },
        ActiveRole = Roles.Client,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow,
    };

    private static WebApplicationFactory<Program> MakeFactory(
        StubUmClient um, bool umFlagOn = false, IUsersStore? users = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);
                if (users is not null)
                {
                    services.RemoveAll<IUsersStore>();
                    services.AddSingleton(users);
                }
                if (umFlagOn)
                {
                    services.Configure<UpstreamFeatureFlags>(f => f.UserManagement = true);
                }
            });
        });

    /// <summary>Client with a genuine gateway session bearer (sub/sid = userId), the same
    /// one-session-audience token the OTP mint produces (pattern from DualRoleBffTests).</summary>
    private static HttpClient AuthedClient(WebApplicationFactory<Program> factory, string userId)
    {
        var http = factory.CreateClient();
        http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", MintGatewayBearer(factory, userId, Roles.Client));
        return http;
    }

    private static string MintGatewayBearer(WebApplicationFactory<Program> factory, string userId, params string[] roles)
    {
        var config = factory.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var signingKey = config["Jwt:SigningKey"]!;

        var creds = new Microsoft.IdentityModel.Tokens.SigningCredentials(
            new Microsoft.IdentityModel.Tokens.SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
            Microsoft.IdentityModel.Tokens.SecurityAlgorithms.HmacSha256);

        var claims = new List<System.Security.Claims.Claim>
        {
            new("sub", userId),
            new(System.Security.Claims.ClaimTypes.Sid, userId),
        };
        if (roles.Length > 0)
        {
            claims.Add(new System.Security.Claims.Claim("active_role", roles[0]));
            foreach (var r in roles) claims.Add(new System.Security.Claims.Claim("roles", r));
        }

        var token = new System.IdentityModel.Tokens.Jwt.JwtSecurityToken(
            issuer: config["Jwt:Issuer"]!,
            audience: config["Jwt:Audience"]!,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: creds);

        return new System.IdentityModel.Tokens.Jwt.JwtSecurityTokenHandler().WriteToken(token);
    }

    /// <summary>Stub over the generated UM client: UpdateAsync echoes the submitted fields
    /// (what UM persists is what it returns); ProfileAsync serves a configurable username.</summary>
    private sealed class StubUmClient : UmClient
    {
        public StubUmClient() : base("http://localhost", new HttpClient()) { }

        public int UpdateCalls { get; private set; }
        public string? ProfileUsername { get; init; }

        public override Task<UmUpdateResponse> UpdateAsync(UmUpdateRequest? body, CancellationToken ct)
        {
            UpdateCalls++;
            return Task.FromResult(new UmUpdateResponse
            {
                UserId = body?.UserId,
                Username = body?.Username,
                Email = body?.Email,
                ProfilePic = body?.ProfilePic,
            });
        }

        public override Task<UmProfileResponse> ProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult(new UmProfileResponse
            {
                UserId = userId,
                Username = ProfileUsername,
            });
    }

    /// <summary>IUsersStore whose writes all throw — proves the profile-update mirror is
    /// genuinely best-effort.</summary>
    private sealed class ThrowingUsersStore : IUsersStore
    {
        private static Exception Boom() => new InvalidOperationException("users store unavailable");

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct) => throw Boom();
        public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct) => throw Boom();
        public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct) => throw Boom();
        public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct) => throw Boom();
        public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct) => throw Boom();
        public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct) => throw Boom();
        public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct) => throw Boom();
        public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct) => throw Boom();
        public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct) => throw Boom();
        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct) => throw Boom();
        public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct) => throw Boom();
        public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct) => throw Boom();
        public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct) => throw Boom();
        public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct) => throw Boom();
        public Task<bool> PurgePiiAsync(string userId, CancellationToken ct) => throw Boom();
    }
}
