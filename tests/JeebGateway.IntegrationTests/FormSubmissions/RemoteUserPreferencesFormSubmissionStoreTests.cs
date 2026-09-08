using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using JeebGateway.FormSubmissions;
using JeebGateway.Services.Generated.ServiceRemoteUserPreferences;
using JeebGateway.Users;
using Microsoft.Extensions.DependencyInjection;
using Polly.CircuitBreaker;
using Polly.Timeout;
using Xunit;

namespace JeebGateway.IntegrationTests.FormSubmissions;

public sealed class RemoteUserPreferencesFormSubmissionStoreTests
{
    private static readonly FormSubmissionRecord Record = new(
        TemplateSchemaValidator.ToAnswers(FormFixtures.Body), DateTimeOffset.Parse("2026-09-06T12:00:00Z"), null);

    [Fact]
    public async Task Generated_client_writes_and_reads_one_complete_snapshot()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync("user", FormTemplateRegistry.Onboarding, Record, default);
        var read = await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default);
        AssertRecord(Record, read!);
        Assert.Equal(new[] { "POST", "GET" }, fixture.Transport.Methods);
        Assert.All(fixture.Transport.Paths, path => Assert.Equal(
            "/nested-preferences/user/jeeb.form." + FormTemplateRegistry.Onboarding, path));
        using var wire = JsonDocument.Parse(fixture.Transport.Writes.Single());
        var snapshot = wire.RootElement.GetProperty("value");
        Assert.Equal(4, snapshot.EnumerateObject().Count());
        Assert.Equal("1", snapshot.GetProperty("schema_version").GetString());
        Assert.Equal("", snapshot.GetProperty("zone_key").GetString());
    }

    [Fact]
    public async Task Absent_nested_key_is_not_onboarded()
    {
        using var fixture = new Fixture();
        Assert.Null(await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default));
        Assert.Single(fixture.Transport.Methods);
    }

    [Fact]
    public async Task Failure_before_commit_preserves_prior_complete_snapshot()
    {
        using var fixture = new Fixture();
        await fixture.Store.SetAsync("user", FormTemplateRegistry.Onboarding, Record, default);
        fixture.Transport.WriteFailure = new HttpRequestException();
        await Assert.ThrowsAsync<UserPreferencesUnavailableException>(() => fixture.Store.SetAsync(
            "user", FormTemplateRegistry.Onboarding, Record with { ZoneKey = "new" }, default));
        AssertRecord(Record, (await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default))!);
    }

    [Fact]
    public async Task Commit_then_timeout_reports_failure_but_read_is_one_complete_snapshot()
    {
        using var fixture = new Fixture();
        fixture.Transport.FailAfterCommit = true;
        await Assert.ThrowsAsync<TimeoutException>(() => fixture.Store.SetAsync(
            "user", FormTemplateRegistry.Onboarding, Record, default));
        AssertRecord(Record, (await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default))!);
        Assert.Single(fixture.Transport.Writes);
    }

    [Fact]
    public async Task Two_store_instances_with_overlapping_writes_cannot_mix_record_fields()
    {
        using var fixture = new Fixture();
        var later = Record with
        {
            SubmittedAt = Record.SubmittedAt.AddMinutes(1), ZoneKey = "zone-b",
            Answers = new Dictionary<string, string>(Record.Answers) { ["address"] = "Address B" }
        };
        fixture.Transport.HoldFirstWrite = true;
        var first = fixture.Store.SetAsync("user", FormTemplateRegistry.Onboarding, Record, default);
        await fixture.Transport.FirstWriteEntered.Task;
        var otherInstance = new RemoteUserPreferencesFormSubmissionStore(fixture.Scopes);
        await otherInstance.SetAsync("user", FormTemplateRegistry.Onboarding, later, default);
        AssertRecord(later, (await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default))!);
        fixture.Transport.ReleaseFirstWrite.TrySetResult();
        await first;
        AssertRecord(Record, (await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default))!);
        Assert.Equal(2, fixture.Transport.Writes.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("null")]
    [InlineData("{\"value\":{}}")]
    [InlineData("{\"value\":{\"state\":\"legacy answer\"}}")]
    [InlineData("{\"value\":{\"schema_version\":\"2\",\"submitted_at\":\"2026-09-06T12:00:00.0000000+00:00\",\"zone_key\":\"\",\"answers\":\"{}\"}}")]
    public async Task Legacy_empty_or_invalid_wire_payload_is_dependency_failure(string payload)
    {
        using var fixture = new Fixture();
        fixture.Transport.ForcedRead = payload;
        await Assert.ThrowsAsync<UserPreferencesUnavailableException>(() => fixture.Store.GetAsync(
            "user", FormTemplateRegistry.Onboarding, default));
    }

    [Theory]
    [InlineData("submitted_at", "not-a-timestamp")]
    [InlineData("zone_key", "  ")]
    [InlineData("answers", "{}")]
    [InlineData("answers", "[]")]
    [InlineData("answers", "{\"state\":null}")]
    [InlineData("answers", "{\"state\":\"one\",\"state\":\"two\"}")]
    [InlineData("answers", "{")]
    public void Corrupt_snapshot_is_not_a_completed_record(string key, string value)
    {
        var snapshot = FormSubmissionSnapshot.Encode(Record);
        snapshot[key] = value;
        Assert.Throws<UserPreferencesUnavailableException>(() => FormSubmissionSnapshot.Decode(snapshot));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Polly_timeouts_and_open_circuits_keep_dependency_contract(bool write)
    {
        using var fixture = new Fixture();
        Func<Task> call = write
            ? () => fixture.Store.SetAsync("user", FormTemplateRegistry.Onboarding, Record, default)
            : async () => { await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, default); };
        fixture.Transport.Failure = new TimeoutRejectedException();
        await Assert.ThrowsAsync<TimeoutException>(call);
        fixture.Transport.Failure = new BrokenCircuitException();
        await Assert.ThrowsAsync<UserPreferencesUnavailableException>(call);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_propagates(bool write)
    {
        using var fixture = new Fixture();
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            if (write) await fixture.Store.SetAsync("user", FormTemplateRegistry.Onboarding, Record, canceled.Token);
            else await fixture.Store.GetAsync("user", FormTemplateRegistry.Onboarding, canceled.Token);
        });
    }

    private static void AssertRecord(FormSubmissionRecord expected, FormSubmissionRecord actual)
    {
        Assert.Equal(expected.SubmittedAt, actual.SubmittedAt);
        Assert.Equal(expected.ZoneKey, actual.ZoneKey);
        Assert.Equal(expected.Answers.OrderBy(p => p.Key), actual.Answers.OrderBy(p => p.Key));
    }

    private sealed class Fixture : IDisposable
    {
        public AtomicTransport Transport { get; } = new();
        private readonly HttpClient _http;
        private readonly ServiceProvider _provider;
        public IServiceScopeFactory Scopes => _provider.GetRequiredService<IServiceScopeFactory>();
        public RemoteUserPreferencesFormSubmissionStore Store { get; }
        public Fixture()
        {
            _http = new HttpClient(Transport);
            var services = new ServiceCollection();
            services.AddScoped(_ => new ServiceRemoteUserPreferencesClient("http://unit.invalid/", _http));
            _provider = services.BuildServiceProvider(validateScopes: true);
            Store = new(Scopes);
        }
        public void Dispose() { _provider.Dispose(); _http.Dispose(); }
    }

    // Implements the actual RUP whole-key SQL-upsert contract at the HTTP boundary.
    private sealed class AtomicTransport : HttpMessageHandler
    {
        private readonly ConcurrentDictionary<string, string> _values = new();
        private int _writeCount;
        public ConcurrentQueue<string> Writes { get; } = new();
        public ConcurrentQueue<string> Methods { get; } = new();
        public ConcurrentQueue<string> Paths { get; } = new();
        public Exception? Failure { get; set; }
        public Exception? WriteFailure { get; set; }
        public bool FailAfterCommit { get; set; }
        public bool HoldFirstWrite { get; set; }
        public string? ForcedRead { get; set; }
        public TaskCompletionSource FirstWriteEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirstWrite { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Methods.Enqueue(request.Method.Method); Paths.Enqueue(request.RequestUri!.AbsolutePath);
            if (Failure is not null) throw Failure;
            var key = request.RequestUri.AbsolutePath;
            Assert.StartsWith("/nested-preferences/", key);
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadAsStringAsync(ct);
                Writes.Enqueue(body);
                if (HoldFirstWrite && Interlocked.Increment(ref _writeCount) == 1)
                {
                    FirstWriteEntered.TrySetResult();
                    await ReleaseFirstWrite.Task.WaitAsync(ct);
                }
                if (WriteFailure is not null) throw WriteFailure;
                _values[key] = body;
                if (FailAfterCommit) throw new TimeoutRejectedException();
                return new HttpResponseMessage(HttpStatusCode.Created);
            }
            var payload = ForcedRead;
            if (payload is null && !_values.TryGetValue(key, out payload)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload!, Encoding.UTF8, "application/json")
            };
        }
    }
}
