using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Idempotency;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace JeebGateway.IntegrationTests.Cdn;

/// <summary>
/// S03 H2/H3 (DEC1) — the CDN signed-PUT broker endpoint POST /api/cdn/assets.
/// Pins the snake_case contract {upload_url, object_ref, expires_in≤300}, the
/// slot/content-type validation, the auth gate, and the flag-off 503 kill switch.
///
/// The broker requires FeatureFlags:UseUpstream:Cdn ON; these tests stand up a
/// stub <see cref="ICDNServiceClient"/> so they are CI-safe without a live
/// cdn-service, exactly like CdnServiceClientContractTests does for the client.
/// </summary>
public sealed class CdnUploadBrokerEndpointTests
{
    [Fact]
    public async Task BrokerUploadUrl_Happy_Returns_200_With_Snake_Case_Ticket_And_Bounded_Ttl()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "s03-cdn-happy");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("upload_url").GetString().Should().StartWith("https://");
        json.GetProperty("object_ref").GetString().Should().NotBeNullOrWhiteSpace();
        // BR-2: expires_in must be ≤ 300.
        json.GetProperty("expires_in").GetInt32().Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task BrokerUploadUrl_Clamps_Upstream_Ttl_Above_300_To_300()
    {
        using var factory = CdnEnabledFactory(new StubCdn { ExpiresInSeconds = 999 });
        var client = ClientFor(factory, "s03-cdn-clamp");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "selfie_with_liveness",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("expires_in").GetInt32().Should().Be(300);
    }

    [Fact]
    public async Task BrokerUploadUrl_Relative_UploadUrl_Is_Absolutized_To_Gateway_Proxy_Preserving_Query()
    {
        // JEBV4-259 — the actual production bug: cdn's Local provider mints a
        // relative, host-less signed-PUT URL. The broker must rewrite it to the
        // absolute gateway streaming-proxy route (query preserved), not leak it raw.
        var stub = new StubCdn
        {
            UploadUrlOverride = "/api/ImageUpload/put-signed/OBJ123?exp=1720000000&ct=image/jpeg&sig=abc",
        };
        using var factory = CdnEnabledFactory(stub);
        var client = ClientFor(factory, "s03-cdn-relative");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        var uploadUrl = json.GetProperty("upload_url").GetString();
        uploadUrl.Should().StartWith("http://localhost/api/cdn/put-signed/OBJ123");
        uploadUrl.Should().Contain("exp=1720000000");
        uploadUrl.Should().Contain("sig=abc");
    }

    [Fact]
    public async Task BrokerUploadUrl_Returns_Method_And_RequiredHeaders_From_Upstream()
    {
        // JEBV4-259 — method + requiredHeaders were previously DROPPED. Relay them.
        var stub = new StubCdn
        {
            TicketMethod = "PUT",
            TicketRequiredHeaders = new Dictionary<string, string>
            {
                ["Content-Type"] = "image/png",
                ["x-amz-acl"] = "private",
            },
        };
        using var factory = CdnEnabledFactory(stub);
        var client = ClientFor(factory, "s03-cdn-headers");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/png",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("method").GetString().Should().Be("PUT");
        var headers = json.GetProperty("required_headers");
        headers.GetProperty("Content-Type").GetString().Should().Be("image/png");
        headers.GetProperty("x-amz-acl").GetString().Should().Be("private");
    }

    [Fact]
    public async Task BrokerUploadUrl_Guarantees_ContentType_When_Upstream_Omits_RequiredHeaders()
    {
        // JEBV4-259 — even if cdn returns no requiredHeaders, the broker guarantees
        // Content-Type (from the requested content_type) so the mobile client's
        // dedicated interceptor-free Dio sends the right media type — never the
        // shared-Dio application/json default that corrupted the body.
        var stub = new StubCdn(); // empty requiredHeaders
        using var factory = CdnEnabledFactory(stub);
        var client = ClientFor(factory, "s03-cdn-ct-default");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "selfie_with_liveness",
            content_type = "image/webp",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("method").GetString().Should().Be("PUT");
        json.GetProperty("required_headers").GetProperty("Content-Type").GetString()
            .Should().Be("image/webp");
    }

    [Fact]
    public async Task BrokerUploadUrl_Proof_Of_Delivery_Slot_Returns_200()
    {
        // JEBV4-200 — companion to jeeb-mobile PR #117: the proof-photo slot must
        // be accepted by the signed-PUT broker like the existing KYC slots.
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "s03-cdn-pod");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "proof_of_delivery",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        json.GetProperty("upload_url").GetString().Should().StartWith("https://");
    }

    [Theory]
    [InlineData("dispute_evidence")]
    [InlineData("support_attachment")]
    public async Task BrokerUploadUrl_Case_Attachment_Slots_Use_Existing_Signed_Upload_Path(string slot)
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, $"case-cdn-{slot}", "customer");

        var response = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot,
            content_type = "image/jpeg",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await ReadJsonAsync(response);
        body.GetProperty("object_ref").GetString().Should().Contain(slot);
        body.GetProperty("method").GetString().Should().Be("PUT");
        body.GetProperty("expires_in").GetInt32().Should().BeLessThanOrEqualTo(300);
    }

    [Fact]
    public async Task BrokerUploadUrl_AudioMp4_Is_Accepted_Only_For_Dispute_Evidence()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "case-cdn-voice", "customer");

        var dispute = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "dispute_evidence",
            content_type = "audio/mp4",
        });
        var support = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "support_attachment",
            content_type = "audio/mp4",
        });

        dispute.StatusCode.Should().Be(HttpStatusCode.OK);
        support.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BrokerUploadUrl_Requires_Idempotency_Key()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "cdn-no-key");
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");

        var response = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "dispute_evidence",
            content_type = "image/jpeg",
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BrokerUploadUrl_Replay_Uses_Distributed_Idempotency_Response()
    {
        var cdn = new StubCdn();
        using var factory = CdnEnabledIdempotentFactory(cdn);
        var client = ClientFor(factory, "cdn-retry");

        var first = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "dispute_evidence", content_type = "image/jpeg",
        });
        var replay = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "dispute_evidence", content_type = "image/jpeg",
        });

        first.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        replay.Headers.Contains("Idempotency-Replayed").Should().BeTrue();
        (await ReadJsonAsync(replay)).GetProperty("object_ref").GetString()
            .Should().Be((await ReadJsonAsync(first)).GetProperty("object_ref").GetString());
        cdn.MintCalls.Should().Be(1);
    }

    [Fact]
    public async Task BrokerUploadUrl_Reused_Key_With_Different_Request_Is_409_Without_Another_Mint()
    {
        var cdn = new StubCdn();
        using var factory = CdnEnabledIdempotentFactory(cdn);
        var client = ClientFor(factory, "cdn-collision");

        (await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "dispute_evidence", content_type = "image/jpeg",
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        var collision = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "support_attachment", content_type = "image/jpeg",
        });

        collision.StatusCode.Should().Be(HttpStatusCode.Conflict);
        cdn.MintCalls.Should().Be(1);
    }

    [Fact]
    public async Task BrokerUploadUrl_Scopes_The_Same_Raw_Key_By_User()
    {
        var cdn = new StubCdn();
        using var factory = CdnEnabledIdempotentFactory(cdn);
        var first = ClientFor(factory, "cdn-user-1");
        var second = ClientFor(factory, "cdn-user-2");
        first.DefaultRequestHeaders.Remove("Idempotency-Key");
        second.DefaultRequestHeaders.Remove("Idempotency-Key");
        first.DefaultRequestHeaders.Add("Idempotency-Key", "shared-mobile-key");
        second.DefaultRequestHeaders.Add("Idempotency-Key", "shared-mobile-key");

        (await first.PostAsJsonAsync("/api/cdn/assets", new
        { slot = "dispute_evidence", content_type = "image/jpeg" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);
        (await second.PostAsJsonAsync("/api/cdn/assets", new
        { slot = "dispute_evidence", content_type = "image/jpeg" }))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        cdn.MintCalls.Should().Be(2);
    }

    [Fact]
    public async Task BrokerUploadUrl_Reservation_And_Result_Ttls_Do_Not_Outlive_Ticket()
    {
        var store = new RecordingIdempotencyStore();
        using var factory = CdnEnabledIdempotentFactory(new StubCdn(), store);
        var response = await ClientFor(factory, "cdn-ttl").PostAsJsonAsync(
            "/api/cdn/assets", new
            {
                slot = "dispute_evidence", content_type = "image/jpeg", ttl_seconds = 60,
            });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        store.Puts.Should().HaveCount(2);
        store.Puts.Should().OnlyContain(item => item.TtlSeconds <= 60);
        store.Puts.Should().Contain(item => item.StatusCode == 202);
        store.Puts.Should().Contain(item => item.StatusCode == 200);
    }

    [Fact]
    public async Task BrokerUploadUrl_Does_Not_Cache_An_Upstream_Invalid_Ticket_As_Success()
    {
        var cdn = new StubCdn { ExpiresInSeconds = 30 };
        var store = new RecordingIdempotencyStore();
        using var factory = CdnEnabledIdempotentFactory(cdn, store);
        var response = await ClientFor(factory, "cdn-invalid").PostAsJsonAsync(
            "/api/cdn/assets", new
            {
                slot = "dispute_evidence", content_type = "image/jpeg", ttl_seconds = 60,
            });

        response.StatusCode.Should().Be(HttpStatusCode.BadGateway);
        store.Puts.Should().ContainSingle().Which.StatusCode.Should().Be(202);
        cdn.MintCalls.Should().Be(1);
    }

    [Fact]
    public async Task BrokerUploadUrl_Rejects_Process_Local_Idempotency_Fallback()
    {
        var cdn = new StubCdn();
        using var factory = CdnEnabledIdempotentFactory(
            cdn, new InMemoryIdempotencyStore(TimeProvider.System));

        var response = await ClientFor(factory, "cdn-no-external-store").PostAsJsonAsync(
            "/api/cdn/assets", new
            {
                slot = "dispute_evidence", content_type = "image/jpeg",
            });

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        cdn.MintCalls.Should().Be(0);
    }

    [Fact]
    public async Task BrokerUploadUrl_Chat_Attachment_Slot_Returns_200_For_A_Client()
    {
        // TC-A1 (P4/P5, b01-20260725) — the in-chat image attachment slot must be
        // accepted by the signed-PUT broker exactly like the KYC/POD slots. Fails on
        // c872d63 with 400 "Invalid upload slot" — that 400 was the whole reason a
        // chat attachment could never reach the CDN.
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "p45-cdn-chat-client", "customer");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "chat_attachment",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await ReadJsonAsync(resp);
        // ABSOLUTE upload target (JEBV4-259): the client must never be handed a
        // relative/internal URL it cannot reach.
        json.GetProperty("upload_url").GetString().Should().StartWith("http");
        json.GetProperty("object_ref").GetString().Should().NotBeNullOrWhiteSpace();
        // BR-2: expires_in must be ≤ 300.
        json.GetProperty("expires_in").GetInt32().Should().BeLessThanOrEqualTo(300);
        // The client's dedicated interceptor-free Dio needs the media type back.
        json.GetProperty("required_headers").TryGetProperty("Content-Type", out var ct).Should().BeTrue();
        ct.GetString().Should().Be("image/jpeg");
    }

    [Fact]
    public async Task BrokerUploadUrl_Chat_Attachment_Slot_Returns_200_For_A_Jeeber()
    {
        // TC-A2 — CdnBroker is granted to Participant = {client, jeeber}
        // (CapabilityRolePolicy). BOTH chat parties must be able to attach, so the
        // jeeber (opaque role "driver") must get the same 200.
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "p45-cdn-chat-jeeber", "driver");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "chat_attachment",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task BrokerUploadUrl_Allowlist_Is_Still_Closed_And_Enumerates_Chat_Attachment()
    {
        // TC-A3 — the allowlist did NOT become "anything": a neighbouring, plausible
        // slot string is still rejected, and the ProblemDetails names the closed set
        // (which now includes chat_attachment) so a client can self-diagnose.
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "p45-cdn-chat-badslot", "customer");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "chat_video",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().Contain("chat_attachment");
        body.Should().Contain("proof_of_delivery", "the pre-existing slots must still be advertised");
    }

    [Fact]
    public async Task BrokerUploadUrl_Chat_Attachment_Without_Identity_Returns_401()
    {
        // TC-A4 — adding the new slot must not have loosened the auth gate.
        using var factory = CdnEnabledFactory(new StubCdn());
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "chat_attachment",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BrokerUploadUrl_Unknown_Slot_Returns_400()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var client = ClientFor(factory, "s03-cdn-badslot");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "not_a_real_slot",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task BrokerUploadUrl_Without_Identity_Returns_401()
    {
        using var factory = CdnEnabledFactory(new StubCdn());
        var anon = factory.CreateClient();

        var resp = await anon.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task BrokerUploadUrl_Flag_Off_Returns_503_KillSwitch()
    {
        // Default factory — Cdn flag is off in the test (appsettings.json) env.
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "s03-cdn-off");

        var resp = await client.PostAsJsonAsync("/api/cdn/assets", new
        {
            slot = "id_document_front",
            content_type = "image/jpeg",
        });

        resp.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }

    // ----- helpers -----

    private static WebApplicationFactory<Program> CdnEnabledFactory(ICDNServiceClient stub) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Cdn", "true");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICDNServiceClient>();
                services.AddSingleton(stub);
                services.RemoveAll<IIdempotencyStore>();
                services.AddSingleton<IIdempotencyStore>(new RecordingIdempotencyStore());
            });
        });

    private static WebApplicationFactory<Program> CdnEnabledIdempotentFactory(
        ICDNServiceClient stub, IIdempotencyStore? store = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("FeatureFlags:UseUpstream:Cdn", "true");
            builder.UseSetting("JeebStateService:BaseUrl", "http://state.test/");
            builder.ConfigureServices(services =>
            {
                services.RemoveAll<ICDNServiceClient>();
                services.AddSingleton(stub);
                services.RemoveAll<IIdempotencyStore>();
                if (store is null) services.AddSingleton<IIdempotencyStore>(new RecordingIdempotencyStore());
                else services.AddSingleton(store);
            });
        });

    /// <param name="role">
    /// The OPAQUE user-management role (<c>customer</c> = contract <c>client</c>,
    /// <c>driver</c> = contract <c>jeeber</c>; JeebRoleTranslator). Defaults to
    /// <c>driver</c> so every pre-existing test keeps its exact behaviour.
    /// </param>
    private static HttpClient ClientFor(
        WebApplicationFactory<Program> factory, string userId, string role = "driver")
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", role);
        client.DefaultRequestHeaders.Add("Idempotency-Key", "cdn-upload-" + userId);
        return client;
    }

    private static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage resp)
    {
        var stream = await resp.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        return doc.RootElement.Clone();
    }

    private sealed class StubCdn : ICDNServiceClient
    {
        private int _mintCalls;
        public int MintCalls => _mintCalls;
        public int ExpiresInSeconds { get; init; } = 300;

        /// <summary>JEBV4-259: when set, the stub returns this upload_url (e.g. the
        /// relative Local-provider shape) instead of the default absolute one.</summary>
        public string? UploadUrlOverride { get; init; }

        /// <summary>JEBV4-259: the method the upstream advertises (default PUT).</summary>
        public string TicketMethod { get; init; } = "PUT";

        /// <summary>JEBV4-259: the requiredHeaders the upstream advertises (default empty).</summary>
        public IReadOnlyDictionary<string, string> TicketRequiredHeaders { get; init; }
            = new Dictionary<string, string>();

        public Task<CdnUploadTicket> MintUploadUrlAsync(CdnUploadUrlRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref _mintCalls);
            return Task.FromResult(new CdnUploadTicket
            {
                UploadUrl = UploadUrlOverride ?? $"https://cdn.jeeb.lb/put/{request.Slot}?sig=abc",
                ObjectRef = $"cdn://obj/{request.Slot}/{Guid.NewGuid():N}",
                ExpiresInSeconds = ExpiresInSeconds,
                Method = TicketMethod,
                RequiredHeaders = TicketRequiredHeaders,
            });
        }

        public Task<CdnAsset> UploadAsync(CdnUploadRequest request, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnSignedUrl> GetSignedUrlAsync(string assetId, int ttlSeconds, CancellationToken ct)
            => throw new NotImplementedException();

        public Task<CdnAsset?> GetAssetAsync(string assetId, CancellationToken ct)
            => Task.FromResult<CdnAsset?>(null);
    }

    private sealed class RecordingIdempotencyStore : IExternalIdempotencyStore
    {
        private readonly InMemoryIdempotencyStore _inner = new(TimeProvider.System);
        public List<(string Key, int StatusCode, int TtlSeconds)> Puts { get; } = new();

        public Task<IdempotencyOutcome?> GetAsync(string key, CancellationToken ct) =>
            _inner.GetAsync(key, ct);

        public Task<IReadOnlyList<IdempotencyOutcome>> FindByPrefixAsync(
            string prefix, CancellationToken ct) => _inner.FindByPrefixAsync(prefix, ct);

        public Task<IdempotencyOutcome> PutOrGetAsync(
            string key, int statusCode, string responseBodyJson, int ttlSeconds, CancellationToken ct)
        {
            Puts.Add((key, statusCode, ttlSeconds));
            return _inner.PutOrGetAsync(key, statusCode, responseBodyJson, ttlSeconds, ct);
        }
    }
}
