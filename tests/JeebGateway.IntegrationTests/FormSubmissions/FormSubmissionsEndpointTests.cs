using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using JeebGateway.FormSubmissions;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class FormSubmissionsEndpointTests
{
    private const string Route = "/form-builder/templates/" + FormTemplateRegistry.Onboarding;
    [Theory]
    [InlineData("driver")]
    [InlineData("customer")]
    [InlineData("admin")]
    public async Task Authenticated_roles_can_upsert_and_read_their_own_answers(string role)
    {
        using var factory = new Factory();
        using var client = factory.Client("user-a", role);
        client.DefaultRequestHeaders.Add("Accept-Language", "ar");
        client.DefaultRequestHeaders.Add("Idempotency-Key", "ignored-upsert-key");
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(Route + "/submission")).StatusCode);
        for (var i = 0; i < 2; i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync(Route + "/submit", FormFixtures.Body)).StatusCode);
        Assert.Single(factory.Store.Records);
        var response = await client.GetFromJsonAsync<JsonElement>(Route + "/submission");
        Assert.Equal("user-a", response.GetProperty("userId").GetString());
        Assert.False(response.GetProperty("coverage").GetProperty("checked").GetBoolean());
        Assert.Equal(33.89, response.GetProperty("data").GetProperty("home_base").GetProperty("lat").GetDouble());
        Assert.Equal("ar", factory.Builder.Language);
        using var other = factory.Client("user-b", role);
        Assert.Equal(HttpStatusCode.NotFound, (await other.GetAsync(Route + "/submission")).StatusCode);
    }

    [Fact]
    public async Task Missing_identity_is_unauthorized()
    {
        using var factory = new Factory();
        using var client = factory.CreateClient();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync(Route + "/submit", FormFixtures.Body)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync(Route + "/submission")).StatusCode);
    }

    [Theory]
    [InlineData("unknown", 404)]
    [InlineData("disabled", 503)]
    [InlineData("missing", 404)]
    [InlineData("unavailable", 502)]
    [InlineData("store-error", 502)]
    [InlineData("store-timeout", 504)]
    public async Task Failures_are_not_successful_writes(string scenario, int expected)
    {
        using var factory = new Factory(enabled: scenario != "disabled");
        if (scenario == "missing") factory.Builder.Failure = new HttpRequestException("missing", null, HttpStatusCode.NotFound);
        if (scenario == "unavailable") factory.Builder.Failure = new HttpRequestException("offline");
        if (scenario == "store-error") factory.Store.Failure = new UserPreferencesUnavailableException("unavailable");
        if (scenario == "store-timeout") factory.Store.Failure = new TimeoutException();
        using var client = factory.Client("user", "customer");
        var route = scenario == "unknown" ? "/form-builder/templates/unknown" : Route;
        Assert.Equal(expected, (int)(await client.PostAsJsonAsync(route + "/submit", FormFixtures.Body)).StatusCode);
        Assert.Empty(factory.Store.Records);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("""{"country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":95,"lng":0}}""", "home_base.lat")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":0,"lng":0},"vehicle_number":"x"}""", "vehicle_number")]
    public async Task Invalid_input_returns_field_problem_without_store_write(string body, string? field)
    {
        using var factory = new Factory();
        using var client = factory.Client("user", "customer");
        var response = await client.PostAsJsonAsync(Route + "/submit", FormFixtures.Json(body));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (field is not null) Assert.Equal(field, problem.GetProperty("field").GetString());
        Assert.Empty(factory.Store.Records);
    }

    [Theory]
    [InlineData(33.89, 35.5, 201)]
    [InlineData(0, 0, 409)]
    public async Task Configured_coverage_is_checked_before_persistence(double lat, double lng, int status)
    {
        using var factory = new Factory(box: true);
        using var client = factory.Client("user", "driver");
        var body = JsonSerializer.SerializeToElement(new { state = "x", country = "x", address = "x", home_base = new { lat, lng } });
        var response = await client.PostAsJsonAsync(Route + "/submit", body);
        Assert.Equal(status, (int)response.StatusCode);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();
        if (status == 201) Assert.Equal("beirut", json.GetProperty("coverage").GetProperty("zoneKey").GetString());
        else { Assert.Empty(factory.Store.Records); Assert.Equal("out_of_coverage", json.GetProperty("reasonCode").GetString()); }
    }

    [Fact]
    public async Task Template_maximum_length_is_enforced_on_the_endpoint()
    {
        using var factory = new Factory();
        using var client = factory.Client("user", "customer");
        var response = await client.PostAsJsonAsync(Route + "/submit", new {
            state = new string('x', 257), country = "x", address = "x", home_base = new { lat = 0, lng = 0 } });
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("state", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("field").GetString());
        Assert.Empty(factory.Store.Records);
    }

    [Theory]
    [InlineData(false, 502)]
    [InlineData(true, 504)]
    public async Task Store_read_errors_are_not_false_absence(bool timeout, int status)
    {
        using var factory = new Factory();
        factory.Store.Failure = timeout ? new TimeoutException() : new UserPreferencesUnavailableException("down");
        using var client = factory.Client("user", "customer");
        Assert.Equal(status, (int)(await client.GetAsync(Route + "/submission")).StatusCode);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"state":"Beirut","country":"Lebanon","address":"Home","home_base":{}}""")]
    [InlineData("""{"state":"","country":"Lebanon","address":"Home","home_base":{"lat":0,"lng":0}}""")]
    public async Task Invalid_completed_answers_return_dependency_failure(string answers)
    {
        using var factory = new Factory();
        factory.Store.Records[("user", FormTemplateRegistry.Onboarding)] = new FormSubmissionRecord(
            TemplateSchemaValidator.ToAnswers(FormFixtures.Json(answers)), DateTimeOffset.UtcNow, null);
        using var client = factory.Client("user", "customer");
        var response = await client.GetAsync(Route + "/submission");
        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("https://jeeb.dev/errors/upstream-unavailable", problem.GetProperty("type").GetString());
        Assert.False(problem.TryGetProperty("submittedAt", out _));
    }

    [Fact]
    public async Task Incomplete_template_prevents_both_write_and_successful_read()
    {
        using var factory = new Factory();
        factory.Builder.Document = FormFixtures.Json("[]");
        using var client = factory.Client("user", "customer");
        Assert.Equal(HttpStatusCode.BadGateway, (await client.PostAsJsonAsync(Route + "/submit", FormFixtures.Body)).StatusCode);
        Assert.Empty(factory.Store.Records);
        factory.Store.Records[("user", FormTemplateRegistry.Onboarding)] = new FormSubmissionRecord(
            TemplateSchemaValidator.ToAnswers(FormFixtures.Body), DateTimeOffset.UtcNow, null);
        Assert.Equal(HttpStatusCode.BadGateway, (await client.GetAsync(Route + "/submission")).StatusCode);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("")]
    [InlineData("null")]
    public async Task Malformed_or_missing_json_never_reaches_store(string text)
    {
        using var factory = new Factory();
        using var client = factory.Client("user", "customer");
        var response = await client.PostAsync(Route + "/submit", new StringContent(text, System.Text.Encoding.UTF8, "application/json"));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Store.Records);
    }

    private sealed class Factory(bool enabled = true, bool box = false) : WebApplicationFactory<Program>
    {
        public FakeFormBuilder Builder { get; } = new();
        public FakeStore Store { get; } = new();
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.UseSetting("FeatureFlags:UseUpstream:FormBuilder", enabled ? "true" : "false");
            if (box)
                foreach (var (key, value) in new Dictionary<string, string> {
                    ["Key"]="beirut", ["MinLatitude"]="33.80", ["MaxLatitude"]="34.00", ["MinLongitude"]="35.40", ["MaxLongitude"]="35.60" })
                    builder.UseSetting("JeeberOnboarding:Coverage:Boundaries:0:" + key, value);
            builder.ConfigureServices(services => {
                services.RemoveAll<IFormSubmissionStore>(); services.AddSingleton<IFormSubmissionStore>(Store);
                services.RemoveAll<IFormBuilderServiceClient>(); services.AddSingleton<IFormBuilderServiceClient>(Builder);
            });
        }
        public HttpClient Client(string user, string role)
        {
            var client = CreateClient();
            client.DefaultRequestHeaders.Add("X-User-Id", user);
            client.DefaultRequestHeaders.Add("X-User-Roles", role);
            return client;
        }
    }

    private sealed class FakeStore : IFormSubmissionStore
    {
        public Dictionary<(string User, string Template), FormSubmissionRecord> Records { get; } = new();
        public Exception? Failure { get; set; }
        public Task<FormSubmissionRecord?> GetAsync(string user, string template, CancellationToken ct)
        { if (Failure is not null) throw Failure; return Task.FromResult(Records.GetValueOrDefault((user, template))); }
        public Task SetAsync(string user, string template, FormSubmissionRecord record, CancellationToken ct)
        { if (Failure is not null) throw Failure; Records[(user, template)] = record; return Task.CompletedTask; }
    }
}
