using FluentAssertions;
using JeebGateway.Availability;
using JeebGateway.Infrastructure;
using JeebGateway.Requests;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Tiers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;

namespace JeebGateway.IntegrationTests;

[CollectionDefinition("Request expiry PostgreSQL")]
public sealed class PostgresRequestExpiryCollection
    : ICollectionFixture<PostgresRequestExpiryFixture>
{
    public const string CollectionName = "Request expiry PostgreSQL";
}

/// <summary>
/// Real PostgreSQL concurrency proof for the legacy gateway TTL authority.
/// Each sweeper represents a separate gateway replica with its own stale
/// in-memory request projection and a shared durable database.
/// </summary>
[Collection(PostgresRequestExpiryCollection.CollectionName)]
public sealed class PostgresRequestExpiryAuthorityTests
{
    private readonly PostgresRequestExpiryFixture _database;

    public PostgresRequestExpiryAuthorityTests(PostgresRequestExpiryFixture database) =>
        _database = database;

    [Fact]
    public async Task Concurrent_replica_sweeps_expire_pending_request_exactly_once()
    {
        var now = new DateTimeOffset(2026, 7, 24, 12, 0, 0, TimeSpan.Zero);
        var notifier = new InMemoryRequestExpiryNotifier();
        var requestId = Guid.NewGuid().ToString();
        var clientId = Guid.NewGuid().ToString();
        await using var replicaA = await CreateReplicaAsync(requestId, clientId, now, notifier);
        await using var replicaB = await CreateReplicaAsync(requestId, clientId, now, notifier);
        await MirrorCreateAsync(replicaA, requestId);

        replicaA.Clock.Advance(TimeSpan.FromMinutes(31));
        replicaB.Clock.Advance(TimeSpan.FromMinutes(31));

        await Task.WhenAll(
            replicaA.Sweeper.SweepOnceAsync(CancellationToken.None),
            replicaB.Sweeper.SweepOnceAsync(CancellationToken.None));

        notifier.Expiries.Should().ContainSingle(e => e.RequestId == requestId);
        var durable = await ReadAuthorityStateAsync(requestId);
        durable.Status.Should().Be(RequestStatus.Expired);
        durable.ExpiredAt.Should().Be(now.AddMinutes(31));

        var projectedStatuses = new[]
        {
            (await replicaA.Store.GetAsync(requestId, CancellationToken.None))!.Status,
            (await replicaB.Store.GetAsync(requestId, CancellationToken.None))!.Status,
        };
        projectedStatuses.Count(status => status == RequestStatus.Expired).Should().Be(
            1,
            "only the database-winning replica projects and emits side effects");
    }

    [Fact]
    public async Task Concurrent_sweeps_cannot_expire_durably_accepted_request_from_stale_memory()
    {
        var now = new DateTimeOffset(2026, 7, 24, 13, 0, 0, TimeSpan.Zero);
        var notifier = new InMemoryRequestExpiryNotifier();
        var requestId = Guid.NewGuid().ToString();
        var clientId = Guid.NewGuid().ToString();
        await using var replicaA = await CreateReplicaAsync(requestId, clientId, now, notifier);
        await using var replicaB = await CreateReplicaAsync(requestId, clientId, now, notifier);
        await MirrorCreateAsync(replicaA, requestId);

        await replicaA.Mirror.UpdateLifecycleAsync(
            requestId,
            RequestStatus.Accepted,
            gwJeeberId: "jeeber-accepted-upstream",
            gwAcceptedFee: 25m,
            now.AddMinutes(1),
            CancellationToken.None);

        replicaA.Clock.Advance(TimeSpan.FromMinutes(45));
        replicaB.Clock.Advance(TimeSpan.FromMinutes(45));

        (await replicaA.Store.GetAsync(requestId, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);
        (await replicaB.Store.GetAsync(requestId, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending);

        await Task.WhenAll(
            replicaA.Sweeper.SweepOnceAsync(CancellationToken.None),
            replicaB.Sweeper.SweepOnceAsync(CancellationToken.None));

        notifier.Expiries.Should().BeEmpty();
        var durable = await ReadAuthorityStateAsync(requestId);
        durable.Status.Should().Be(RequestStatus.Accepted);
        durable.ExpiredAt.Should().BeNull();
        (await replicaA.Store.GetAsync(requestId, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending, "the rejected expiry cannot mutate stale local state");
        (await replicaB.Store.GetAsync(requestId, CancellationToken.None))!.Status
            .Should().Be(RequestStatus.Pending, "the rejected expiry cannot mutate stale local state");
    }

    private async Task<Replica> CreateReplicaAsync(
        string requestId,
        string clientId,
        DateTimeOffset now,
        InMemoryRequestExpiryNotifier notifier)
    {
        var clock = new MutableTimeProvider(now);
        var store = new InMemoryRequestsStore(clock);
        await store.CreateAsync(RequestInput(requestId, clientId), CancellationToken.None);

        var mirror = new PostgresDurableRequestsMirror(
            new NpgsqlConnectionFactory(_database.ConnectionString),
            NullLogger<PostgresDurableRequestsMirror>.Instance);
        var services = new ServiceCollection()
            .AddSingleton<TimeProvider>(clock)
            .AddSingleton(store)
            .AddSingleton<IRequestsStore>(store)
            .AddSingleton<IDurableRequestsMirror>(mirror)
            .AddSingleton<IRequestExpiryNotifier>(notifier)
            .AddSingleton<IPendingOffersStore, InMemoryPendingOffersStore>()
            .AddSingleton<JeebGateway.Tiers.ITiersStore, InMemoryTiersStore>()
            .BuildServiceProvider();

        var delivery = new DeliveryServiceClient(new HttpClient
        {
            BaseAddress = new Uri("http://unused-delivery.test/"),
        });
        var windows = new TierExpiryWindowResolver(
            new StaticOptionsMonitor<UpstreamFeatureFlags>(
                new UpstreamFeatureFlags { Delivery = false }),
            delivery,
            NullLogger<TierExpiryWindowResolver>.Instance);
        var sweeper = new RequestExpirySweeper(
            services,
            clock,
            Options.Create(new RequestExpiryOptions()),
            windows,
            new StaticOptionsMonitor<RequestExpirySourceOptions>(
                new RequestExpirySourceOptions { Source = "gateway" }),
            NullLogger<RequestExpirySweeper>.Instance);

        return new Replica(services, clock, store, mirror, sweeper);
    }

    private async Task MirrorCreateAsync(Replica replica, string requestId)
    {
        var row = await replica.Store.GetAsync(requestId, CancellationToken.None);
        row.Should().NotBeNull();

        if (_database.UsesExternalPostgres)
        {
            await using var externalConnection = new NpgsqlConnection(_database.ConnectionString);
            await externalConnection.OpenAsync();
            await using var seed = new NpgsqlCommand(
                """
                INSERT INTO delivery_requests (
                    id, status, gw_mirror, gw_status, gw_updated_at
                ) VALUES (
                    @Id, 'pending', TRUE, 'pending', now()
                )
                """,
                externalConnection);
            seed.Parameters.AddWithValue("Id", Guid.Parse(requestId));
            await seed.ExecuteNonQueryAsync();
            return;
        }

        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var insertUser = new NpgsqlCommand(
            """
            INSERT INTO users (id, phone, name)
            VALUES (@Id, @Phone, 'Expiry Authority Test Client')
            ON CONFLICT (id) DO NOTHING
            """,
            connection);
        var clientGuid = Guid.Parse(row!.ClientId);
        var phoneSuffix = BitConverter.ToUInt32(clientGuid.ToByteArray(), 0) % 90_000_000 + 10_000_000;
        insertUser.Parameters.AddWithValue("Id", clientGuid);
        insertUser.Parameters.AddWithValue("Phone", $"+961{phoneSuffix}");
        await insertUser.ExecuteNonQueryAsync();

        await replica.Mirror.UpsertOnCreateAsync(row, CancellationToken.None);
    }

    private async Task<AuthorityState> ReadAuthorityStateAsync(string requestId)
    {
        await using var connection = new NpgsqlConnection(_database.ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT COALESCE(gw_status, status::text), gw_expired_at
              FROM delivery_requests
             WHERE id = @Id
            """,
            connection);
        command.Parameters.AddWithValue("Id", Guid.Parse(requestId));
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).Should().BeTrue();
        return new AuthorityState(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetFieldValue<DateTimeOffset>(1));
    }

    private static CreateRequestInput RequestInput(string requestId, string clientId) => new()
    {
        Id = requestId,
        ClientId = clientId,
        Description = "expiry authority concurrency test",
        TierId = "flash",
        PickupLocation = new GeoPoint { Lat = 24.7136, Lng = 46.6753 },
        DropoffLocation = new GeoPoint { Lat = 24.6309, Lng = 46.7194 },
    };

    private sealed record Replica(
        ServiceProvider Services,
        MutableTimeProvider Clock,
        InMemoryRequestsStore Store,
        PostgresDurableRequestsMirror Mirror,
        RequestExpirySweeper Sweeper) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => Services.DisposeAsync();
    }

    private sealed record AuthorityState(string Status, DateTimeOffset? ExpiredAt);

    private sealed class MutableTimeProvider : TimeProvider
    {
        private DateTimeOffset _now;

        public MutableTimeProvider(DateTimeOffset now) => _now = now;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }

    private sealed class StaticOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}

public sealed class PostgresRequestExpiryFixture : IAsyncLifetime
{
    private const string ExternalPostgresVariable = "JEEB_GATEWAY_TEST_POSTGRES";
    private readonly PostgreSqlContainer? _postgres;
    private readonly string? _externalConnectionString;
    private readonly string _schemaName = $"expiry_authority_{Guid.NewGuid():N}";

    public PostgresRequestExpiryFixture()
    {
        _externalConnectionString = Environment.GetEnvironmentVariable(ExternalPostgresVariable);
        if (string.IsNullOrWhiteSpace(_externalConnectionString))
        {
            _postgres = new PostgreSqlBuilder()
                .WithImage("postgis/postgis:16-3.4-alpine")
                .WithDatabase("jeeb_gateway_expiry_tests")
                .WithUsername("jeeb_test")
                .WithPassword("jeeb_test")
                .WithCleanUp(true)
                .Build();
        }
    }

    public string ConnectionString { get; private set; } = null!;

    public bool UsesExternalPostgres => _postgres is null;

    public async Task InitializeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.StartAsync();
            ConnectionString = _postgres.GetConnectionString();
            await ApplyProductionSchemaAsync();
            return;
        }

        await CreateExternalVerificationSchemaAsync(_externalConnectionString!);
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
            return;
        }

        await using var connection = new NpgsqlConnection(_externalConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"DROP SCHEMA IF EXISTS {_schemaName} CASCADE",
            connection);
        await command.ExecuteNonQueryAsync();
    }

    private async Task ApplyProductionSchemaAsync()
    {
        var root = FindRepositoryRoot();
        string[] migrations =
        {
            "0001_init_users_kyc.sql",
            "0004_init_delivery_requests.sql",
            "0013_scheduled_delivery.sql",
            "0024_delivery_requests_gateway_mirror.sql",
            "0043_delivery_requests_gw_expired_at.sql",
        };

        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var migration in migrations)
        {
            var sql = await File.ReadAllTextAsync(Path.Combine(root, "db", "migrations", migration));
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    private async Task CreateExternalVerificationSchemaAsync(string externalConnectionString)
    {
        await using var connection = new NpgsqlConnection(externalConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            CREATE SCHEMA {_schemaName};
            CREATE TABLE {_schemaName}.delivery_requests (
                id              UUID          PRIMARY KEY,
                status          TEXT          NOT NULL DEFAULT 'pending',
                gw_mirror       BOOLEAN       NOT NULL DEFAULT FALSE,
                gw_status       TEXT          NULL,
                gw_jeeber_id    TEXT          NULL,
                gw_accepted_fee NUMERIC(20,4) NULL,
                gw_expired_at   TIMESTAMPTZ   NULL,
                gw_updated_at   TIMESTAMPTZ   NOT NULL DEFAULT now()
            );
            """,
            connection);
        await command.ExecuteNonQueryAsync();

        ConnectionString = new NpgsqlConnectionStringBuilder(externalConnectionString)
        {
            SearchPath = _schemaName,
        }.ConnectionString;
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory);
             current is not null;
             current = current.Parent)
        {
            if (Directory.Exists(Path.Combine(current.FullName, "db", "migrations")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException(
            "Could not find db/migrations from the test output directory.");
    }
}
