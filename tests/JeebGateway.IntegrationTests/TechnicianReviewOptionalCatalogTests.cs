using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.DTOs.Feedback;
using JeebGateway.service.ServiceCatalog;
using JeebGateway.service.ServiceFeedback;
using JeebGateway.service.ServiceUserManagement;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class TechnicianReviewOptionalCatalogTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Public_guid_review_skips_optional_catalog_when_base_url_is_blank(
        string? catalogBaseUrl)
    {
        var tag = Guid.Parse("86b603e5-f61f-4469-83c8-d89dd41c590c");
        var catalog = new RecordingCatalogClient(catalogBaseUrl);

        using var factory = Factory(catalog);
        using var client = IdentifiedClient(factory);

        var response = await client.GetAsync($"/api/Feedback/technician-review?tag={tag}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TechnicianReviewResponseDto>();
        body.Should().NotBeNull();
        body!.Technician.Should().BeNull();
        body.TotalReviewCount.Should().Be(0);
        catalog.ItemGetCalls.Should().Be(0,
            "an absent optional catalog destination must never be called");
    }

    [Fact]
    public async Task Public_guid_review_enriches_from_catalog_when_base_url_is_configured()
    {
        var tag = Guid.Parse("86b603e5-f61f-4469-83c8-d89dd41c590c");
        var catalog = new RecordingCatalogClient("http://catalog.test/");

        using var factory = Factory(catalog);
        using var client = IdentifiedClient(factory);

        var response = await client.GetAsync($"/api/Feedback/technician-review?tag={tag}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<TechnicianReviewResponseDto>();
        body.Should().NotBeNull();
        body!.Technician.Should().NotBeNull();
        body.Technician!.Guid.Should().Be(tag);
        body.Technician.Name.Should().Be("Configured catalog technician");
        catalog.ItemGetCalls.Should().Be(1);
    }

    private static WebApplicationFactory<Program> Factory(RecordingCatalogClient catalog)
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<ServiceFeedbackClient>();
                services.RemoveAll<ServiceCatalogClient>();
                services.RemoveAll<ServiceUserManagementClient>();

                services.AddSingleton<ServiceFeedbackClient>(new EmptyFeedbackClient());
                services.AddSingleton<ServiceCatalogClient>(catalog);
                services.AddSingleton<ServiceUserManagementClient>(new NoCallUserClient());
            });
        });

    private static HttpClient IdentifiedClient(WebApplicationFactory<Program> factory)
    {
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-User-Id", "technician-review-catalog-test");
        return client;
    }

    private sealed class EmptyFeedbackClient : ServiceFeedbackClient
    {
        public EmptyFeedbackClient()
            : base("http://feedback.test/", new HttpClient())
        {
        }

        public override Task<GetGroupedCommentsResponse> GroupedAsync(
            string tag,
            int length,
            int offset,
            int filter,
            CancellationToken cancellationToken)
            => Task.FromResult(new GetGroupedCommentsResponse
            {
                GroupedComments = [],
                TotalReviewCount = 0,
                AverageRating = 0
            });
    }

    private sealed class RecordingCatalogClient : ServiceCatalogClient
    {
        public RecordingCatalogClient(string? baseUrl)
            : base(baseUrl!, new HttpClient())
        {
        }

        public int ItemGetCalls { get; private set; }

        public override Task<ItemResponse> ItemGETAsync(Guid guid, CancellationToken cancellationToken)
        {
            ItemGetCalls++;
            return Task.FromResult(new ItemResponse
            {
                Guid = guid,
                Name = "Configured catalog technician"
            });
        }
    }

    private sealed class NoCallUserClient : ServiceUserManagementClient
    {
        public NoCallUserClient()
            : base("http://user-management.test/", new HttpClient())
        {
        }

        public override Task<UserProfileResponse> ProfileAsync(
            string userId,
            CancellationToken cancellationToken)
            => throw new InvalidOperationException("No profile call is expected for an empty review page.");
    }
}
