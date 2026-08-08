using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Cdn;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using UmClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UmProfileResponse = JeebGateway.service.ServiceUserManagement.UserProfileResponse;
using UmApiException = JeebGateway.service.ServiceUserManagement.ApiException;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// F5 — the PUBLIC, server-resolved avatar route (<c>AvatarController.GetAvatar</c>,
/// <c>GET /api/users/{userId}/avatar</c>). This route is the wave's biggest
/// security-posture decision (genuinely unauthenticated), so its coverage leans
/// heavily on the NEGATIVE case: proving it can never be used as a general-purpose
/// arbitrary-object read, unlike the bearer-gated
/// <see cref="JeebGateway.Controllers.CdnController.GetAssetContent"/> it borrows its
/// streaming mechanics from.
/// </summary>
public sealed class AvatarControllerTests
{
    private static readonly byte[] JpegBytes = { 0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46 };

    [Fact]
    public async Task GetAvatar_Happy_Returns_200_With_Bytes_And_Public_CacheControl()
    {
        var um = new StubUmClient { ProfilePic = "profile_avatar/abc123.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        resp.Content.Headers.ContentType!.ToString().Should().Be("image/jpeg");
        (await resp.Content.ReadAsByteArrayAsync()).Should().Equal(JpegBytes);
        resp.Headers.CacheControl!.Public.Should().BeTrue();
        resp.Headers.CacheControl.MaxAge.Should().NotBeNull();

        // The object ref reached cdn PERCENT-ENCODED into a single segment, resolved
        // ENTIRELY from the UM profile — never from anything the caller supplied.
        upstream.RequestUri!.AbsolutePath.Should().Be("/api/ImageUpload/fetch/profile_avatar%2Fabc123.jpg");
    }

    [Fact]
    public async Task GetAvatar_Requires_No_Authentication_Not_A_401()
    {
        // The load-bearing posture assertion: this route is deliberately public.
        // No Authorization header, no X-User-Id — must never 401.
        var um = new StubUmClient { ProfilePic = "profile_avatar/abc123.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetAvatar_NoAvatarSet_Returns_404_And_Never_Dials_Cdn()
    {
        var um = new StubUmClient { ProfilePic = null };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        upstream.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAvatar_UnknownUser_UmProfile404_Returns_404_Not_500()
    {
        var um = new StubUmClient { ThrowNotFound = true };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/nonexistent/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        upstream.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task GetAvatar_CdnObjectAged_Out_Upstream404_Returns_404()
    {
        // ProfilePic points at a real ref but cdn's own 90-day retention purged it —
        // must not read as a hard error.
        var um = new StubUmClient { ProfilePic = "profile_avatar/gone.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.NotFound, "application/json", Array.Empty<byte>());
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetAvatar_CdnUpstream500_Returns_502()
    {
        var um = new StubUmClient { ProfilePic = "profile_avatar/abc123.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.InternalServerError, "application/json", Array.Empty<byte>());
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.BadGateway);
    }

    [Fact]
    public async Task GetAvatar_FlagsOff_Returns_503_And_Never_Dials_Um_Or_Cdn()
    {
        var um = new StubUmClient { ProfilePic = "profile_avatar/abc123.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream, cdnFlagOn: false);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        um.ProfileCalls.Should().Be(0);
        upstream.WasCalled.Should().BeFalse();
    }

    // ----- the security-critical negative case -----

    [Theory]
    // A stored ref that (however it got there) carries a traversal token must never
    // be dialled — proves this route cannot be walked outside its own object even if
    // the upstream data were somehow malformed.
    [InlineData("../id_document_front/some-other-users-id.jpg")]
    [InlineData("id_document_front/%2e%2e/admin")]
    [InlineData("dispute_evidence\\..\\admin")]
    public async Task GetAvatar_MalformedStoredRef_Never_Dials_Cdn_Fails_Closed_404(string malformedRef)
    {
        var um = new StubUmClient { ProfilePic = malformedRef };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        upstream.WasCalled.Should().BeFalse(
            "a malformed/traversing stored ref must fail closed before the gateway dials cdn");
    }

    [Theory]
    // A CLEAN (non-traversing) cross-slot ref passes the traversal guard, so slot
    // confinement is the only thing stopping this public route from re-serving a
    // bearer-gated KYC/dispute/chat/proof asset whose ref lands in ProfilePic.
    [InlineData("id_document_front/known-guid.jpg")]
    [InlineData("dispute_evidence/known-guid.jpg")]
    [InlineData("chat_attachment/known-guid.jpg")]
    [InlineData("proof_of_delivery/known-guid.jpg")]
    public async Task GetAvatar_CrossSlotStoredRef_Never_Dials_Cdn_Fails_Closed_404(string crossSlotRef)
    {
        var um = new StubUmClient { ProfilePic = crossSlotRef };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync("/api/users/user-1/avatar");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        upstream.WasCalled.Should().BeFalse(
            "a non-avatar-slot stored ref must fail closed — this public route serves the profile_avatar slot only");
    }

    [Fact]
    public async Task GetAvatar_CannotBeRedirectedToAnotherUsersKycAsset_ByAnyClientSuppliedInput()
    {
        // The security invariant this whole route depends on: unlike GetAssetContent
        // (bearer-gated, accepts an arbitrary client-supplied objectPath), this route
        // accepts NO path-shaped input from the caller at all — extra query/segments
        // are either ignored or 404, never routed into the cdn fetch target. Prove a
        // query-string "objectPath"-style smuggle attempt has zero effect: the ref
        // actually fetched is STILL exactly what UM returned for THIS userId.
        var um = new StubUmClient { ProfilePic = "profile_avatar/legit-owner.jpg" };
        var upstream = new StubUpstreamHandler(HttpStatusCode.OK, "image/jpeg", JpegBytes);
        using var factory = AvatarFactory(um, upstream);
        var client = factory.CreateClient();

        var resp = await client.GetAsync(
            "/api/users/user-1/avatar?objectPath=id_document_front/victim.jpg&ref=dispute_evidence/x.jpg");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        upstream.RequestUri!.AbsolutePath.Should().Be("/api/ImageUpload/fetch/profile_avatar%2Flegit-owner.jpg",
            "the fetched ref must come ONLY from the server-side UM resolution, never a query param");
    }

    [Fact]
    public async Task GetAvatar_Is_Covered_By_PublicEndpoint_Not_A_Capability_Gap()
    {
        // ADR-005 default-deny: an unauthenticated route MUST carry an explicit
        // [PublicEndpoint] opt-out, never a silent omission of [RequireCapability].
        await using var factory = new WebApplicationFactory<Program>();
        using var _ = factory.CreateClient();

        var guard = factory.Services.GetRequiredService<CapabilityCoverageGuard>();
        var uncovered = guard.FindUncoveredActions();

        uncovered.Should().NotContain(n => n.Contains("GetAvatar"));
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> AvatarFactory(
        StubUmClient um, StubUpstreamHandler upstream, bool cdnFlagOn = true, bool umFlagOn = true) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Cdn", cdnFlagOn ? "true" : "false");
            builder.UseSetting("FeatureFlags:UseUpstream:UserManagement", umFlagOn ? "true" : "false");
            builder.UseSetting("Services:Cdn:BaseUrl", "http://cdn-test.internal:10072/");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<UmClient>();
                services.AddSingleton<UmClient>(um);

                services.AddHttpClient(CdnUploadUrlResolver.ProxyHttpClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => upstream);
            });
        });

    /// <summary>Stub over the generated UM client — configurable ProfilePic / 404 / error.</summary>
    private sealed class StubUmClient : UmClient
    {
        public StubUmClient() : base("http://localhost", new HttpClient()) { }

        public string? ProfilePic { get; init; }
        public bool ThrowNotFound { get; init; }
        public int ProfileCalls { get; private set; }

        public override Task<UmProfileResponse> ProfileAsync(string userId, CancellationToken ct)
        {
            ProfileCalls++;
            if (ThrowNotFound)
            {
                throw new UmApiException("Not Found", 404, "{}",
                    new Dictionary<string, IEnumerable<string>>(), null);
            }
            return Task.FromResult(new UmProfileResponse { UserId = userId, ProfilePic = ProfilePic });
        }
    }

    /// <summary>Stands in for cdn-service on the <c>cdn-proxy</c> client.</summary>
    private sealed class StubUpstreamHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _contentType;
        private readonly byte[] _body;

        public StubUpstreamHandler(HttpStatusCode status, string contentType, byte[] body)
        {
            _status = status;
            _contentType = contentType;
            _body = body;
        }

        public bool WasCalled { get; private set; }
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            WasCalled = true;
            RequestUri = request.RequestUri;

            var content = new ByteArrayContent(_body);
            content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(_contentType);
            return Task.FromResult(new HttpResponseMessage(_status) { Content = content });
        }
    }
}
