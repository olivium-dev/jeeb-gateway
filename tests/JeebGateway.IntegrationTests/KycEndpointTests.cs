using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Kyc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// T-backend-004 / JEEB-22: Jeeber KYC submission and status endpoints.
/// The tests pin every AC bullet:
///   * documents land in encrypted storage (round-trip via the document store)
///   * queue entry created with status pending_review
///   * GET /kyc/status returns the latest submission
///   * required-field and content-type validation
/// </summary>
public class KycEndpointTests
{
    [Fact]
    public async Task Submit_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        using var form = BuildValidForm();
        var resp = await anon.PostAsync("/kyc/submit", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Submit_Returns_202_With_PendingReview_Status()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-happy");
        var before = DateTimeOffset.UtcNow;

        using var form = BuildValidForm();
        var resp = await client.PostAsync("/kyc/submit", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var body = await resp.Content.ReadFromJsonAsync<KycSubmissionResponse>();
        body!.UserId.Should().Be("kyc-happy");
        body.Status.Should().Be(KycStatus.PendingReview);
        body.VehicleType.Should().Be("motorcycle");
        body.VehicleRegistration.Should().Be("KW-12345");
        body.LivenessPassed.Should().BeTrue();
        body.SubmittedAt.Should().BeOnOrAfter(before.AddSeconds(-1));
        body.Id.Should().StartWith("kyc_");
    }

    [Fact]
    public async Task Submit_Persists_Each_Document_In_Encrypted_Storage()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-store-check");

        using var form = BuildValidForm();
        var resp = await client.PostAsync("/kyc/submit", form);
        resp.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var store = factory.Services.GetRequiredService<IKycStore>();
        var docs = factory.Services.GetRequiredService<IKycDocumentStorage>();
        var latest = await store.GetLatestForUserAsync("kyc-store-check", CancellationToken.None);
        latest.Should().NotBeNull();

        foreach (var docId in new[] { latest!.IdFrontDocumentId, latest.IdBackDocumentId, latest.SelfieDocumentId })
        {
            var stored = await docs.GetAsync(docId, CancellationToken.None);
            stored.Should().NotBeNull("each uploaded document must be retrievable from the encrypted store");
            stored!.OwnerId.Should().Be("kyc-store-check");
            stored.SizeBytes.Should().BeGreaterThan(0);
            stored.Decrypt().Length.Should().Be((int)stored.SizeBytes);
        }
    }

    [Fact]
    public async Task Submit_Missing_Selfie_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-no-selfie");

        using var form = new MultipartFormDataContent
        {
            { ImagePart(), "idFront", "front.jpg" },
            { ImagePart(), "idBack", "back.jpg" },
            { new StringContent("car"), "vehicleType" },
            { new StringContent("KW-99999"), "vehicleRegistration" }
        };

        var resp = await client.PostAsync("/kyc/submit", form);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_Missing_Vehicle_Type_Returns_400()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-no-vehicle");

        using var form = new MultipartFormDataContent
        {
            { ImagePart(), "idFront", "front.jpg" },
            { ImagePart(), "idBack", "back.jpg" },
            { ImagePart(), "selfie", "selfie.jpg" },
            { new StringContent("KW-77777"), "vehicleRegistration" }
        };

        var resp = await client.PostAsync("/kyc/submit", form);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Submit_Rejects_Non_Image_Content_Type()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-bad-mime");

        var badPart = new ByteArrayContent(new byte[] { 1, 2, 3 });
        badPart.Headers.ContentType = new MediaTypeHeaderValue("application/pdf");

        using var form = new MultipartFormDataContent
        {
            { badPart, "idFront", "front.pdf" },
            { ImagePart(), "idBack", "back.jpg" },
            { ImagePart(), "selfie", "selfie.jpg" },
            { new StringContent("motorcycle"), "vehicleType" },
            { new StringContent("KW-12345"), "vehicleRegistration" }
        };

        var resp = await client.PostAsync("/kyc/submit", form);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task GetStatus_Without_Submission_Returns_HasSubmission_False()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-fresh");

        var resp = await client.GetAsync("/kyc/status");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KycStatusResponse>();
        body!.UserId.Should().Be("kyc-fresh");
        body.HasSubmission.Should().BeFalse();
        body.Latest.Should().BeNull();
    }

    [Fact]
    public async Task GetStatus_After_Submit_Returns_PendingReview()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-status-flow");

        using (var form = BuildValidForm())
        {
            (await client.PostAsync("/kyc/submit", form)).EnsureSuccessStatusCode();
        }

        var resp = await client.GetAsync("/kyc/status");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await resp.Content.ReadFromJsonAsync<KycStatusResponse>();
        body!.HasSubmission.Should().BeTrue();
        body.Latest.Should().NotBeNull();
        body.Latest!.Status.Should().Be(KycStatus.PendingReview);
        body.Latest.VehicleType.Should().Be("motorcycle");
    }

    [Fact]
    public async Task GetStatus_Without_Identity_Returns_401()
    {
        using var factory = new WebApplicationFactory<Program>();
        var anon = factory.CreateClient();

        var resp = await anon.GetAsync("/kyc/status");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Resubmission_Returns_Latest_From_GetStatus()
    {
        using var factory = new WebApplicationFactory<Program>();
        var client = ClientFor(factory, "kyc-resub");

        using (var first = BuildValidForm("motorcycle", "KW-AAA"))
        {
            (await client.PostAsync("/kyc/submit", first)).EnsureSuccessStatusCode();
        }
        await Task.Delay(10);
        using (var second = BuildValidForm("car", "KW-BBB"))
        {
            (await client.PostAsync("/kyc/submit", second)).EnsureSuccessStatusCode();
        }

        var body = await client.GetFromJsonAsync<KycStatusResponse>("/kyc/status");
        body!.Latest!.VehicleType.Should().Be("car");
        body.Latest.VehicleRegistration.Should().Be("KW-BBB");
    }

    // -----------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------

    private static HttpClient ClientFor(WebApplicationFactory<Program> factory, string userId)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        return client;
    }

    private static MultipartFormDataContent BuildValidForm(
        string vehicleType = "motorcycle",
        string vehicleRegistration = "KW-12345")
    {
        return new MultipartFormDataContent
        {
            { ImagePart(), "idFront", "front.jpg" },
            { ImagePart(), "idBack", "back.jpg" },
            { ImagePart(), "selfie", "selfie.jpg" },
            { new StringContent(vehicleType), "vehicleType" },
            { new StringContent(vehicleRegistration), "vehicleRegistration" }
        };
    }

    private static ByteArrayContent ImagePart()
    {
        // Minimal JPEG SOI/EOI marker pair — enough to satisfy a length-only
        // content check, content-type is validated by header not magic bytes.
        var part = new ByteArrayContent(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9, 0xAA, 0xBB, 0xCC, 0xDD });
        part.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        return part;
    }
}
