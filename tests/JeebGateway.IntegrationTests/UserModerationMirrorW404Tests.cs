using System.Net;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Migration;
using JeebGateway.Users;
using JeebGateway.Users.Moderation;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// gwdbx W4-04 — the moderation write-through ships INERT at "local" and only
/// enqueues at dual-write-local-read+; the drainer maps a flip onto the exact
/// W4-03 user-management routes. The read rungs are pinned off by Program.cs
/// validation until O5 resolves (asserted here at the options level).
/// </summary>
public class UserModerationMirrorW404Tests
{
    private static UserManagementModerationMirror Mirror(string mode)
    {
        var options = new GwdbxMigrationOptions { UserModerationMode = mode };
        return new UserManagementModerationMirror(
            new StaticOptionsMonitor(options),
            NullLogger<UserManagementModerationMirror>.Instance);
    }

    private static UserModerationChange Suspend(string userId = "u-1") => new()
    {
        UserId = userId,
        IsSuspended = true,
        Reason = "fraud review",
        ActorRef = "admin-1",
        At = new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero),
    };

    [Fact]
    public async Task Local_Mode_Enqueues_Nothing()
    {
        var mirror = Mirror("local");

        await mirror.MirrorAsync(Suspend(), default);

        mirror.Reader.TryRead(out _).Should().BeFalse("\"local\" must be a strict no-op");
    }

    [Fact]
    public async Task DualWrite_Mode_Enqueues_The_Change()
    {
        var mirror = Mirror("dual-write-local-read");

        await mirror.MirrorAsync(Suspend(), default);

        mirror.Reader.TryRead(out var change).Should().BeTrue();
        change!.UserId.Should().Be("u-1");
        change.IsSuspended.Should().BeTrue();
    }

    [Fact]
    public async Task Store_Suspend_Hands_The_Flip_To_The_Mirror()
    {
        var mirror = Mirror("dual-write-local-read");
        var inner = new InMemoryUsersStore();
        var seeded = await inner.GetOrCreateAsync(Guid.NewGuid().ToString(), default);
        var store = new UpstreamBackedUsersStore(
            new NullProjectionStore(),
            inner,
            new NullUpstreamProfileClient(),
            NullLogger<UpstreamBackedUsersStore>.Instance,
            mirror);

        await store.SuspendAsync(seeded.Id, "abuse", "admin-9", default);
        mirror.Reader.TryRead(out var change).Should().BeTrue("suspend must enqueue exactly one flip");
        change!.IsSuspended.Should().BeTrue();
        change.Reason.Should().Be("abuse");
        change.ActorRef.Should().Be("admin-9");

        await store.UnsuspendAsync(seeded.Id, "admin-9", default);
        mirror.Reader.TryRead(out var change2).Should().BeTrue();
        change2!.IsSuspended.Should().BeFalse();
    }

    [Fact]
    public async Task Drainer_Posts_Suspend_And_Unsuspend_To_The_W403_Routes()
    {
        var mirror = Mirror("dual-write-local-read");
        var handler = new RecordingHandler();
        var factory = new SingleClientFactory(handler, "http://um.test/");
        var drainer = new ModerationMirrorDrainer(
            mirror, factory, NullLogger<ModerationMirrorDrainer>.Instance);

        await drainer.StartAsync(default);
        await mirror.MirrorAsync(Suspend("11111111-1111-1111-1111-111111111111"), default);
        await mirror.MirrorAsync(new UserModerationChange
        {
            UserId = "11111111-1111-1111-1111-111111111111",
            IsSuspended = false,
            ActorRef = "admin-1",
        }, default);

        await WaitForAsync(() => handler.Requests.Count >= 2);
        await drainer.StopAsync(default);

        handler.Requests[0].Path.Should().Be(
            "/api/User/11111111-1111-1111-1111-111111111111/moderation/suspend");
        var suspendBody = JsonSerializer.Deserialize<JsonElement>(handler.Requests[0].Body);
        suspendBody.GetProperty("reason").GetString().Should().Be("fraud review");
        suspendBody.GetProperty("actorRef").GetString().Should().Be("admin-1");
        suspendBody.GetProperty("suspendedAt").GetDateTimeOffset()
            .Should().Be(new DateTimeOffset(2026, 8, 1, 12, 0, 0, TimeSpan.Zero));

        handler.Requests[1].Path.Should().Be(
            "/api/User/11111111-1111-1111-1111-111111111111/moderation/unsuspend");
        var unsuspendBody = JsonSerializer.Deserialize<JsonElement>(handler.Requests[1].Body);
        unsuspendBody.TryGetProperty("reason", out _).Should().BeFalse(
            "unsuspend must not carry a reason (WhenWritingNull)");
    }

    [Fact]
    public void ReadRungs_Are_Pinned_Off_Until_O5()
    {
        // Mirrors the Program.cs Validate predicate byte-for-byte; a rung at or
        // above dual-write-upstream-read must be rejected at boot.
        static bool Guard(GwdbxMigrationOptions o) =>
            GwdbxMigrationOptions.PhaseOf(o.UserModerationMode)
                < GwdbxMigrationPhase.DualWriteUpstreamRead;

        Guard(new GwdbxMigrationOptions { UserModerationMode = "local" }).Should().BeTrue();
        Guard(new GwdbxMigrationOptions { UserModerationMode = "dual-write-local-read" }).Should().BeTrue();
        Guard(new GwdbxMigrationOptions { UserModerationMode = "dual-write-upstream-read" }).Should().BeFalse();
        Guard(new GwdbxMigrationOptions { UserModerationMode = "upstream-authority" }).Should().BeFalse();
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++)
        {
            await Task.Delay(20);
        }
        condition().Should().BeTrue("the drainer should have processed the queue");
    }

    // ---- test doubles ------------------------------------------------------

    private sealed class StaticOptionsMonitor : IOptionsMonitor<GwdbxMigrationOptions>
    {
        public StaticOptionsMonitor(GwdbxMigrationOptions value) => CurrentValue = value;
        public GwdbxMigrationOptions CurrentValue { get; }
        public GwdbxMigrationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GwdbxMigrationOptions, string?> listener) => null;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        public sealed record Recorded(string Path, string Body);

        public List<Recorded> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            lock (Requests)
            {
                Requests.Add(new Recorded(request.RequestUri!.AbsolutePath, body));
            }
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{}"),
            };
        }
    }

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;
        private readonly string _baseUrl;

        public SingleClientFactory(HttpMessageHandler handler, string baseUrl)
        {
            _handler = handler;
            _baseUrl = baseUrl;
        }

        public HttpClient CreateClient(string name)
            => new(_handler, disposeHandler: false) { BaseAddress = new Uri(_baseUrl) };
    }

    private sealed class NullProjectionStore : IUserProjectionStore
    {
        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserProfile?>(null);

        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
            => Task.FromResult(new UserSearchResult { Items = Array.Empty<UserProfile>(), Total = 0 });

        public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct) => Task.CompletedTask;

        public Task SetSuspensionAsync(
            string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
            => Task.CompletedTask;

        public Task PurgePiiAsync(string userId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NullUpstreamProfileClient : IUpstreamUserProfileClient
    {
        public Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult<UserProfile?>(null);
    }
}
