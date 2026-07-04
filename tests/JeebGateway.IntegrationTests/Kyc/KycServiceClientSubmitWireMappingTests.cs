using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.Kyc;

/// <summary>
/// JEBV4-113 §3.2 — pins the SubmitAsync wire mapping so <c>id_type</c> /
/// <c>id_number</c> / <c>driver_license_number</c> / <c>driver_license_expiry</c>
/// / <c>tos_accepted_version</c> are no longer silently dropped before the
/// request reaches kyc-service. kyc-service's own <c>KycSubmitRequest</c>
/// (Contracts.cs) has no first-class slot for these, but it DOES accept and
/// durably persist (jsonb <c>metadata</c> column) a generic opaque bag — these
/// tests assert the gateway forwards them there instead of throwing them away,
/// and that the named wire fields (subject/refs/grantsRole) are untouched.
/// </summary>
public sealed class KycServiceClientSubmitWireMappingTests
{
    [Fact]
    public async Task SubmitAsync_Forwards_Previously_Dropped_Fields_Into_Metadata()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, SubmittedEnvelope);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://kyc.test/") };
        var client = new KycServiceClient(http);

        await client.SubmitAsync(new KycSubmitUpstreamPayload
        {
            UserId = "user-1",
            IdType = "national_id",
            IdNumber = "123456789012",
            IdDocumentFrontRef = "cdn://front",
            IdDocumentBackRef = "cdn://back",
            DriverLicenseNumber = "DL-1",
            DriverLicenseExpiry = "2030-01-01",
            VehicleRegistrationRef = "cdn://vehreg",
            SelfieWithLivenessRef = "cdn://selfie",
            TosAcceptedVersion = "v1",
        }, "idem-1", CancellationToken.None);

        var body = handler.LastRequestBody;
        body.Should().NotBeNull();
        using var doc = JsonDocument.Parse(body!);
        var root = doc.RootElement;

        // Named wire fields still map as before.
        root.GetProperty("subject").GetString().Should().Be("user-1");
        root.GetProperty("idFrontRef").GetString().Should().Be("cdn://front");
        root.GetProperty("idBackRef").GetString().Should().Be("cdn://back");
        root.GetProperty("selfieRef").GetString().Should().Be("cdn://selfie");
        root.GetProperty("grantsRole").GetString().Should().Be("jeeber");

        // Previously-dropped fields now ride along in the generic metadata bag
        // kyc-service already accepts and persists.
        var metadata = root.GetProperty("metadata");
        metadata.GetProperty("id_type").GetString().Should().Be("national_id");
        metadata.GetProperty("id_number").GetString().Should().Be("123456789012");
        metadata.GetProperty("driver_license_number").GetString().Should().Be("DL-1");
        metadata.GetProperty("driver_license_expiry").GetString().Should().Be("2030-01-01");
        metadata.GetProperty("tos_accepted_version").GetString().Should().Be("v1");
    }

    [Fact]
    public async Task SubmitAsync_Omits_Metadata_When_None_Of_The_Extra_Fields_Are_Set()
    {
        var handler = new CapturingHandler(HttpStatusCode.Created, SubmittedEnvelope);
        var http = new HttpClient(handler) { BaseAddress = new Uri("http://kyc.test/") };
        var client = new KycServiceClient(http);

        await client.SubmitAsync(new KycSubmitUpstreamPayload
        {
            UserId = "user-2",
            IdDocumentFrontRef = "cdn://front",
            IdDocumentBackRef = "cdn://back",
            SelfieWithLivenessRef = "cdn://selfie",
        }, "idem-2", CancellationToken.None);

        var body = handler.LastRequestBody;
        using var doc = JsonDocument.Parse(body!);
        doc.RootElement.TryGetProperty("metadata", out _).Should().BeFalse();
    }

    private const string SubmittedEnvelope =
        "{\"id\":\"sub-1\",\"status\":\"Submitted\"}";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _json;

        public string? LastRequestBody { get; private set; }

        public CapturingHandler(HttpStatusCode status, string json)
        {
            _status = status;
            _json = json;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
            {
                LastRequestBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            };
        }
    }
}
