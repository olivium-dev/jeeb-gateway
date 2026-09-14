using JeebGateway.Auth.FirebaseDiagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class FirebaseTokenDiagnosticsControllerTests
{
    [Theory]
    [InlineData("Development", "development", "jeeb-development-msi")]
    [InlineData("Staging", "staging", "jeeb-5a293")]
    public async Task Exact_non_production_pair_forwards_exact_contract_and_returns_safe_success(
        string hostEnvironment,
        string configuredEnvironment,
        string projectId)
    {
        var subjectHash = new string('a', 64);
        var upstream = new StubClient(new(
            FirebaseTokenDiagnosticOutcome.Verified,
            new FirebaseTokenDiagnosticResponse(true, projectId, "custom", subjectHash)));
        var controller = CreateController(upstream, hostEnvironment, configuredEnvironment, projectId);

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("header.payload.signature", projectId, "firebase-uid"),
            CancellationToken.None);

        var ok = Assert.IsType<OkObjectResult>(action);
        var body = Assert.IsType<FirebaseTokenDiagnosticResponse>(ok.Value);
        Assert.True(body.Verified);
        Assert.Equal(projectId, body.ProjectId);
        Assert.Equal("custom", body.Provider);
        Assert.Equal(subjectHash, body.SubjectSha256);
        Assert.Equal(("header.payload.signature", projectId, "firebase-uid"), upstream.LastRequest);
    }

    [Theory]
    [InlineData("Production", "staging", "jeeb-5a293")]
    [InlineData("Development", "development", "jeeb-5a293")]
    [InlineData("Testing", "development", "jeeb-development-msi")]
    public async Task Disallowed_configuration_is_empty_404_without_calling_upstream(
        string hostEnvironment,
        string configuredEnvironment,
        string projectId)
    {
        var upstream = new StubClient(new(FirebaseTokenDiagnosticOutcome.Invalid));
        var controller = CreateController(upstream, hostEnvironment, configuredEnvironment, projectId);

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", projectId, "subject"),
            CancellationToken.None);

        Assert.IsType<NotFoundResult>(action);
        Assert.Null(upstream.LastRequest);
    }

    [Fact]
    public async Task Request_project_must_equal_the_configured_project()
    {
        var upstream = new StubClient(new(FirebaseTokenDiagnosticOutcome.Verified));
        var controller = CreateController(
            upstream,
            Environments.Staging,
            "staging",
            "jeeb-5a293");

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", "jeeb-development-msi", "subject"),
            CancellationToken.None);

        Assert.IsType<BadRequestObjectResult>(action);
        Assert.Null(upstream.LastRequest);
    }

    [Theory]
    [InlineData(128, true)]
    [InlineData(129, false)]
    public async Task Expected_subject_obeys_the_Firebase_uid_length_boundary(
        int subjectLength,
        bool shouldCallUpstream)
    {
        var upstream = new StubClient(new(FirebaseTokenDiagnosticOutcome.Invalid));
        var controller = CreateController(
            upstream,
            Environments.Staging,
            "staging",
            "jeeb-5a293");

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", "jeeb-5a293", new string('u', subjectLength)),
            CancellationToken.None);

        Assert.Equal(shouldCallUpstream, upstream.LastRequest is not null);
        Assert.Equal(
            shouldCallUpstream ? StatusCodes.Status401Unauthorized : StatusCodes.Status400BadRequest,
            StatusCodeOf(action));
    }

    [Theory]
    [InlineData(FirebaseTokenDiagnosticOutcome.Invalid, StatusCodes.Status401Unauthorized)]
    [InlineData(FirebaseTokenDiagnosticOutcome.Disabled, StatusCodes.Status404NotFound)]
    [InlineData(FirebaseTokenDiagnosticOutcome.Unavailable, StatusCodes.Status503ServiceUnavailable)]
    [InlineData(FirebaseTokenDiagnosticOutcome.UpstreamFailure, StatusCodes.Status503ServiceUnavailable)]
    public async Task Upstream_outcomes_map_to_bounded_statuses(
        FirebaseTokenDiagnosticOutcome outcome,
        int statusCode)
    {
        var controller = CreateController(
            new StubClient(new(outcome)),
            Environments.Staging,
            "staging",
            "jeeb-5a293");

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", "jeeb-5a293", "subject"),
            CancellationToken.None);

        Assert.Equal(statusCode, StatusCodeOf(action));
    }

    [Fact]
    public async Task Transport_failure_is_generic_503()
    {
        var controller = CreateController(
            new StubClient(new(FirebaseTokenDiagnosticOutcome.Verified))
            {
                Exception = new HttpRequestException("secret upstream detail"),
            },
            Environments.Staging,
            "staging",
            "jeeb-5a293");

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", "jeeb-5a293", "subject"),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.DoesNotContain("secret", result.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Diagnostic_deadline_is_generic_503()
    {
        var controller = CreateController(
            new StubClient(new(FirebaseTokenDiagnosticOutcome.Verified))
            {
                Exception = new TaskCanceledException("deadline detail"),
            },
            Environments.Staging,
            "staging",
            "jeeb-5a293");

        var action = await controller.Verify(
            new FirebaseTokenDiagnosticRequest("token", "jeeb-5a293", "subject"),
            CancellationToken.None);

        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.DoesNotContain("deadline", result.Value?.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    private static FirebaseTokenDiagnosticsController CreateController(
        StubClient upstream,
        string hostEnvironment,
        string configuredEnvironment,
        string projectId) => new(
            upstream,
            Options.Create(new FirebaseTokenDiagnosticsOptions
            {
                Enabled = true,
                Environment = configuredEnvironment,
                ProjectId = projectId,
            }),
            new FirebaseTokenDiagnosticsOptionsTests.TestHostEnvironment(hostEnvironment));

    private static int StatusCodeOf(IActionResult result) => result switch
    {
        StatusCodeResult status => status.StatusCode,
        ObjectResult status => status.StatusCode ?? StatusCodes.Status200OK,
        _ => throw new Xunit.Sdk.XunitException($"Unexpected result {result.GetType().Name}"),
    };

    private sealed class StubClient(FirebaseTokenDiagnosticResult result)
        : IUserManagementFirebaseTokenDiagnosticClient
    {
        public Exception? Exception { get; init; }
        public (string Token, string Project, string Subject)? LastRequest { get; private set; }

        public Task<FirebaseTokenDiagnosticResult> VerifyAsync(
            string idToken,
            string expectedProjectId,
            string expectedSubject,
            CancellationToken cancellationToken)
        {
            LastRequest = (idToken, expectedProjectId, expectedSubject);
            if (Exception is not null)
            {
                throw Exception;
            }

            return Task.FromResult(result);
        }
    }
}
