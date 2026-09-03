using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Chat.Firebase;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// POST /v1/chat/firebase-token — the identity hop from a Jeeb session to a Firebase
/// custom token, so the client can read its own thread straight from Firestore.
///
/// <para>The contract these tests pin:</para>
/// <list type="number">
///   <item>an authenticated caller gets a token whose decoded <c>uid</c> is THEIR
///         Jeeb user id — the same value chat-service stamps into
///         <c>Participants[].UserId</c>, which is the whole reason the Firestore
///         membership rule matches;</item>
///   <item>the uid follows the gateway's canonical resolver (<c>sid</c> before
///         <c>sub</c>), NOT the raw <c>sub</c> claim — a user-management-reissued
///         token carries <c>sub = e-mail</c> and the GUID in <c>sid</c>, so minting on
///         <c>sub</c> would deny every UM session and leak e-mails as Firebase uids;</item>
///   <item>an unauthenticated caller is rejected;</item>
///   <item>the route cannot operate on a credential committed to the repo.</item>
/// </list>
///
/// <para>No real service-account key is used or needed: each test generates a
/// throwaway RSA key and a synthetic key file in the OS temp directory.</para>
/// </summary>
public sealed class ChatFirebaseTokenMintTests
{
    private const string Route = "/v1/chat/firebase-token";
    private const string ProjectId = "jeeb-5a293";

    private const string FirebaseAudience =
        "https://identitytoolkit.googleapis.com/google.identity.identitytoolkit.v1.IdentityToolkit";

    // -------------------------------------------------------------------------
    // 1. The core contract: decoded uid == the caller's Jeeb user id.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Authenticated_caller_gets_a_custom_token_whose_uid_is_their_user_id()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path);

        const string userId = "11111111-2222-3333-4444-555555555555";
        var client = Authenticated(factory, sub: userId);

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        Assert.Equal(userId, body.GetProperty("uid").GetString());

        // Decode the minted token and assert the uid Firebase will actually see.
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(userId, decoded.Claims.Single(c => c.Type == "uid").Value);

        // Custom-token shape Firebase requires.
        Assert.Equal(FirebaseAudience, decoded.Audiences.Single());
        Assert.Equal(key.ClientEmail, decoded.Issuer);
        Assert.Equal(key.ClientEmail, decoded.Claims.Single(c => c.Type == "sub").Value);
        Assert.Equal("RS256", decoded.Header.Alg);

        // Firebase rejects custom tokens living longer than an hour.
        Assert.True(decoded.ValidTo <= DateTime.UtcNow.AddHours(1).AddMinutes(1));
        Assert.True(decoded.ValidTo > DateTime.UtcNow);
    }

    [Fact]
    public async Task Minted_token_verifies_against_the_service_accounts_public_key()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path);

        const string userId = "sig-check-user";
        var client = Authenticated(factory, sub: userId);

        var response = await client.PostAsync(Route, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;

        // A token Firebase cannot verify is a token the client can never exchange, so
        // prove the signature really is the service account's.
        var result = await new JwtSecurityTokenHandler().ValidateTokenAsync(token,
            new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = key.ClientEmail,
                ValidateAudience = true,
                ValidAudience = FirebaseAudience,
                ValidateLifetime = true,
                IssuerSigningKey = new RsaSecurityKey(key.PublicKey),
                ValidateIssuerSigningKey = true,
            });

        Assert.True(result.IsValid, result.Exception?.Message);
    }

    // -------------------------------------------------------------------------
    // 2. The uid must follow the canonical resolver, not the raw `sub`.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Uid_comes_from_sid_when_present_so_UM_reissued_sessions_still_match_participants()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path);

        // A user-management-reissued bearer: sub is the e-mail, the canonical GUID is sid.
        const string guid = "99999999-8888-7777-6666-555555555555";
        const string email = "someone@example.com";

        var client = Authenticated(factory, sub: email, sid: guid);

        var response = await client.PostAsync(Route, content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var token = body.GetProperty("token").GetString()!;
        var decoded = new JwtSecurityTokenHandler().ReadJwtToken(token);
        var uid = decoded.Claims.Single(c => c.Type == "uid").Value;

        // The GUID is what chat-service stores in Participants[].UserId, so it is the
        // only uid the Firestore rule can match.
        Assert.Equal(guid, uid);

        // And the e-mail must never become a Firebase uid.
        Assert.NotEqual(email, uid);
        Assert.DoesNotContain("@", uid);
    }

    // -------------------------------------------------------------------------
    // 3. No session, no token.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Unauthenticated_caller_is_rejected()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        // The gateway normalizes 401s to an RFC7807 envelope; what matters is that no
        // token escaped with it.
        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("token", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_with_a_bearer_signed_by_the_wrong_key_is_rejected()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path);

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        var config = factory.Services.GetRequiredService<IConfiguration>();
        var forged = new JwtSecurityToken(
            issuer: config["Jwt:Issuer"] ?? "jeeb-gateway",
            audience: config["Jwt:Audience"] ?? "jeeb-clients",
            claims: new[] { new Claim("sub", "attacker"), new Claim("roles", "client") },
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(
                    "an-entirely-different-signing-key-32bytes!!")),
                SecurityAlgorithms.HmacSha256));

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(forged));

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // 3b. The uid is CLAIM-derived. A request header can never become a Firebase
    //     identity, even where the trusted-edge header path is switched on.
    // -------------------------------------------------------------------------

    /// <summary>
    /// <c>UserIdentity.TryGetUserId</c>'s last arm falls back to the raw <c>X-User-Id</c>
    /// header whenever <c>EdgeIdentityTrust.HeadersTrusted</c> is true — which is
    /// UNCONDITIONAL in Development and Testing. For most endpoints that arm is a
    /// convenience. Here it would be privilege escalation: it would mint a FIREBASE
    /// IDENTITY for whoever the header names, and the Firestore membership rule would
    /// then faithfully admit the holder to that person's conversations.
    ///
    /// <para>This test runs in Development ON PURPOSE, so the header really is trusted.
    /// A pass therefore means the controller's claim-chain guard closed the path, not
    /// that the environment happened to have it closed already.</para>
    /// </summary>
    [Fact]
    public async Task Header_supplied_identity_alone_cannot_mint_even_where_edge_headers_are_trusted()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path, environment: "Development");

        // A VALID bearer that authenticates and carries the chat.read role, but no
        // subject claim of any kind — so the only identity on offer is the header.
        var client = AuthenticatedWithoutSubjectClaim(factory);
        client.DefaultRequestHeaders.Add("X-User-Id", "victim-user-id");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain("victim-user-id", body, StringComparison.OrdinalIgnoreCase);

        // POSITIVE CONTROL — same factory, same key, same route. If this did not mint,
        // the 401 above would prove nothing (an unconfigured key or an unregistered
        // route would produce it just as readily).
        var legitimate = Authenticated(factory, sub: "real-user-id");
        var ok = await legitimate.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    /// <summary>
    /// The discriminating case: claim AND header both present, and disagreeing. The
    /// minted uid must follow the claim, so a header can never redirect the identity.
    /// </summary>
    [Fact]
    public async Task Conflicting_header_cannot_redirect_the_minted_uid_away_from_the_claim()
    {
        using var key = new TempServiceAccountKey(ProjectId);
        using var factory = NewFactory(key.Path, environment: "Development");

        const string realUserId = "99999999-8888-7777-6666-555555555555";

        var client = Authenticated(factory, sub: realUserId);
        client.DefaultRequestHeaders.Add("X-User-Id", "victim-user-id");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(realUserId, body.GetProperty("uid").GetString());

        var decoded = new JwtSecurityTokenHandler()
            .ReadJwtToken(body.GetProperty("token").GetString()!);

        Assert.Equal(realUserId, decoded.Claims.Single(c => c.Type == "uid").Value);
    }

    // -------------------------------------------------------------------------
    // 4. Credential locality — a committed key must be unusable.
    // -------------------------------------------------------------------------

    [Fact]
    public async Task Route_is_unavailable_when_no_key_is_configured()
    {
        using var factory = NewFactory(keyPath: string.Empty);
        var client = Authenticated(factory, sub: "no-key-user");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task NonDevelopment_host_refuses_to_boot_when_key_path_is_empty(
        string environment)
    {
        using var host = new HostBuilder()
            .UseEnvironment(environment)
            .ConfigureServices(services =>
            {
                services.AddLogging();
                services.Configure<FirebaseCustomTokenOptions>(options =>
                {
                    options.ProjectId = ProjectId;
                    options.ServiceAccountKeyPath = string.Empty;
                });
                services.AddSingleton<FirebaseCustomTokenMinter>();
                services.AddHostedService<FirebaseCustomTokenStartupValidator>();
            })
            .Build();

        var error = await Assert.ThrowsAnyAsync<Exception>(() => host.StartAsync());

        Assert.Contains("ServiceAccountKeyPath is required", error.ToString(),
            StringComparison.Ordinal);
        Assert.Contains(environment, error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Development")]
    [InlineData("Testing")]
    public async Task Development_and_Testing_may_boot_without_a_key_but_mint_stays_unavailable(
        string environment)
    {
        using var factory = NewFactory(keyPath: string.Empty, environment);
        var client = Authenticated(factory, sub: "no-key-nonproduction-user");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Route_refuses_a_relative_key_path_because_it_would_resolve_into_the_app_directory()
    {
        using var factory = NewFactory("service-account.json");
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => Task.Run(() => factory.CreateClient()));

        Assert.Contains("absolute host path", error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_refuses_a_key_that_lives_inside_the_application_content_root()
    {
        // Simulates exactly the failure this guard exists to prevent: a service-account
        // key committed to the repo and published alongside the binaries.
        using var probe = NewFactory(keyPath: string.Empty);
        var contentRoot = probe.Services
            .GetRequiredService<IHostEnvironment>().ContentRootPath;

        var committed = Path.Combine(contentRoot, $"committed-key-{Guid.NewGuid():N}.json");
        using var key = new TempServiceAccountKey(ProjectId, committed);

        using var factory = NewFactory(key.Path);
        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => Task.Run(() => factory.CreateClient()));

        Assert.Contains("inside the application content root", error.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Route_refuses_a_key_belonging_to_a_different_firebase_project()
    {
        // Cross-tenant guard: a key for another project must fail closed rather than
        // mint identities into someone else's Firebase.
        using var key = new TempServiceAccountKey("some-other-project");
        using var factory = NewFactory(key.Path);

        var error = await Assert.ThrowsAnyAsync<Exception>(
            () => Task.Run(() => factory.CreateClient()));

        Assert.Contains("different Firebase project", error.ToString(),
            StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(
        string keyPath, string? environment = null)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            // The shared test factory supplies an ephemeral signer so unrelated
            // Production-mode tests can satisfy the real boot invariant. A single
            // space is an explicit whitespace-only override; ASP.NET host settings
            // discard an empty string before the later app-configuration layer.
            builder.UseSetting(
                "Firebase:Chat:ServiceAccountKeyPath",
                string.IsNullOrEmpty(keyPath) ? " " : keyPath);
            builder.UseSetting("Firebase:Chat:ProjectId", ProjectId);

            if (environment is not null)
            {
                // Pinned explicitly so the trusted-edge header path is provably OPEN in
                // the tests that assert the controller closes it anyway.
                builder.UseEnvironment(environment);
            }
        });

    /// <summary>
    /// A bearer that authenticates and carries the chat.read role but NO subject claim
    /// (<c>sid</c>/<c>sub</c>/nameidentifier). Models the only shape in which the
    /// <c>X-User-Id</c> fallback in <c>UserIdentity</c> is reachable on this route.
    /// </summary>
    private static HttpClient AuthenticatedWithoutSubjectClaim(
        WebApplicationFactory<Program> factory)
        => BearerClient(factory, new List<Claim> { new("roles", "client") });

    private static HttpClient BearerClient(
        WebApplicationFactory<Program> factory, List<Claim> claims)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", new JwtSecurityTokenHandler().WriteToken(token));

        return client;
    }

    /// <summary>
    /// Mints an HS256 bearer against the host's effective Jwt config — the same pattern
    /// ChatControllerErrorShapeTests uses. <paramref name="sid"/> models a
    /// user-management-reissued token (sub = e-mail, GUID in sid).
    /// </summary>
    private static HttpClient Authenticated(
        WebApplicationFactory<Program> factory, string sub, string? sid = null)
    {
        var config = factory.Services.GetRequiredService<IConfiguration>();
        var issuer = config["Jwt:Issuer"] ?? "jeeb-gateway";
        var audience = config["Jwt:Audience"] ?? "jeeb-clients";
        var signingKey = config["Jwt:SigningKey"] ?? "jeeb-gateway-itest-signing-key-32bytes!!";

        var claims = new List<Claim> { new("sub", sub), new("roles", "client") };
        if (sid is not null)
        {
            claims.Add(new Claim("sid", sid));
        }

        var token = new JwtSecurityToken(
            issuer: issuer,
            audience: audience,
            claims: claims,
            notBefore: DateTime.UtcNow.AddMinutes(-1),
            expires: DateTime.UtcNow.AddMinutes(30),
            signingCredentials: new SigningCredentials(
                new SymmetricSecurityKey(Encoding.UTF8.GetBytes(signingKey)),
                SecurityAlgorithms.HmacSha256));

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            HandleCookies = false,
        });
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        return client;
    }

    /// <summary>
    /// A throwaway RSA key written out in service-account JSON shape. Never a real
    /// credential — generated per test and deleted on dispose.
    /// </summary>
    private sealed class TempServiceAccountKey : IDisposable
    {
        private readonly RSA _rsa;

        public string Path { get; }
        public string ClientEmail { get; }
        public RSA PublicKey => _rsa;

        public TempServiceAccountKey(string projectId, string? path = null)
        {
            _rsa = RSA.Create(2048);
            ClientEmail = $"firebase-adminsdk-test@{projectId}.iam.gserviceaccount.com";
            Path = path ?? System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"jeeb-fbkey-{Guid.NewGuid():N}.json");

            var json = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["type"] = "service_account",
                ["project_id"] = projectId,
                ["private_key_id"] = Guid.NewGuid().ToString("N"),
                ["private_key"] = _rsa.ExportPkcs8PrivateKeyPem(),
                ["client_email"] = ClientEmail,
                ["token_uri"] = "https://oauth2.googleapis.com/token",
            });

            File.WriteAllText(Path, json);
        }

        public void Dispose()
        {
            try
            {
                if (File.Exists(Path))
                {
                    File.Delete(Path);
                }
            }
            catch (IOException)
            {
                // Best-effort cleanup of a temp file; never fail a test over it.
            }

            _rsa.Dispose();
        }
    }
}
