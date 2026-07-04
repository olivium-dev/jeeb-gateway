using FluentAssertions;
using JeebGateway.Users;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEB users-durable. Pins the bounce/replica-survivable behaviour of
/// <see cref="UpstreamBackedUsersStore"/>: the UM-resolved identity projection is written
/// to a durable projection store, so a fresh ("cold") store instance backed by the SAME
/// <see cref="IUserProjectionStore"/> observes exactly the state a live instance would —
/// the same durability contract <c>StateServiceDisputeCaseStoreTests</c> pins for the v2
/// dispute-case store. Runs against an in-memory fake projection (fast, no Docker); the
/// real Npgsql <see cref="PostgresUserProjectionStore"/> SQL is exercised by the
/// Testcontainers integration suite, mirroring the settlement-store convention.
/// </summary>
public sealed class UpstreamBackedUsersStoreTests
{
    private static readonly CancellationToken Ct = CancellationToken.None;

    // ── Durable projection: upsert is searchable, and survives a bounce ──────────

    [Fact]
    public async Task UpsertProjection_PersistsToDurableProjection_AdminSearchFindsIt()
    {
        var projection = new FakeUserProjectionStore();
        var store = NewStore(projection);
        var id = Guid.NewGuid().ToString();

        await store.UpsertProjectionAsync(
            Profile(id, phone: "+9611234567", name: "Ada Lovelace", roles: new[] { "customer" }), Ct);

        var result = await store.SearchAsync(new UserSearchQuery { Name = "ada" }, Ct);

        result.Total.Should().Be(1);
        result.Items.Should().ContainSingle(u => u.Id == id);
    }

    [Fact]
    public async Task ColdInstance_OverSameProjection_StillSeesTheUser()
    {
        var projection = new FakeUserProjectionStore();
        var writer = NewStore(projection);
        var id = Guid.NewGuid().ToString();

        await writer.UpsertProjectionAsync(
            Profile(id, phone: "+9611234567", name: "Grace Hopper", roles: new[] { "customer", "driver" }), Ct);

        // Cold instance — fresh store object (fresh in-memory store), SAME durable
        // projection (simulates a gateway bounce / replica move).
        var reader = NewStore(projection);

        var byId = await reader.GetByIdAsync(id, Ct);
        byId.Should().NotBeNull("the durable projection survives a bounce");
        byId!.Name.Should().Be("Grace Hopper");
        byId.Roles.Should().Contain("driver");

        var search = await reader.SearchAsync(new UserSearchQuery { Phone = "123" }, Ct);
        search.Items.Should().ContainSingle(u => u.Id == id);
    }

    // ── Strict superset: a durable-write fault never breaks the in-process path ───

    [Fact]
    public async Task UpsertProjection_DurableWriteFaults_DoesNotThrow_AndInMemoryStillServes()
    {
        // Super-login projection shape: empty phone (invalid for the users table) +
        // a durable store that faults. The login flow must not break.
        var store = NewStore(new ThrowingProjectionStore());
        var id = Guid.NewGuid().ToString();

        var act = async () => await store.UpsertProjectionAsync(
            Profile(id, phone: string.Empty, name: string.Empty, roles: new[] { "customer" }), Ct);

        await act.Should().NotThrowAsync();

        // The token-mint read path still resolves the active role from the in-memory store.
        var got = await store.GetByIdAsync(id, Ct);
        got.Should().NotBeNull();
        got!.ActiveRole.Should().Be("customer");
    }

    // ── UM proxy: a cold read hydrates from user-management, then persists durably ──

    [Fact]
    public async Task GetById_ColdMiss_HydratesFromUpstream_ThenPersistsDurably()
    {
        var projection = new FakeUserProjectionStore();
        var id = Guid.NewGuid().ToString();
        var upstream = new StubUpstreamUserProfileClient(
            Profile(id, phone: string.Empty, name: "Katherine Johnson", roles: new[] { "customer", "driver" }, activeRole: "driver"));
        var store = NewStore(projection, upstream);

        var got = await store.GetByIdAsync(id, Ct);

        got.Should().NotBeNull("a cold read proxies user-management's profile-get");
        got!.Name.Should().Be("Katherine Johnson");
        got.ActiveRole.Should().Be("driver");

        // Persisted — a subsequent cold instance (no upstream) still sees it.
        var cold = NewStore(projection);
        var durable = await cold.GetByIdAsync(id, Ct);
        durable.Should().NotBeNull();
        durable!.Name.Should().Be("Katherine Johnson");
    }

    // ── Suspension is durable (the security-relevant bit survives a bounce) ───────

    [Fact]
    public async Task Suspend_IsDurable_ColdInstanceSeesSuspension()
    {
        var projection = new FakeUserProjectionStore();
        var writer = NewStore(projection);
        var id = Guid.NewGuid().ToString();
        await writer.UpsertProjectionAsync(
            Profile(id, phone: "+9611234567", name: "Ada", roles: new[] { "customer" }), Ct);

        var suspended = await writer.SuspendAsync(id, "fraud", "admin-1", Ct);
        suspended.Should().NotBeNull();
        suspended!.IsSuspended.Should().BeTrue();

        var reader = NewStore(projection);
        var durable = await reader.GetByIdAsync(id, Ct);
        durable!.IsSuspended.Should().BeTrue("suspension must survive a gateway bounce");
        durable.SuspensionReason.Should().Be("fraud");
    }

    // ── jeeberName-gap invariant: a blank re-projection never wipes a learned name ─

    [Fact]
    public async Task Reproject_WithBlankDisplay_PreservesLearnedName_ReplacesIdentity()
    {
        var projection = new FakeUserProjectionStore();
        var writer = NewStore(projection);
        var id = Guid.NewGuid().ToString();

        await writer.UpsertProjectionAsync(
            Profile(id, phone: "+9611234567", name: "Ada Lovelace", roles: new[] { "customer" }), Ct);
        // A re-login projects identity fields only (Name = "").
        await writer.UpsertProjectionAsync(
            Profile(id, phone: "+9611234567", name: string.Empty, roles: new[] { "customer", "driver" }, activeRole: "driver"), Ct);

        var got = await NewStore(projection).GetByIdAsync(id, Ct);
        got!.Name.Should().Be("Ada Lovelace", "a blank projection must not wipe a learned display name");
        got.Roles.Should().Contain("driver", "identity roles are still replaced by the upstream projection");
        got.ActiveRole.Should().Be("driver");
    }

    // ── Behaviour preserved: saved addresses still delegate to the in-memory store ─

    [Fact]
    public async Task SavedAddresses_StillDelegateToInMemoryStore()
    {
        var store = NewStore(new FakeUserProjectionStore());
        var id = Guid.NewGuid().ToString();
        await store.GetOrCreateAsync(id, Ct);

        var created = await store.CreateAddressAsync(
            id, new AddressUpsert { Label = "Home", Line1 = "1 Rue Gemmayze" }, Ct);
        var list = await store.ListAddressesAsync(id, Ct);

        list.Should().ContainSingle(a => a.Id == created.Id && a.Label == "Home");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static UpstreamBackedUsersStore NewStore(
        IUserProjectionStore projection, IUpstreamUserProfileClient? upstream = null)
        => new(
            projection,
            new InMemoryUsersStore(),
            upstream ?? new StubUpstreamUserProfileClient(null),
            NullLogger<UpstreamBackedUsersStore>.Instance);

    private static UserProfile Profile(
        string id, string phone, string name, string[] roles, string activeRole = "customer")
        => new()
        {
            Id = id,
            Phone = phone,
            Name = name,
            Roles = roles.ToList(),
            ActiveRole = activeRole,
            Language = "en",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
        };

    /// <summary>
    /// Dictionary-backed <see cref="IUserProjectionStore"/> that mimics the durable
    /// users-table semantics the real Postgres store enforces in SQL: blank-preserving
    /// display fields, identity replace, suspension preserved across a re-projection,
    /// and case-insensitive name/phone/email search. The analogue of the dispute
    /// suite's <c>FakeIdempotencyStore</c>.
    /// </summary>
    private sealed class FakeUserProjectionStore : IUserProjectionStore
    {
        private readonly Dictionary<string, UserProfile> _rows = new(StringComparer.Ordinal);
        private readonly object _lock = new();

        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
        {
            lock (_lock)
            {
                return Task.FromResult(_rows.TryGetValue(userId, out var p) ? Clone(p) : null);
            }
        }

        public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
        {
            lock (_lock)
            {
                _rows.TryGetValue(profile.Id, out var existing);
                // UserProfile.Phone is init-only, so blank-preserve it at clone time
                // rather than mutating the constructed instance.
                var effectivePhone = (existing is not null && string.IsNullOrWhiteSpace(profile.Phone))
                    ? existing.Phone
                    : profile.Phone;
                var incoming = Clone(profile, effectivePhone);
                if (existing is not null)
                {
                    // Display fields are blank-preserving.
                    if (string.IsNullOrWhiteSpace(incoming.Name)) incoming.Name = existing.Name;
                    if (string.IsNullOrWhiteSpace(incoming.Email)) incoming.Email = existing.Email;
                    if (string.IsNullOrWhiteSpace(incoming.AvatarUrl)) incoming.AvatarUrl = existing.AvatarUrl;
                    // Suspension / rating / created_at preserved on conflict.
                    incoming.IsSuspended = existing.IsSuspended;
                    incoming.SuspensionReason = existing.SuspensionReason;
                    incoming.SuspendedAt = existing.SuspendedAt;
                    incoming.SuspendedBy = existing.SuspendedBy;
                    incoming.Rating = existing.Rating;
                    incoming.RatingCount = existing.RatingCount;
                }
                _rows[profile.Id] = incoming;
            }
            return Task.CompletedTask;
        }

        public Task SetSuspensionAsync(
            string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_rows.TryGetValue(userId, out var row))
                {
                    row.IsSuspended = isSuspended;
                    row.SuspensionReason = isSuspended ? (reason ?? string.Empty) : null;
                    row.SuspendedAt = isSuspended ? (at ?? DateTimeOffset.UtcNow) : null;
                    row.SuspendedBy = null;
                }
            }
            return Task.CompletedTask;
        }

        public Task PurgePiiAsync(string userId, CancellationToken ct)
        {
            lock (_lock)
            {
                if (_rows.TryGetValue(userId, out var row))
                {
                    row.Name = string.Empty;
                    row.Email = null;
                    row.AvatarUrl = null;
                }
            }
            return Task.CompletedTask;
        }

        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
        {
            lock (_lock)
            {
                IEnumerable<UserProfile> matches = _rows.Values;
                if (!string.IsNullOrWhiteSpace(query.Name))
                    matches = matches.Where(u => u.Name.Contains(query.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(query.Phone))
                    matches = matches.Where(u => u.Phone.Contains(query.Phone.Trim(), StringComparison.OrdinalIgnoreCase));
                if (!string.IsNullOrWhiteSpace(query.Email))
                    matches = matches.Where(u => !string.IsNullOrEmpty(u.Email)
                        && u.Email!.Contains(query.Email.Trim(), StringComparison.OrdinalIgnoreCase));

                var ordered = matches.OrderByDescending(u => u.CreatedAt).ToList();
                var page = Math.Max(query.Page, 1);
                var size = Math.Clamp(query.PageSize, 1, 100);
                var slice = ordered.Skip((page - 1) * size).Take(size).Select(u => Clone(u)).ToList();
                return Task.FromResult(new UserSearchResult { Items = slice, Total = ordered.Count });
            }
        }

        private static UserProfile Clone(UserProfile p, string? phoneOverride = null) => new()
        {
            Id = p.Id,
            Phone = phoneOverride ?? p.Phone,
            Email = p.Email,
            Name = p.Name,
            AvatarUrl = p.AvatarUrl,
            Language = p.Language,
            Roles = new List<string>(p.Roles),
            Rating = p.Rating,
            RatingCount = p.RatingCount,
            CreatedAt = p.CreatedAt,
            UpdatedAt = p.UpdatedAt,
            IsSuspended = p.IsSuspended,
            SuspensionReason = p.SuspensionReason,
            SuspendedAt = p.SuspendedAt,
            SuspendedBy = p.SuspendedBy,
            ActiveRole = p.ActiveRole,
            RoleSwitchedAt = p.RoleSwitchedAt,
        };
    }

    /// <summary>Durable store that always faults — pins the best-effort mirror guard.</summary>
    private sealed class ThrowingProjectionStore : IUserProjectionStore
    {
        public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
            => throw new InvalidOperationException("durable read down");
        public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
            => throw new InvalidOperationException("durable search down");
        public Task UpsertIdentityAsync(UserProfile profile, CancellationToken ct)
            => throw new InvalidOperationException("durable upsert down");
        public Task SetSuspensionAsync(string userId, bool isSuspended, string? reason, DateTimeOffset? at, CancellationToken ct)
            => throw new InvalidOperationException("durable suspend down");
        public Task PurgePiiAsync(string userId, CancellationToken ct)
            => throw new InvalidOperationException("durable purge down");
    }

    /// <summary>Stub UM profile-get proxy — returns a preset profile (or null for a miss).</summary>
    private sealed class StubUpstreamUserProfileClient : IUpstreamUserProfileClient
    {
        private readonly UserProfile? _profile;
        public StubUpstreamUserProfileClient(UserProfile? profile) => _profile = profile;
        public Task<UserProfile?> GetProfileAsync(string userId, CancellationToken ct)
            => Task.FromResult(_profile);
    }
}
