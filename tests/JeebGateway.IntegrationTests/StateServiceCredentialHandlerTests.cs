using System.Net;
using FluentAssertions;
using JeebGateway.StateService;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// The gateway→state-service shared-secret client. The file is read per request so an atomic
/// swap rotates the credential without a restart, and token material never reaches a log.
/// </summary>
public sealed class StateServiceCredentialHandlerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("jeeb-state-cred").FullName;

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    private string WriteToken(string content, string name = "token")
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private static string ValidToken(char fill = 'a') => new(fill, 40);

    [Fact]
    public async Task The_token_file_becomes_a_bearer_header_on_every_call()
    {
        var path = WriteToken(ValidToken());
        var capture = new CapturingHandler();
        using var http = Client(path, capture);

        await http.GetAsync("/v1/state/bundles");
        await http.GetAsync("/v1/state/bundles");

        capture.AuthorizationHeaders.Should().HaveCount(2);
        capture.AuthorizationHeaders.Should().OnlyContain(h =>
            h != null && h.StartsWith("Bearer ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_atomic_file_swap_rotates_the_credential_without_a_restart()
    {
        var path = WriteToken(ValidToken('a'));
        var capture = new CapturingHandler();
        using var http = Client(path, capture);

        await http.GetAsync("/v1/state/bundles");
        File.WriteAllText(path, ValidToken('b'));
        await http.GetAsync("/v1/state/bundles");

        capture.AuthorizationHeaders[0].Should().NotBe(capture.AuthorizationHeaders[1],
            "the handler must not cache token material, or a rotation needs a restart");
    }

    [Fact]
    public async Task Surrounding_whitespace_and_a_trailing_newline_are_trimmed()
    {
        var path = WriteToken("  " + ValidToken() + "\n");
        var capture = new CapturingHandler();
        using var http = Client(path, capture);

        await http.GetAsync("/v1/state/bundles");

        capture.AuthorizationHeaders.Single().Should().Be("Bearer " + ValidToken());
    }

    [Theory]
    [InlineData("")]
    [InlineData("short")]
    public async Task A_missing_or_undersized_credential_fails_the_call(string content)
    {
        var path = WriteToken(content);
        using var http = Client(path, new CapturingHandler());

        await FluentActions.Awaiting(() => http.GetAsync("/v1/state/bundles"))
            .Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task An_absent_file_fails_the_call_rather_than_sending_no_credential()
    {
        using var http = Client(Path.Combine(_dir, "does-not-exist"), new CapturingHandler());

        await FluentActions.Awaiting(() => http.GetAsync("/v1/state/bundles"))
            .Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task An_embedded_whitespace_credential_is_rejected()
    {
        var path = WriteToken(new string('a', 20) + " " + new string('b', 20));
        using var http = Client(path, new CapturingHandler());

        await FluentActions.Awaiting(() => http.GetAsync("/v1/state/bundles"))
            .Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task A_relative_path_is_rejected_as_a_configuration_error()
    {
        using var http = Client("relative/token", new CapturingHandler());

        var act = () => http.GetAsync("/v1/state/bundles");
        (await act.Should().ThrowAsync<Exception>()).And.Should().NotBeNull();
    }

    [Fact]
    public void Options_report_no_credential_when_the_token_file_is_unset()
    {
        new StateServiceOptions { BaseUrl = "http://state" }.HasServiceCredential.Should().BeFalse();
        new StateServiceOptions { BaseUrl = "http://state", ServiceTokenFile = "  " }
            .HasServiceCredential.Should().BeFalse();
        new StateServiceOptions { BaseUrl = "http://state", ServiceTokenFile = "/run/secrets/x" }
            .HasServiceCredential.Should().BeTrue();
    }

    [Fact]
    public async Task The_token_value_never_appears_in_the_thrown_error_text()
    {
        var secret = new string('z', 20);
        var path = WriteToken(secret);
        using var http = Client(path, new CapturingHandler());

        var thrown = await FluentActions.Awaiting(() => http.GetAsync("/v1/state/bundles"))
            .Should().ThrowAsync<Exception>();

        thrown.Which.ToString().Should().NotContain(secret);
    }

    private static HttpClient Client(string tokenFile, HttpMessageHandler inner)
    {
        var handler = new StateServiceCredentialHandler(new StateServiceOptions
        {
            BaseUrl = "http://state.test",
            ServiceTokenFile = tokenFile,
        })
        {
            InnerHandler = inner,
        };

        return new HttpClient(handler) { BaseAddress = new Uri("http://state.test/") };
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string?> AuthorizationHeaders { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            AuthorizationHeaders.Add(request.Headers.Authorization?.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
