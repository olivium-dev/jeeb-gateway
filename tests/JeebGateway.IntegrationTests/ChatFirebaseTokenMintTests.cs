using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Chat.Firebase;
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

    [Fact]
    public async Task Route_refuses_a_relative_key_path_because_it_would_resolve_into_the_app_directory()
    {
        using var factory = NewFactory("service-account.json");
        var client = Authenticated(factory, sub: "relative-path-user");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
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
        var client = Authenticated(factory, sub: "committed-key-user");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task Route_refuses_a_key_belonging_to_a_different_firebase_project()
    {
        // Cross-tenant guard: a key for another project must fail closed rather than
        // mint identities into someone else's Firebase.
        using var key = new TempServiceAccountKey("some-other-project");
        using var factory = NewFactory(key.Path);

        var client = Authenticated(factory, sub: "wrong-project-user");

        var response = await client.PostAsync(Route, content: null);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static WebApplicationFactory<Program> NewFactory(string keyPath)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("Firebase:Chat:ServiceAccountKeyPath", keyPath);
            builder.UseSetting("Firebase:Chat:ProjectId", ProjectId);
        });

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
