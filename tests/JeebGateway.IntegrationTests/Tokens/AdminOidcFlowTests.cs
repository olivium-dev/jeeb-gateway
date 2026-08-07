using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Auth.Oidc;
using JeebGateway.Controllers;
using JeebGateway.Infrastructure;
using JeebGateway.Security;
using JeebGateway.Tokens;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ApplicationParts;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace JeebGateway.IntegrationTests.Tokens;

public sealed class AdminOidcFlowTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-08-07T12:00:00Z");

    [Fact]
    public void MissingOrUnsafeConfigurationFailsClosed()
    {
        var disabled = Configuration(new Dictionary<string, string?>());
        AdminOidcOptions.TryLoad(disabled, out _, out var disabledError).Should().BeFalse();
        disabledError.Should().Contain("disabled");

        var values = ValidConfiguration();
        values["AdminOidc:AuthorizationEndpoint"] = "http://identity.example.test/authorize";
        AdminOidcOptions.TryLoad(Configuration(values), out _, out var unsafeError).Should().BeFalse();
        unsafeError.Should().Contain("HTTPS");
    }

    [Theory]
    [InlineData("https://localhost/authorize")]
    [InlineData("https://127.0.0.1/authorize")]
    [InlineData("https://10.1.2.3/authorize")]
    [InlineData("https://169.254.169.254/authorize")]
    [InlineData("https://[::1]/authorize")]
    [InlineData("https://[fe80::1]/authorize")]
    [InlineData("https://[::192.168.1.1]/authorize")]
    [InlineData("https://[::ffff:0:192.168.1.1]/authorize")]
    [InlineData("https://[64:ff9b::c0a8:101]/authorize")]
    [InlineData("https://[64:ff9b:1::c0a8:101]/authorize")]
    [InlineData("https://[2002:c0a8:101::]/authorize")]
    [InlineData("https://[2001:0000:4136:e378:8000:63bf:3fff:fdd2]/authorize")]
    public void ProviderEndpointsRejectLocalPrivateAndLinkLocalOrigins(string endpoint)
    {
        var values = ValidConfiguration();
        var origin = new Uri(endpoint).GetLeftPart(UriPartial.Authority);
        values["AdminOidc:Issuer"] = origin;
        values["AdminOidc:AuthorizationEndpoint"] = origin + "/authorize";
        values["AdminOidc:TokenEndpoint"] = origin + "/token";
        values["AdminOidc:JwksUri"] = origin + "/jwks";

        AdminOidcOptions.TryLoad(Configuration(values), out _, out _).Should().BeFalse();
    }

    [Fact]
    public void ProviderEndpointsMustStayOnIssuerOriginAndTransportNeverRedirects()
    {
        var values = ValidConfiguration();
        values["AdminOidc:TokenEndpoint"] = "https://token.identity.example.test/token";
        AdminOidcOptions.TryLoad(Configuration(values), out _, out var error).Should().BeFalse();
        error.Should().Contain("exact Issuer origin");

        using var handler = AdminOidcHttpTransport.CreateHandler();
        handler.AllowAutoRedirect.Should().BeFalse();
        handler.UseProxy.Should().BeFalse();
        handler.UseCookies.Should().BeFalse();
    }

    [Fact]
    public async Task ProductionStartupEnforcesOnlyWhenEnabledAndFailsClosedWhenIncomplete()
    {
        var production = new TestingEnvironment { EnvironmentName = "Production" };

        // Extraction adaptation: a disabled AdminOidc section must keep the live
        // mobile BFF booting with zero new config instead of failing closed.
        var disabled = Configuration(new Dictionary<string, string?>());
        AdminOidcStartupGuard.EnsureConfigured(disabled, production);
        var disabledHealth = await new AdminOidcConfigurationHealthCheck(disabled, production)
            .CheckHealthAsync(new HealthCheckContext());
        disabledHealth.Status.Should().Be(HealthStatus.Healthy);

        var incomplete = Configuration(new Dictionary<string, string?>
        {
            ["AdminOidc:Enabled"] = "true",
        });
        Action start = () => AdminOidcStartupGuard.EnsureConfigured(incomplete, production);
        start.Should().Throw<InvalidOperationException>().WithMessage("*not ready*");

        var unhealthy = await new AdminOidcConfigurationHealthCheck(incomplete, production)
            .CheckHealthAsync(new HealthCheckContext());
        unhealthy.Status.Should().Be(HealthStatus.Unhealthy);

        var valid = Configuration(ValidConfiguration());
        AdminOidcStartupGuard.EnsureConfigured(valid, production);
        var healthy = await new AdminOidcConfigurationHealthCheck(valid, production)
            .CheckHealthAsync(new HealthCheckContext());
        healthy.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public void RoleMappingRejectsPrivilegeCombinationAndUnknownGroups()
    {
        AdminOidcOptions.TryLoad(Configuration(ValidConfiguration()), out var options, out _).Should().BeTrue();

        AdminOidcRoleMapper.Map(options, new[] { "finance-approvers" })
            .Should().Equal(Roles.FinanceApprover);
        AdminOidcRoleMapper.Map(options, new[] { "finance-approvers", "support-leads" })
            .Should().BeEmpty("finance write cannot be combined with support write");
        AdminOidcRoleMapper.Map(options, new[] { "untrusted-admin" }).Should().BeEmpty();
    }

    [Fact]
    public void CorrelationIsConfidentialTamperEvidentAndReturnPathsAreLocal()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var value = new AdminOidcCorrelation("state", "nonce", "verifier", "/settlements/batches/batch-1",
            Now.ToUnixTimeSeconds(), "old-refresh");
        var protectedValue = AdminOidcCorrelationProtector.Protect(value, key);

        protectedValue.Should().NotContain("verifier").And.NotContain("old-refresh");
        AdminOidcCorrelationProtector.TryUnprotect(protectedValue, key, out var roundTrip).Should().BeTrue();
        roundTrip.Should().Be(value);
        const int tamperIndex = 10;
        var tampered = protectedValue[..tamperIndex]
                       + (protectedValue[tamperIndex] == 'A' ? 'B' : 'A')
                       + protectedValue[(tamperIndex + 1)..];
        AdminOidcCorrelationProtector.TryUnprotect(tampered, key, out _).Should().BeFalse();

        AdminOidcAuthController.SafeReturnPath("/settlements/batches/batch-1")
            .Should().Be("/settlements/batches/batch-1");
        AdminOidcAuthController.SafeReturnPath("//evil.example").Should().Be("/");
        AdminOidcAuthController.SafeReturnPath("/%5cevil.example").Should().Be("/");
        AdminOidcAuthController.SafeReturnPath("/admin").Should().Be("/");
    }

    [Fact]
    public async Task CodePkceCallbackAcceptsOnlySignedFreshMfaAndMappedGroups()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "oidc-key-1" };
        var handler = new ProviderHandler(key, Now);
        var tokens = new CapturingTokenService();
        var controller = Controller(handler, tokens);

        var start = controller.Start("/settlements/batches/batch-1")
            .Should().BeOfType<RedirectResult>().Subject;
        var authorization = new Uri(start.Url!);
        var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorization.Query);
        query["code_challenge_method"].ToString().Should().Be("S256");
        query["prompt"].ToString().Should().Be("login");
        query["max_age"].ToString().Should().Be("0");
        query["nonce"].ToString().Should().NotBeNullOrWhiteSpace();
        query["state"].ToString().Should().NotBeNullOrWhiteSpace();
        handler.NonceFromCorrelation = query["nonce"].ToString();

        var correlationCookie = CookieValue(controller.Response.Headers.SetCookie, AdminSessionCookies.OidcCorrelationCookie);
        var callbackController = Controller(handler, tokens);
        callbackController.HttpContext.Request.Headers.Cookie =
            $"{AdminSessionCookies.OidcCorrelationCookie}={correlationCookie}";
        var callback = await callbackController.Callback(
            "authorization-code", query["state"].ToString(), null, CancellationToken.None);

        callback.Should().BeOfType<LocalRedirectResult>().Which.Url
            .Should().Be("/settlements/batches/batch-1");
        tokens.IssuedRoles.Should().Equal(Roles.FinanceApprover);
        tokens.Authentication.Should().NotBeNull();
        tokens.Authentication!.Methods.Should().Contain("mfa");
        tokens.Authentication.Provider.Should().Be("https://identity.example.test");
        tokens.Authentication.PersistRoleContext.Should().BeTrue();
        handler.TokenRequestBody.Should().Contain("code_verifier=").And.Contain("client_secret=oidc-secret");
        callbackController.Response.Headers.SetCookie.ToString().Should()
            .Contain(AdminSessionCookies.RefreshCookie);
        callbackController.Response.Headers.SetCookie.ToString().ToLowerInvariant().Should().Contain("httponly");
    }

    [Fact]
    public async Task InvalidCallbackClearsOnlyCorrelationAndPreservesExistingSession()
    {
        using var rsa = RSA.Create(2048);
        var controller = Controller(
            new ProviderHandler(new RsaSecurityKey(rsa) { KeyId = "unused" }, Now),
            new CapturingTokenService());
        controller.HttpContext.Request.Headers.Cookie =
            $"{AdminSessionCookies.RefreshCookie}=existing-session";

        var result = await controller.Callback("code", "state", null, CancellationToken.None);

        result.Should().BeOfType<ObjectResult>().Which.StatusCode.Should().Be(401);
        var setCookies = controller.Response.Headers.SetCookie.ToString();
        setCookies.Should().Contain(AdminSessionCookies.OidcCorrelationCookie);
        setCookies.Should().NotContain(AdminSessionCookies.RefreshCookie + "=");
        setCookies.Should().NotContain(AdminSessionCookies.CsrfCookie + "=");
    }

    [Fact]
    public void TokenValidationRejectsForgedStaleOrPasswordOnlyTokens()
    {
        using var rsa = RSA.Create(2048);
        using var forgedRsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "valid" };
        var forgedKey = new RsaSecurityKey(forgedRsa) { KeyId = "forged" };
        AdminOidcOptions.TryLoad(Configuration(ValidConfiguration()), out var options, out _).Should().BeTrue();
        var jwks = Jwks(key);

        var forged = Mint(forgedKey, Now, Now.ToUnixTimeSeconds(), ["pwd", "mfa"], ["finance-approvers"], "nonce");
        var stale = Mint(key, Now, Now.AddMinutes(-6).ToUnixTimeSeconds(), ["pwd", "mfa"], ["finance-approvers"], "nonce");
        var passwordOnly = Mint(key, Now, Now.ToUnixTimeSeconds(), ["pwd"], ["finance-approvers"], "nonce");

        Action validateForged = () => AdminOidcTokenValidator.Validate(forged, jwks, "nonce", options, Now);
        Action validateStale = () => AdminOidcTokenValidator.Validate(stale, jwks, "nonce", options, Now);
        Action validatePassword = () => AdminOidcTokenValidator.Validate(passwordOnly, jwks, "nonce", options, Now);
        validateForged.Should().Throw<SecurityTokenException>();
        validateStale.Should().Throw<SecurityTokenException>();
        validatePassword.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void TokenValidationRejectsWrongAzpEvenWithOneValidAudience()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "valid" };
        AdminOidcOptions.TryLoad(Configuration(ValidConfiguration()), out var options, out _)
            .Should().BeTrue();
        var token = Mint(
            key, Now, Now.ToUnixTimeSeconds(), ["mfa"], ["finance-approvers"], "nonce",
            azp: "different-client");

        Action validate = () => AdminOidcTokenValidator.Validate(
            token, Jwks(key), "nonce", options, Now);
        validate.Should().Throw<SecurityTokenException>().WithMessage("*authorized party*");
    }

    [Theory]
    [InlineData("webauthn")]
    [InlineData("fido")]
    [InlineData("hwk")]
    [InlineData("pwd", "otp")]
    [InlineData("pwd", "pwd")]
    [InlineData("webauthn", "hwk")]
    public void TokenValidationNeverInfersMfaFromFactorNamesCountsOrDuplicates(
        params string[] methods)
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "valid" };
        AdminOidcOptions.TryLoad(Configuration(ValidConfiguration()), out var options, out _).Should().BeTrue();
        var token = Mint(key, Now, Now.ToUnixTimeSeconds(), methods,
            ["finance-approvers"], "nonce");

        Action validate = () => AdminOidcTokenValidator.Validate(
            token, Jwks(key), "nonce", options, Now);

        validate.Should().Throw<SecurityTokenException>();
    }

    [Fact]
    public void ExactConfiguredAcrCanProveAssuranceWithoutNormalizingFactorNames()
    {
        using var rsa = RSA.Create(2048);
        var key = new RsaSecurityKey(rsa) { KeyId = "valid" };
        var configuration = ValidConfiguration();
        configuration["AdminOidc:RequiredAcrValues"] = "urn:jeeb:loa:phishing-resistant";
        AdminOidcOptions.TryLoad(Configuration(configuration), out var options, out _).Should().BeTrue();
        var accepted = Mint(key, Now, Now.ToUnixTimeSeconds(), ["webauthn"],
            ["finance-approvers"], "nonce", "urn:jeeb:loa:phishing-resistant");
        var rejected = Mint(key, Now, Now.ToUnixTimeSeconds(), ["webauthn"],
            ["finance-approvers"], "nonce", "urn:other:loa");

        AdminOidcTokenValidator.Validate(accepted, Jwks(key), "nonce", options, Now)
            .Authentication.Methods.Should().Contain("mfa");
        Action validateRejected = () => AdminOidcTokenValidator.Validate(
            rejected, Jwks(key), "nonce", options, Now);
        validateRejected.Should().Throw<SecurityTokenException>();
    }

    private static AdminOidcAuthController Controller(ProviderHandler handler, ITokenService tokens)
    {
        var controller = new AdminOidcAuthController(
            Configuration(ValidConfiguration()),
            new TestingEnvironment(),
            new SingleClientFactory(new HttpClient(handler)),
            tokens,
            new FixedTimeProvider(Now),
            NullLogger<AdminOidcAuthController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };
        controller.HttpContext.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        return controller;
    }

    private static Dictionary<string, string?> ValidConfiguration() => new()
    {
        ["AdminOidc:Enabled"] = "true",
        ["AdminOidc:Issuer"] = "https://identity.example.test",
        ["AdminOidc:AuthorizationEndpoint"] = "https://identity.example.test/authorize",
        ["AdminOidc:TokenEndpoint"] = "https://identity.example.test/token",
        ["AdminOidc:JwksUri"] = "https://identity.example.test/jwks",
        ["AdminOidc:ClientId"] = "jeeb-admin",
        ["AdminOidc:ClientSecret"] = "oidc-secret",
        ["AdminOidc:RedirectUri"] = "https://admin.jeeb.example/gateway/admin/v1/auth/oidc/callback",
        ["AdminOidc:StateProtectionKey"] = Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()),
        ["AdminOidc:RoleMappings:finance_approver:0"] = "finance-approvers",
        ["AdminOidc:RoleMappings:support_lead:0"] = "support-leads",
        ["AdminOidc:RoleMappings:operations:0"] = "operations",
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    private static string CookieValue(Microsoft.Extensions.Primitives.StringValues headers, string name)
    {
        var prefix = name + "=";
        var header = headers.First(value => value!.StartsWith(prefix, StringComparison.Ordinal));
        return header![prefix.Length..].Split(';')[0];
    }

    private static string Mint(
        SecurityKey key,
        DateTimeOffset now,
        long authTime,
        string[] methods,
        string[] groups,
        string nonce,
        string? acr = null,
        string? azp = null)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, "external-operator-1"),
            new("nonce", nonce),
            new("auth_time", authTime.ToString(), ClaimValueTypes.Integer64),
            new("name", "Finance Operator"),
            new("email", "finance@example.test"),
            new("email_verified", "true"),
        };
        if (!string.IsNullOrWhiteSpace(acr)) claims.Add(new Claim("acr", acr));
        if (!string.IsNullOrWhiteSpace(azp)) claims.Add(new Claim("azp", azp));
        claims.AddRange(methods.Select(method => new Claim("amr", method)));
        claims.AddRange(groups.Select(group => new Claim("groups", group)));
        var token = new JwtSecurityToken(
            "https://identity.example.test",
            "jeeb-admin",
            claims,
            now.AddMinutes(-1).UtcDateTime,
            now.AddMinutes(5).UtcDateTime,
            new SigningCredentials(key, SecurityAlgorithms.RsaSha256));
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static string Jwks(RsaSecurityKey key)
    {
        var parameters = key.Rsa!.ExportParameters(false);
        return JsonSerializer.Serialize(new
        {
            keys = new[]
            {
                new
                {
                    kty = "RSA", use = "sig", kid = key.KeyId, alg = "RS256",
                    n = Base64UrlEncoder.Encode(parameters.Modulus),
                    e = Base64UrlEncoder.Encode(parameters.Exponent),
                },
            },
        });
    }

    private sealed class ProviderHandler(RsaSecurityKey key, DateTimeOffset now) : HttpMessageHandler
    {
        public string? TokenRequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/token")
            {
                TokenRequestBody = await request.Content!.ReadAsStringAsync(cancellationToken);
                var token = Mint(key, now, now.ToUnixTimeSeconds(), ["pwd", "mfa"],
                    ["finance-approvers"], NonceFromCorrelation!);
                return Json(new { id_token = token });
            }
            if (request.RequestUri.AbsolutePath == "/jwks") return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(Jwks(key), Encoding.UTF8, "application/json"),
            };
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // The start nonce is copied into the provider token by the test immediately
        // before callback; this emulates the provider echoing the authorize nonce.
        public string? NonceFromCorrelation { get; set; }

        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(value),
        };
    }

    private sealed class CapturingTokenService : ITokenService
    {
        public IReadOnlyList<string> IssuedRoles { get; private set; } = [];
        public VerifiedAuthenticationContext? Authentication { get; private set; }

        public Task<TokenPair> IssueAsync(string userId, IEnumerable<string> roles, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<TokenPair> IssueAsync(
            string userId,
            IEnumerable<string> roles,
            string activeRole,
            VerifiedAuthenticationContext? authentication,
            CancellationToken ct)
        {
            IssuedRoles = roles.ToArray();
            Authentication = authentication;
            return Task.FromResult(new TokenPair
            {
                AccessToken = "access",
                RefreshToken = "refresh",
                AccessTokenExpiresAt = Now.AddMinutes(15),
                RefreshTokenExpiresAt = Now.AddHours(8),
            });
        }

        public Task<RefreshResult> RefreshAsync(string refreshToken, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task RevokeAsync(string refreshToken, RevocationReason reason, CancellationToken ct) => Task.CompletedTask;
        public Task<int> RevokeAllForUserAsync(string userId, RevocationReason reason, CancellationToken ct) => Task.FromResult(0);
    }

    private sealed class SingleClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class TestingEnvironment : IWebHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Testing";
        public string ApplicationName { get; set; } = "JeebGateway.Tests";
        public string WebRootPath { get; set; } = string.Empty;
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
