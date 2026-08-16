using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using JeebGateway.Jobs;
using JeebGateway.StateService.Work;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace JeebGateway.IntegrationTests.Jobs;

public sealed class InternalDurableJobsEndpointTests
{
    [Fact]
    public async Task Endpoint_Accepts_Only_Dedicated_Mounted_Service_Token()
    {
        using var secret = TempSecret.Create(
            "scheduled-job-token-0123456789abcdef0123456789abcdef");
        await using var factory = Factory(secret.Path);
        using var client = factory.CreateClient();

        using var missing = await client.PostAsync(
            "/internal/jobs/data-exports/sweep",
            content: null);
        missing.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var bearerOnlyRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/jobs/data-exports/sweep");
        bearerOnlyRequest.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", secret.Value);
        using var bearerOnly = await client.SendAsync(bearerOnlyRequest);
        bearerOnly.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "a user/admin bearer token is not an executor credential");

        using var wrongRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/jobs/data-exports/sweep");
        wrongRequest.Headers.Add("X-Jeeb-Job-Token", new string('x', secret.Value.Length));
        using var wrong = await client.SendAsync(wrongRequest);
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var validRequest = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/jobs/data-exports/sweep?limit=3");
        validRequest.Headers.Add("X-Jeeb-Job-Token", secret.Value);
        using var valid = await client.SendAsync(validRequest);
        valid.StatusCode.Should().Be(HttpStatusCode.OK);
        var summary = await valid.Content.ReadFromJsonAsync<DurableSweepSummary>();
        summary.Should().Be(new DurableSweepSummary(
            DurableWorkContract.DataExportKind,
            Claimed: 0,
            Completed: 0,
            Deferred: 0,
            Retried: 0,
            Failed: 0,
            LeaseLost: 0,
            Errors: 0));
    }

    [Fact]
    public async Task Unavailable_Mounted_Secret_Returns_503_Without_Running_A_Sweep()
    {
        await using var factory = Factory(tokenFile: string.Empty);
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/internal/jobs/account-deletions/sweep");
        request.Headers.Add("X-Jeeb-Job-Token", new string('x', 64));

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        factory.State.Claims.Should().Be(0);
    }

    private static TestFactory Factory(string tokenFile) => new(tokenFile);

    private sealed class TestFactory : WebApplicationFactory<Program>
    {
        private readonly string _tokenFile;
        public EmptyStateClient State { get; } = new();

        public TestFactory(string tokenFile) => _tokenFile = tokenFile;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            base.ConfigureWebHost(builder);
            builder.UseEnvironment("Development");
            builder.UseSetting("InternalJobAuth:HeaderName", "X-Jeeb-Job-Token");
            builder.UseSetting("InternalJobAuth:TokenFile", _tokenFile);
            builder.UseSetting("Security:ApiKey:Enabled", "false");
            // O4: DurableWorkSweepWorker claims both kinds on start, so "without running a sweep"
            // could never be measured from the endpoint. Off => the endpoint is the only claimer.
            builder.UseSetting("DurableWorkSweep:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IStateWorkItemClient>();
                services.AddSingleton<IStateWorkItemClient>(State);
            });
        }
    }

    private sealed class EmptyStateClient : IStateWorkItemClient
    {
        private int _claims;
        public int Claims => Volatile.Read(ref _claims);

        public Task<IReadOnlyList<StateWorkItem>> ClaimAsync(
            StateWorkClaim request,
            CancellationToken ct)
        {
            Interlocked.Increment(ref _claims);
            return Task.FromResult<IReadOnlyList<StateWorkItem>>([]);
        }

        public Task<StateWorkItem> CreateAsync(
            string idempotencyKey,
            StateWorkItemCreate request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem?> GetAsync(Guid workItemId, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<StateWorkItem?> GetLatestAsync(
            string application,
            string kind,
            string subjectRef,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> RenewLeaseAsync(
            Guid workItemId,
            StateWorkLeaseRenew request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> CompleteAsync(
            Guid workItemId,
            StateWorkComplete request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> DeferAsync(
            Guid workItemId,
            StateWorkDefer request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> FailAsync(
            Guid workItemId,
            StateWorkFail request,
            CancellationToken ct) => throw new NotSupportedException();

        public Task<StateWorkItem> ConsumeAsync(
            Guid workItemId,
            StateWorkConsume request,
            CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TempSecret : IDisposable
    {
        private TempSecret(string path, string value)
        {
            Path = path;
            Value = value;
        }

        public string Path { get; }
        public string Value { get; }

        public static TempSecret Create(string value)
        {
            var path = System.IO.Path.GetTempFileName();
            File.WriteAllText(path, value);
            return new TempSecret(path, value);
        }

        public void Dispose() => File.Delete(Path);
    }
}
