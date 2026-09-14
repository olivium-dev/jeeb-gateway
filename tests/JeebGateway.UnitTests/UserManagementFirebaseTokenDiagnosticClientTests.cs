using System.Net;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Auth.FirebaseDiagnostics;
using Xunit;

namespace JeebGateway.UnitTests;

public sealed class UserManagementFirebaseTokenDiagnosticClientTests
{
    [Fact]
    public async Task Posts_exact_contract_to_exact_um_route_and_accepts_only_safe_success()
    {
        var expectedSubjectHash = Sha256("firebase-uid");
        var handler = new StubHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal(
                "https://um.test/api/User/diagnostics/firebase-token",
                request.RequestUri?.AbsoluteUri);
            var requestJson = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            using var document = JsonDocument.Parse(requestJson);
            var root = document.RootElement;
            Assert.Equal("secret-token", root.GetProperty("idToken").GetString());
            Assert.Equal("jeeb-5a293", root.GetProperty("expectedProjectId").GetString());
            Assert.Equal("firebase-uid", root.GetProperty("expectedSubject").GetString());
            Assert.Equal(3, root.EnumerateObject().Count());

            return Json(HttpStatusCode.OK, new
            {
                verified = true,
                projectId = "jeeb-5a293",
                provider = "custom",
                subjectSha256 = expectedSubjectHash,
                ignoredUpstreamField = "must-not-pass-through",
            });
        });
        var client = CreateClient(handler);

        var result = await client.VerifyAsync(
            "secret-token",
            "jeeb-5a293",
            "firebase-uid",
            CancellationToken.None);

        Assert.Equal(FirebaseTokenDiagnosticOutcome.Verified, result.Outcome);
        Assert.Equal(
            new FirebaseTokenDiagnosticResponse(true, "jeeb-5a293", "custom", expectedSubjectHash),
            result.Response);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, FirebaseTokenDiagnosticOutcome.Invalid)]
    [InlineData(HttpStatusCode.NotFound, FirebaseTokenDiagnosticOutcome.Disabled)]
    [InlineData(HttpStatusCode.ServiceUnavailable, FirebaseTokenDiagnosticOutcome.Unavailable)]
    [InlineData(HttpStatusCode.BadRequest, FirebaseTokenDiagnosticOutcome.UpstreamFailure)]
    [InlineData(HttpStatusCode.InternalServerError, FirebaseTokenDiagnosticOutcome.UpstreamFailure)]
    public async Task Maps_only_the_bounded_upstream_statuses(
        HttpStatusCode status,
        FirebaseTokenDiagnosticOutcome expected)
    {
        var client = CreateClient(new StubHandler(_ =>
            Json(status, new { verified = false, rawUid = "must-never-be-read" })));

        var result = await client.VerifyAsync("token", "jeeb-5a293", "subject", CancellationToken.None);

        Assert.Equal(expected, result.Outcome);
        Assert.Null(result.Response);
    }

    [Theory]
    [InlineData("wrong-project", "custom", "6a20a51e4b93c0277f33eb53cd55c8cc96ff09486f9ee6f6ee3f4f5c9e570f86")]
    [InlineData("jeeb-5a293", "unsafe provider", "6a20a51e4b93c0277f33eb53cd55c8cc96ff09486f9ee6f6ee3f4f5c9e570f86")]
    [InlineData("jeeb-5a293", "custom", "not-a-sha256")]
    public async Task Rejects_malformed_or_cross_project_success_as_upstream_failure(
        string projectId,
        string provider,
        string subjectHash)
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, new
        {
            verified = true,
            projectId,
            provider,
            subjectSha256 = subjectHash,
        })));

        var result = await client.VerifyAsync("token", "jeeb-5a293", "subject", CancellationToken.None);

        Assert.Equal(FirebaseTokenDiagnosticOutcome.UpstreamFailure, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Invalid_json_success_is_upstream_failure()
    {
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{not-json", Encoding.UTF8, "application/json"),
        }));

        var result = await client.VerifyAsync("token", "jeeb-5a293", "subject", CancellationToken.None);

        Assert.Equal(FirebaseTokenDiagnosticOutcome.UpstreamFailure, result.Outcome);
    }

    [Fact]
    public async Task Rejects_a_well_formed_hash_for_a_different_subject()
    {
        var client = CreateClient(new StubHandler(_ => Json(HttpStatusCode.OK, new
        {
            verified = true,
            projectId = "jeeb-5a293",
            provider = "custom",
            subjectSha256 = Sha256("different-subject"),
        })));

        var result = await client.VerifyAsync("token", "jeeb-5a293", "subject", CancellationToken.None);

        Assert.Equal(FirebaseTokenDiagnosticOutcome.UpstreamFailure, result.Outcome);
        Assert.Null(result.Response);
    }

    [Fact]
    public async Task Oversized_success_body_is_rejected_without_deserializing_it()
    {
        var client = CreateClient(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(new string('x', 4097), Encoding.UTF8, "application/json"),
        }));

        var result = await client.VerifyAsync("token", "jeeb-5a293", "subject", CancellationToken.None);

        Assert.Equal(FirebaseTokenDiagnosticOutcome.UpstreamFailure, result.Outcome);
    }

    [Fact]
    public async Task Operation_deadline_covers_a_stalled_success_body()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new NeverCompletingStream()),
        });
        var client = new UserManagementFirebaseTokenDiagnosticClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://um.test/") },
            TimeSpan.FromMilliseconds(30));
        var stopwatch = Stopwatch.StartNew();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.VerifyAsync(
            "token",
            "jeeb-5a293",
            "subject",
            CancellationToken.None));

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(2));
    }

    private static UserManagementFirebaseTokenDiagnosticClient CreateClient(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://um.test/") });

    private static HttpResponseMessage Json(HttpStatusCode status, object body) => new(status)
    {
        Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json"),
    };

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(respond(request));
    }

    private sealed class NeverCompletingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
