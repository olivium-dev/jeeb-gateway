using FluentAssertions;
using JeebGateway.Requests;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace JeebGateway.IntegrationTests.Requests;

/// <summary>
/// JEBV4-65 — the shared request-create validator. Proves the single source of
/// truth produces the EXACT envelopes the three create surfaces used, and — the
/// contract-audit AC — that the tier-not-found result has IDENTICAL status +
/// problem type regardless of the surface's field label (so the three routes can
/// no longer drift, and JEBV4-62 can change the status in one place).
/// </summary>
public class RequestCreateValidationTests
{
    // ----- description-required (legacy + JSON surfaces) -----

    [Fact]
    public void DescriptionRequired_Envelope_Is_400_NoType()
    {
        var pd = RequestCreateValidation.DescriptionRequiredProblem();

        pd.Title.Should().Be("description is required.");
        pd.Status.Should().Be(StatusCodes.Status400BadRequest);
        pd.Type.Should().BeNull("the legacy/JSON surfaces ship this 400 without a Type URI");
    }

    // ----- initial-status legality (legacy surface) -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("pending")]
    [InlineData("PENDING")]   // case-insensitive
    [InlineData("scheduled")]
    public void InitialStatus_Legal_Or_Absent_Is_Ok(string? status)
        => RequestCreateValidation.ValidateInitialStatus(status).Should().BeNull();

    [Theory]
    [InlineData("delivered")]
    [InlineData("cancelled")]
    [InlineData("matched")]
    public void InitialStatus_Illegal_Is_422_TransitionNotAllowed(string status)
    {
        var pd = RequestCreateValidation.ValidateInitialStatus(status);

        pd.Should().NotBeNull();
        pd!.Status.Should().Be(StatusCodes.Status422UnprocessableEntity);
        pd.Type.Should().Be("https://jeeb.dev/errors/transition-not-allowed");
        pd.Detail.Should().Contain(status);
    }

    // ----- tier-exists: the JEBV4-62 coupling point -----

    [Fact]
    public async Task TierExists_When_Present_Is_Ok()
    {
        var tiers = new FakeTiersStore("urgent", "same-day");

        (await RequestCreateValidation.ValidateTierExistsAsync(tiers, "urgent", "tierId", CancellationToken.None))
            .Should().BeNull();
    }

    [Theory]
    [InlineData("tierId")] // legacy + JSON surfaces
    [InlineData("tier")]   // voice form surface
    public async Task TierNotFound_Has_Identical_Status_And_Type_Across_Surfaces(string fieldLabel)
    {
        var tiers = new FakeTiersStore("urgent");

        var pd = await RequestCreateValidation.ValidateTierExistsAsync(tiers, "bogus", fieldLabel, CancellationToken.None);

        // AC: identical status + problem TYPE across all three routes.
        pd.Should().NotBeNull();
        pd!.Status.Should().Be(StatusCodes.Status404NotFound);
        pd.Type.Should().Be("https://jeeb.dev/errors/tier-not-found");
        // The field label only tunes the human-readable wording (behaviour-preserving).
        pd.Title.Should().Be($"{fieldLabel} does not match any active delivery tier.");
        pd.Detail.Should().Be($"{fieldLabel}=bogus");
    }

    // ----- audio/photo URL-shape + photo-count (legacy surface) -----

    [Theory]
    [InlineData(null)]
    [InlineData("https://cdn/x.mp3")]
    [InlineData("s3://bucket/x.opus")]
    public void UrlAndPhotos_Valid_Audio_Is_Ok(string? audioUrl)
        => RequestCreateValidation.ValidateUrlAndPhotos(audioUrl, Array.Empty<string>()).Should().BeNull();

    [Fact]
    public void UrlAndPhotos_Bad_Audio_Is_400_AudioUrlInvalid()
    {
        var pd = RequestCreateValidation.ValidateUrlAndPhotos("not-a-url", Array.Empty<string>());

        pd.Should().NotBeNull();
        pd!.Status.Should().Be(StatusCodes.Status400BadRequest);
        pd.Type.Should().Be("https://jeeb.dev/errors/audio-url-invalid");
    }

    [Fact]
    public void UrlAndPhotos_Too_Many_Is_400_PhotosTooMany()
    {
        var photos = Enumerable.Range(0, RequestCreateValidation.MaxPhotos + 1)
            .Select(i => $"https://cdn/p{i}.jpg").ToArray();

        var pd = RequestCreateValidation.ValidateUrlAndPhotos(null, photos);

        pd.Should().NotBeNull();
        pd!.Status.Should().Be(StatusCodes.Status400BadRequest);
        pd.Type.Should().Be("https://jeeb.dev/errors/photos-too-many");
    }

    [Fact]
    public void UrlAndPhotos_Bad_Photo_Is_400_PhotoUrlInvalid()
    {
        var pd = RequestCreateValidation.ValidateUrlAndPhotos(null, new[] { "https://cdn/ok.jpg", "nope" });

        pd.Should().NotBeNull();
        pd!.Status.Should().Be(StatusCodes.Status400BadRequest);
        pd.Type.Should().Be("https://jeeb.dev/errors/photo-url-invalid");
    }

    [Fact]
    public void UrlAndPhotos_Order_Audio_Before_Photos()
    {
        // Bad audio AND too-many photos → the audio violation wins (original order).
        var photos = Enumerable.Range(0, RequestCreateValidation.MaxPhotos + 1)
            .Select(i => $"https://cdn/p{i}.jpg").ToArray();

        var pd = RequestCreateValidation.ValidateUrlAndPhotos("bad", photos);

        pd!.Type.Should().Be("https://jeeb.dev/errors/audio-url-invalid");
    }

    private sealed class FakeTiersStore : ITiersStore
    {
        private readonly HashSet<string> _tiers;
        public FakeTiersStore(params string[] tiers) => _tiers = new(tiers, StringComparer.OrdinalIgnoreCase);
        public Task<bool> ExistsAsync(string tierCode, CancellationToken ct) => Task.FromResult(_tiers.Contains(tierCode));
    }
}
