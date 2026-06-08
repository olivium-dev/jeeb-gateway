using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// S05 create-path gates that do NOT depend on the moderation flag:
///   * N5 — illegal initial status ("Picked") → 422 transition_not_allowed.
///   * A2 — multipart/form-data create → 201 (the voice-first create path).
///   * Flag-OFF regression — with the default (moderation OFF) factory, a
///     "kitchen knife" / "arak" order still creates 201 (today's behaviour is
///     byte-for-byte unchanged; the gate is additive and default-off).
///
/// Default factory = FeatureFlags:CreateModeration:Enabled unset (OFF).
/// </summary>
public class CreateGatesEndpointTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory;

    public CreateGatesEndpointTests(WebApplicationFactory<Program> factory)
    {
        _factory = factory;
    }

    // ----- N5: illegal initial transition -------------------------------------

    [Fact]
    public async Task N5_Create_With_Illegal_Initial_Status_Returns_422()
    {
        var client = ClientFor("s05-n5-picked");

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description = "I need two manakish from the bakery",
            tierId = "flash",
            status = "Picked",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var problem = await resp.Content.ReadFromJsonAsync<ProblemDetails>();
        problem!.Type.Should().Be("https://jeeb.dev/errors/transition-not-allowed");
        problem.Status.Should().Be(422);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("scheduled")]
    [InlineData(null)]
    public async Task Create_With_Legal_Or_Absent_Status_Is_Accepted(string? status)
    {
        var client = ClientFor($"s05-n5-legal-{status ?? "none"}");

        object body = status is null
            ? new
            {
                description = "legal status omitted",
                tierId = "flash",
                pickupLocation = new { lat = 33.88, lng = 35.50 },
                dropoffLocation = new { lat = 33.89, lng = 35.51 }
            }
            : new
            {
                description = "legal status supplied",
                tierId = "flash",
                status,
                // 'scheduled' is only legal with a future scheduledAt; supply one
                // so the status value itself is the thing under test, not the
                // scheduled-at coupling.
                scheduledAt = status == "scheduled" ? DateTimeOffset.UtcNow.AddHours(2) : (DateTimeOffset?)null,
                pickupLocation = new { lat = 33.88, lng = 35.50 },
                dropoffLocation = new { lat = 33.89, lng = 35.51 }
            };

        var resp = await client.PostAsJsonAsync("/requests", body);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ----- A2: multipart create -----------------------------------------------

    [Fact]
    public async Task A2_Multipart_Create_Returns_201()
    {
        var client = ClientFor("s05-a2-multipart");

        using var form = new MultipartFormDataContent
        {
            { new StringContent("Two manakish and water"), "description" },
            { new StringContent("flash"), "tierId" },
            { new StringContent("33.88"), "pickupLat" },
            { new StringContent("35.50"), "pickupLng" },
            { new StringContent("33.89"), "dropoffLat" },
            { new StringContent("35.51"), "dropoffLng" },
        };

        var resp = await client.PostAsync("/requests", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<CreatedDto>();
        dto!.Id.Should().NotBeNullOrWhiteSpace();
        dto.Status.Should().Be("pending");
        dto.TierId.Should().Be("flash");
    }

    [Fact]
    public async Task A2_Multipart_VoiceFirst_Derives_Description_From_Transcription()
    {
        var client = ClientFor("s05-a2-voice-first");

        using var form = new MultipartFormDataContent
        {
            // No 'description' field — voice-first; transcription becomes the description.
            { new StringContent("deliver groceries from the corner shop"), "transcription" },
            { new StringContent("flash"), "tierId" },
            { new StringContent("33.88"), "pickupLat" },
            { new StringContent("35.50"), "pickupLng" },
            { new StringContent("33.89"), "dropoffLat" },
            { new StringContent("35.51"), "dropoffLng" },
        };
        // A voice payload may accompany the fields; the create accepts (ignores) it.
        var audio = new ByteArrayContent(new byte[] { 1, 2, 3, 4 });
        audio.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("audio/mpeg");
        form.Add(audio, "voicePayload", "order.mp3");

        var resp = await client.PostAsync("/requests", form);

        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await resp.Content.ReadFromJsonAsync<CreatedDto>();
        dto!.Description.Should().Be("deliver groceries from the corner shop");
    }

    // ----- Flag-OFF regression: gate is additive + default-off ----------------

    [Theory]
    [InlineData("a kitchen knife")]
    [InlineData("a bottle of arak from the shop")]
    public async Task Gate_Off_By_Default_Prohibited_Text_Still_Creates_201(string description)
    {
        var client = ClientFor("s05-gateoff-" + description.GetHashCode());

        var resp = await client.PostAsJsonAsync("/requests", new
        {
            description,
            tierId = "flash",
            pickupLocation = new { lat = 33.88, lng = 35.50 },
            dropoffLocation = new { lat = 33.89, lng = 35.51 }
        });

        resp.StatusCode.Should().Be(HttpStatusCode.Created,
            "the create-moderation gate is additive and default-OFF, so today's path is unchanged");
    }

    // ----------------------------------------------------------------- helpers

    private HttpClient ClientFor(string userId)
    {
        var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", userId);
        client.DefaultRequestHeaders.Add("X-User-Roles", "customer");
        return client;
    }

    private sealed record CreatedDto(string Id, string Status, string? TierId, string Description);
}
