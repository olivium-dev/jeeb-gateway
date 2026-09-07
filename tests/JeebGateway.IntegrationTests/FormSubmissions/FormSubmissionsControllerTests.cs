using System.Net;
using System.Security.Claims;
using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Controllers;
using JeebGateway.FormSubmissions;
using JeebGateway.Services;
using JeebGateway.Users;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class FormSubmissionsControllerTests
{
    [Fact]
    public async Task Upsert_and_read_use_authenticated_identity_and_restore_object_fields()
    {
        using var f = new Fixture();
        f.Controller.Request.Headers.AcceptLanguage = "ar";
        var created = Assert.IsType<ObjectResult>(await f.Controller.Submit(FormTemplateRegistry.Onboarding, FormFixtures.Body, default));
        Assert.Equal(201, created.StatusCode);
        var dto = Assert.IsType<FormSubmissionResponse>(created.Value);
        Assert.Equal("caller", dto.UserId);
        Assert.False(dto.Coverage.Checked);
        Assert.Equal("ar", f.Builder.Language);
        Assert.Equal("caller", f.Store.User);
        Assert.IsType<JsonElement>(dto.Data["home_base"]);
        var result = Assert.IsType<OkObjectResult>(await f.Controller.Get(FormTemplateRegistry.Onboarding, default));
        Assert.Equal(dto.SubmittedAt, Assert.IsType<FormSubmissionResponse>(result.Value).SubmittedAt);
    }

    [Theory]
    [InlineData("missing-identity", 401)]
    [InlineData("unknown-template", 404)]
    [InlineData("flag-off", 503)]
    [InlineData("schema-not-found", 404)]
    [InlineData("schema-down", 502)]
    [InlineData("schema-timeout", 502)]
    [InlineData("schema-polly-timeout", 502)]
    [InlineData("schema-open-circuit", 502)]
    [InlineData("store-down", 502)]
    [InlineData("store-timeout", 504)]
    public async Task Submit_failure_never_claims_success(string scenario, int status)
    {
        using var f = new Fixture(enabled: scenario != "flag-off", identity: scenario != "missing-identity");
        if (scenario == "schema-not-found") f.Builder.Failure = new HttpRequestException("missing", null, HttpStatusCode.NotFound);
        if (scenario == "schema-down") f.Builder.Failure = new HttpRequestException();
        if (scenario == "schema-timeout") f.Builder.Failure = new TimeoutException();
        if (scenario == "schema-polly-timeout") f.Builder.Failure = new TimeoutRejectedException();
        if (scenario == "schema-open-circuit") f.Builder.Failure = new BrokenCircuitException();
        if (scenario == "store-down") f.Store.Failure = new UserPreferencesUnavailableException("down");
        if (scenario == "store-timeout") f.Store.Failure = new TimeoutException();
        var template = scenario == "unknown-template" ? "unknown" : FormTemplateRegistry.Onboarding;
        Assert.Equal(status, Assert.IsAssignableFrom<IStatusCodeActionResult>(await f.Controller.Submit(template, FormFixtures.Body, default)).StatusCode);
        Assert.Null(f.Store.Record);
    }

    [Theory]
    [InlineData("[]", null)]
    [InlineData("""{"country":"x","address":"x","home_base":{"lat":0,"lng":0}}""", "state")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":95,"lng":0}}""", "home_base.lat")]
    [InlineData("""{"state":"x","country":"x","address":"x","home_base":{"lat":0,"lng":0},"vehicle_number":"x"}""", "vehicle_number")]
    public async Task Invalid_body_is_400_without_writing(string json, string? field)
    {
        using var f = new Fixture();
        var result = Assert.IsAssignableFrom<ObjectResult>(await f.Controller.Submit(FormTemplateRegistry.Onboarding, FormFixtures.Json(json), default));
        Assert.Equal(400, result.StatusCode);
        if (field is not null) Assert.Equal(field, Assert.IsType<ProblemDetails>(result.Value).Extensions["field"]);
        Assert.Null(f.Store.Record);
    }

    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 201)]
    public async Task Coverage_precedes_store(bool covered, int status)
    {
        using var f = new Fixture(covered: covered);
        var result = Assert.IsAssignableFrom<ObjectResult>(await f.Controller.Submit(FormTemplateRegistry.Onboarding, FormFixtures.Body, default));
        Assert.Equal(status, result.StatusCode);
        if (!covered) { Assert.Null(f.Store.Record); Assert.Equal("out_of_coverage", Assert.IsType<ProblemDetails>(result.Value).Extensions["reasonCode"]); }
    }

    [Theory]
    [InlineData(false, 502)]
    [InlineData(true, 504)]
    public async Task Read_failure_is_not_an_empty_record(bool timeout, int status)
    {
        using var f = new Fixture();
        f.Store.Failure = timeout ? new TimeoutException() : new UserPreferencesUnavailableException("down");
        Assert.Equal(status, Assert.IsAssignableFrom<ObjectResult>(await f.Controller.Get(FormTemplateRegistry.Onboarding, default)).StatusCode);
    }

    [Fact]
    public async Task Read_before_any_completed_submission_is_404()
    {
        using var f = new Fixture();
        Assert.Equal(404, Assert.IsAssignableFrom<ObjectResult>(await f.Controller.Get(FormTemplateRegistry.Onboarding, default)).StatusCode);
    }

    [Fact]
    public async Task Submission_budget_rejects_oversized_body_before_persisting()
    {
        using var f = new Fixture();
        var body = JsonSerializer.SerializeToElement(new { state = new string('x', 16385) });
        Assert.Equal(413, Assert.IsAssignableFrom<ObjectResult>(await f.Controller.Submit(FormTemplateRegistry.Onboarding, body, default)).StatusCode);
        Assert.Null(f.Store.Record);
        Assert.Equal(16384L, typeof(FormSubmissionsBffController).GetMethod("Submit")!
            .GetCustomAttributesData().Single(a => a.AttributeType == typeof(RequestSizeLimitAttribute))
            .ConstructorArguments.Single().Value);
    }

    [Theory]
    [InlineData("Submit", Capabilities.ProfileWriteSelf)]
    [InlineData("Get", Capabilities.ProfileReadSelf)]
    public void Both_actions_declare_self_scoped_capability(string action, string capability)
    {
        var attribute = typeof(FormSubmissionsBffController).GetMethod(action)!.GetCustomAttributes(typeof(RequireCapabilityAttribute), true).Cast<RequireCapabilityAttribute>().Single();
        Assert.Equal(capability, attribute.Capability);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly ServiceProvider _services;
        public FakeFormBuilder Builder { get; } = new();
        public StoreFake Store { get; } = new();
        public FormSubmissionsBffController Controller { get; }
        public Fixture(bool enabled = true, bool identity = true, bool covered = true)
        {
            var services = new ServiceCollection();
            services.AddLogging(); services.AddControllers();
            services.Configure<UpstreamFeatureFlags>(f => f.FormBuilder = enabled);
            _services = services.BuildServiceProvider();
            Controller = new(Builder, _services.GetRequiredService<IOptionsMonitor<UpstreamFeatureFlags>>(), Store, new CoverageFake(covered));
            Controller.ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext { RequestServices = _services } };
            if (identity) Controller.HttpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.NameIdentifier, "caller") }, "test"));
        }
        public void Dispose() => _services.Dispose();
    }

    private sealed class CoverageFake(bool covered) : IJeeberOnboardingCoverageResolver
    {
        public (bool InCoverage, bool Checked, string? ZoneKey) Resolve(double lat, double lng) => (covered, !covered, null);
    }

    private sealed class StoreFake : IFormSubmissionStore
    {
        public FormSubmissionRecord? Record { get; private set; }
        public string? User { get; private set; }
        public Exception? Failure { get; set; }
        public Task<FormSubmissionRecord?> GetAsync(string user, string template, CancellationToken ct)
        { if (Failure is not null) throw Failure; return Task.FromResult(Record); }
        public Task SetAsync(string user, string template, FormSubmissionRecord record, CancellationToken ct)
        { if (Failure is not null) throw Failure; User = user; Record = record; return Task.CompletedTask; }
    }
}
