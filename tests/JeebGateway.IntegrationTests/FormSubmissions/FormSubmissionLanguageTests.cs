using System.Net;
using System.Text;
using JeebGateway.FormSubmissions;
using JeebGateway.Services.Clients;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class FormSubmissionLanguageTests
{
    [Theory]
    [InlineData("ar,en;q=0.8", "ar")]
    [InlineData("en-US,en;q=0.9", "en-US")]
    [InlineData("en;q=0.3,ar;q=0.9", "ar")]
    [InlineData("ar;q=0,*;q=1", "en")]
    [InlineData("invalid; q=oops", "en")]
    [InlineData("", "en")]
    public async Task Multi_value_headers_reach_the_real_client_as_a_valid_single_language(string input, string expected)
    {
        using var handler = new Handler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://unit.invalid/") };
        var client = new FormBuilderServiceClient(http);
        await client.SchemaAsync(FormTemplateRegistry.Onboarding, FormSubmissionLanguage.Resolve(input), default);
        Assert.Equal(expected, handler.Language);
    }

    private sealed class Handler : HttpMessageHandler
    {
        public string? Language { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Language = request.Headers.AcceptLanguage.Single().Value;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent(FormFixtures.Schema.GetRawText(), Encoding.UTF8, "application/json") });
        }
    }
}
