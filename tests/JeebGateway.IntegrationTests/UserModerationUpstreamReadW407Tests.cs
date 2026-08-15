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

/// <summary>gwdbx W4-07 — below the read rung the decorator never calls UM; at the rung
/// moderation state serves from UM (fail-closed on faults, W4-06 wire shape).</summary>
public class UserModerationUpstreamReadW407Tests
{
    private static readonly Guid UserA = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid UserB = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private static UserProfile Row(string id, bool suspended = false) => new()
    {
        Id = id,
        Phone = "+96170000001",
        Name = "Row " + id,
        IsSuspended = suspended,
        SuspensionReason = suspended ? "local reason" : null,
        SuspendedAt = suspended ? new DateTimeOffset(2026, 7, 1, 0, 0, 0, TimeSpan.Zero) : null,
        CreatedAt = DateTimeOffset.UnixEpoch,
        UpdatedAt = DateTimeOffset.UnixEpoch,
    };

    private static UserManagementModerationReadStore Store(
        IUserProjectionStore inner, string mode, HttpMessageHandler handler)
        => new(
            inner,
            new SingleClientFactory(handler, "http://um.test/"),
            new StaticOptionsMonitor(new GwdbxMigrationOptions { UserModerationMode = mode }),
            NullLogger<UserManagementModerationReadStore>.Instance);

    [Fact]
    public async Task Below_The_Read_Rung_UM_Is_Never_Called_And_Local_State_Serves()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("must not be called"));
        var inner = new StubProjection(Row(UserA.ToString(), suspended: true));
        var store = Store(inner, "dual-write-local-read", handler);

        var row = await store.GetByIdAsync(UserA.ToString(), default);
        var search = await store.SearchAsync(new UserSearchQuery(), default);

        row!.IsSuspended.Should().BeTrue("local state must serve below the read rung");
        search.Items.Single().IsSuspended.Should().BeTrue();
        handler.Calls.Should().Be(0, "the decorator must be a strict pass-through below the rung");
    }

    [Fact]
    public async Task At_The_Read_Rung_Moderation_State_Serves_From_UM_Not_Local()
    {
        // Local says suspended, UM says active: the served row must be UM's answer.
        var handler = new ScriptedHandler(_ => StatesJson((UserA, false, null, null, null)));
        var inner = new StubProjection(Row(UserA.ToString(), suspended: true));
        var store = Store(inner, "dual-write-upstream-read", handler);

        var row = await store.GetByIdAsync(UserA.ToString(), default);

        handler.Calls.Should().Be(1);
        handler.LastPath.Should().Be("/api/User/moderation/query");
        row!.IsSuspended.Should().BeFalse("UM is the read source at this rung");
        row.SuspensionReason.Should().BeNull();
        row.SuspendedAt.Should().BeNull();
        row.SuspendedBy.Should().BeNull();
    }

    [Fact]
    public async Task Wire_Shape_Maps_The_W406_States_Onto_Search_Rows()
    {
        var suspendedAt = "2026-08-01T12:30:00";
        var handler = new ScriptedHandler(_ => $$"""
            {"states":{"{{UserA}}":{"userId":"{{UserA}}","isSuspended":true,
            "suspensionReason":"fraud","suspendedAt":"{{suspendedAt}}","suspendedBy":"admin-7"},
            "{{UserB}}":{"userId":"{{UserB}}","isSuspended":false,
            "suspensionReason":"stale","suspendedAt":"{{suspendedAt}}","suspendedBy":"gwdbx-w4-05-backfill"}
            },
            "missing":[]}
            """);
        var inner = new StubProjection(Row(UserA.ToString()), Row(UserB.ToString(), suspended: true));
        var store = Store(inner, "dual-write-upstream-read", handler);

        var result = await store.SearchAsync(new UserSearchQuery(), default);

        var a = result.Items.Single(r => r.Id == UserA.ToString());
        a.IsSuspended.Should().BeTrue();
        a.SuspensionReason.Should().Be("fraud");
        a.SuspendedAt.Should().Be(new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero));
        a.SuspendedBy.Should().Be("admin-7");

        // W4-06 residual: stale metadata on an unsuspended row canonicalizes to null.
        var b = result.Items.Single(r => r.Id == UserB.ToString());
        b.IsSuspended.Should().BeFalse();
        b.SuspensionReason.Should().BeNull();
        b.SuspendedAt.Should().BeNull();
        b.SuspendedBy.Should().BeNull();

        var sent = JsonSerializer.Deserialize<JsonElement>(handler.LastBody!);
        sent.GetProperty("userIds").GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task Upstream_Fault_Fails_The_Read_Closed_With_No_Local_Fallback()
    {
        var handler = new ScriptedHandler(_ => null); // null script => 500
        var inner = new StubProjection(Row(UserA.ToString()));
        var store = Store(inner, "dual-write-upstream-read", handler);

        // W3-13 void lesson: a UM fault must surface, never silently serve local.
        var search = () => store.SearchAsync(new UserSearchQuery(), default);
        await search.Should().ThrowAsync<HttpRequestException>();

        var point = () => store.GetByIdAsync(UserA.ToString(), default);
        await point.Should().ThrowAsync<HttpRequestException>();
    }

    [Fact]
    public async Task Missing_Upstream_Ids_And_NonGuid_Ids_Keep_Local_State()
    {
        // UserA missing upstream (definitive answer, not a fault); "otp-fallback" can't be queried.
        var handler = new ScriptedHandler(_ => $$"""{"states":{},"missing":["{{UserA}}"]}""");
        var inner = new StubProjection(Row(UserA.ToString(), suspended: true), Row("otp-fallback"));
        var store = Store(inner, "dual-write-upstream-read", handler);

        var result = await store.SearchAsync(new UserSearchQuery(), default);

        result.Items.Single(r => r.Id == UserA.ToString()).IsSuspended.Should().BeTrue();
        result.Items.Single(r => r.Id == "otp-fallback").IsSuspended.Should().BeFalse();
        var sent = JsonSerializer.Deserialize<JsonElement>(handler.LastBody!);
        sent.GetProperty("userIds").EnumerateArray().Single().GetString()
            .Should().Be(UserA.ToString(), "non-Guid ids must never reach the UM query");
    }

    [Fact]
    public async Task Pages_Beyond_The_W406_Bound_Are_Chunked_Into_Multiple_Queries()
    {
        var rows = Enumerable.Range(0, UserManagementModerationReadStore.MaxIdsPerQuery + 1)
            .Select(_ => Row(Guid.NewGuid().ToString())).ToArray();
        var handler = new ScriptedHandler(_ => """{"states":{},"missing":[]}""");
        var store = Store(new StubProjection(rows), "dual-write-upstream-read", handler);

        await store.SearchAsync(new UserSearchQuery(), default);

        handler.Calls.Should().Be(2, "201 ids must split into a 200-id call and a 1-id call");
        JsonSerializer.Deserialize<JsonElement>(handler.LastBody!)
            .GetProperty("userIds").GetArrayLength().Should().Be(1);
    }

    [Fact]
    public async Task Writes_And_Role_Counts_Always_Pass_Through_To_The_Local_Projection()
    {
        var handler = new ScriptedHandler(_ => throw new InvalidOperationException("reads only"));
        var inner = new StubProjection();
        var store = Store(inner, "dual-write-upstream-read", handler);

        await store.UpsertIdentityAsync(Row(UserA.ToString()), default);
        await store.SetSuspensionAsync(UserA.ToString(), true, "r", DateTimeOffset.UnixEpoch, default);
        await store.PurgePiiAsync(UserA.ToString(), default);
        await store.CountByRolesAsync(new[] { "role_a" }, default);

        inner.Writes.Should().Equal("upsert", "suspension", "purge", "counts");
        handler.Calls.Should().Be(0, "writes stay local; the W4-04 mirror owns the upstream leg");
    }

    [Fact]
    public void ReadRung_Boot_Requires_A_Wired_UM_BaseUrl()
    {
        // Mirrors the Program.cs W4-07 Validate predicate byte-for-byte (W3-13 fail-closed).
        static bool Guard(GwdbxMigrationOptions o, string? baseUrl) =>
            GwdbxMigrationOptions.PhaseOf(o.UserModerationMode)
                < GwdbxMigrationPhase.DualWriteUpstreamRead
            || Uri.TryCreate(baseUrl, UriKind.Absolute, out _);

        var readRung = new GwdbxMigrationOptions { UserModerationMode = "dual-write-upstream-read" };
        Guard(readRung, null).Should().BeFalse("an unwired UM base URL must fail the boot at the read rung");
        Guard(readRung, "not-a-url").Should().BeFalse();
        Guard(readRung, "http://192.168.2.39:10001").Should().BeTrue();
        Guard(new GwdbxMigrationOptions { UserModerationMode = "dual-write-local-read" }, null)
            .Should().BeTrue("below the read rung UM stays optional");
    }

    private static string StatesJson(
        params (Guid Id, bool Suspended, string? Reason, string? At, string? By)[] states)
    {
        var entries = states.Select(s =>
            $"\"{s.Id}\":{{\"userId\":\"{s.Id}\",\"isSuspended\":{(s.Suspended ? "true" : "false")}," +
            $"\"suspensionReason\":{Quote(s.Reason)},\"suspendedAt\":{Quote(s.At)}," +
            $"\"suspendedBy\":{Quote(s.By)}}}");
        return "{\"states\":{" + string.Join(",", entries) + "},\"missing\":[]}";

        static string Quote(string? v) => v is null ? "null" : $"\"{v}\"";
    }

    // ---- test doubles ------------------------------------------------------

    private sealed class StaticOptionsMonitor : IOptionsMonitor<GwdbxMigrationOptions>
    {
        public StaticOptionsMonitor(GwdbxMigrationOptions value) => CurrentValue = value;
        public GwdbxMigrationOptions CurrentValue { get; }
        public GwdbxMigrationOptions Get(string? name) => CurrentValue;
        public IDisposable? OnChange(Action<GwdbxMigrationOptions, string?> listener) => null;
    }

    private sealed class ScriptedHandler : HttpMessageHandler
    {
        private readonly Func<string, string?> _script;

        public ScriptedHandler(Func<string, string?> script) => _script = script;

        public int Calls { get; private set; }
        public string? LastPath { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            LastPath = request.RequestUri!.AbsolutePath;
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            var body = _script(LastBody ?? "");
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.InternalServerError)
                : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) };
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

    private sealed class StubProjection : IUserProjectionStore
    {
        private readonly UserProfile[] _rows;

        public StubProjection(params UserProfile[] rows) => _rows = rows;

        public List<string> Writes { get; } = new();

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
            => Task.FromResult(_rows.FirstOrDefault(r => r.Id == userId));

        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
            => Task.FromResult(new UserSearchResult { Items = _rows, Total = _rows.Length });

        public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
        {
            Writes.Add("upsert");
            return Task.CompletedTask;
        }

        public Task SetSuspensionAsync(
            string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
        {
            Writes.Add("suspension");
            return Task.CompletedTask;
        }

        public Task PurgePiiAsync(string userId, CancellationToken ct)
        {
            Writes.Add("purge");
            return Task.CompletedTask;
        }

        public Task<UserRoleCounts> CountByRolesAsync(
            IReadOnlyCollection<string> opaqueRoles, CancellationToken ct)
        {
            Writes.Add("counts");
            return Task.FromResult(UserRoleCounts.Empty);
        }
    }
}
