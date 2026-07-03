using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using JeebGateway.Auth.OtpSignIn;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using StackExchange.Redis.Maintenance;
using StackExchange.Redis.Profiling;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Unit coverage for <see cref="RedisOtpRequestRateLimiter"/> — the durable, cross-replica
/// swap for the sign-in OTP-request burst guard (S02 F-E / JEB-37 / PR #32 review B2,
/// AC-GatewayRateLimit).
///
/// These tests deliberately do NOT stand up a live Redis (rule: unit tests must not require
/// one). They exercise the behaviours that are observable WITHOUT a server via a hand-written
/// <see cref="GuardedConnectionMultiplexer"/> whose <c>GetDatabase</c> is guarded (it throws a
/// <see cref="RedisConnectionException"/> and counts calls):
///   • the disabled switch short-circuits BEFORE any Redis access (parity with the in-memory guard);
///   • a Redis outage FAILS OPEN (admits) instead of 500-ing the sign-in path;
///   • the PII-safe key derivation (SHA-256 phone hash + IP normalisation) is byte-for-byte the
///     in-memory limiter's, so the two stores are semantically interchangeable (S02 N13).
/// The sliding-window count arithmetic itself (ZADD/ZREMRANGEBYSCORE/ZCARD/PEXPIRE) is validated
/// against a real Redis in the prod-like environment; here we pin the seams around it.
/// </summary>
public class RedisOtpRequestRateLimiterTests
{
    private static readonly DateTimeOffset FixedNow =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static RedisOtpRequestRateLimiter Build(IConnectionMultiplexer redis, bool enabled)
    {
        var opts = Options.Create(new OtpRequestRateLimitOptions
        {
            Enabled = enabled,
            MaxPerPhonePerWindow = 3,
            MaxPerIpPerWindow = 10,
            WindowSeconds = 60,
        });
        // Deterministic clock; its value is inert for these seams but keeps the limiter's
        // TimeProvider dependency real (mirrors InMemoryOtpRequestRateLimiter's ctor).
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(FixedNow);
        return new RedisOtpRequestRateLimiter(
            redis, opts, clock, NullLogger<RedisOtpRequestRateLimiter>.Instance);
    }

    [Fact]
    public void Disabled_AdmitsEveryRequest_WithoutTouchingRedis()
    {
        var redis = new GuardedConnectionMultiplexer();
        var limiter = Build(redis, enabled: false);

        limiter.TryAcquire("1.2.3.4", "+9613000001").Should().BeTrue();

        redis.GetDatabaseCalls.Should().Be(
            0, "a disabled guard must short-circuit before any Redis access, exactly like the in-memory limiter");
    }

    [Fact]
    public void RedisUnavailable_FailsOpen_AndDoesNotThrow()
    {
        var redis = new GuardedConnectionMultiplexer();
        var limiter = Build(redis, enabled: true);

        // The guarded multiplexer throws on GetDatabase → the limiter must degrade to admit.
        limiter.TryAcquire("1.2.3.4", "+9613000001").Should().BeTrue(
            "a Redis outage must fail open (admit), never 500 the sign-in path");

        redis.GetDatabaseCalls.Should().BeGreaterThan(
            0, "the enabled limiter must actually attempt Redis before falling open");
    }

    [Fact]
    public void HashPhone_IsSha256Hex_NeverExposesRawPhone_AndIsDeterministic()
    {
        const string phone = "+9613000001";

        var first = RedisOtpRequestRateLimiter.HashPhone(phone);
        var second = RedisOtpRequestRateLimiter.HashPhone(phone);

        first.Should().Be(second, "hashing must be deterministic so a phone maps to a stable window key");
        first.Should().HaveLength(64).And.MatchRegex("^[0-9A-F]+$", "SHA-256 hex is 64 uppercase hex chars");
        first.Should().NotContain(phone, "S02 N13: the raw E.164 must never appear in a Redis key");
    }

    [Fact]
    public void HashPhone_TrimsThenHashes_ExactlyLikeInMemoryLimiter()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("+9613000001")));

        RedisOtpRequestRateLimiter.HashPhone("  +9613000001  ").Should().Be(
            expected, "the phone is trimmed then SHA-256 hashed, byte-for-byte as InMemoryOtpRequestRateLimiter does");
    }

    [Theory]
    [InlineData(null, "unknown")]
    [InlineData("", "unknown")]
    [InlineData("   ", "unknown")]
    [InlineData(" 203.0.113.7 ", "203.0.113.7")]
    public void Normalize_MatchesInMemorySemantics(string? input, string expected)
    {
        RedisOtpRequestRateLimiter.Normalize(input).Should().Be(expected);
    }

    // -------------------------------------------------------------------------------------
    // Guarded fake: a minimal IConnectionMultiplexer whose GetDatabase is intercepted so no
    // live Redis is required. GetDatabase counts calls then throws a RedisConnectionException
    // (the exact exception surface the limiter's fail-open catch handles). Every other member
    // is a never-called stub. Hand-written because the test project intentionally carries no
    // mocking framework (xUnit + FluentAssertions only).
    // -------------------------------------------------------------------------------------
    private sealed class GuardedConnectionMultiplexer : IConnectionMultiplexer
    {
        public int GetDatabaseCalls { get; private set; }

        public IDatabase GetDatabase(int db = -1, object? asyncState = null)
        {
            GetDatabaseCalls++;
            throw new RedisConnectionException(
                ConnectionFailureType.UnableToConnect, "guarded fake: Redis intentionally unavailable");
        }

        // --- IDisposable / IAsyncDisposable ---
        public void Dispose() { }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        // --- properties ---
        public string ClientName => "guarded-fake";
        public string Configuration => "guarded-fake";
        public int TimeoutMilliseconds => 0;
        public long OperationCount => 0;
        public bool PreserveAsyncOrder { get; set; }
        public bool IsConnected => false;
        public bool IsConnecting => false;
        public bool IncludeDetailInExceptions { get; set; }
        public int StormLogThreshold { get; set; }

        // --- events (never raised) ---
        public event EventHandler<RedisErrorEventArgs> ErrorMessage { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs> ConnectionFailed { add { } remove { } }
        public event EventHandler<InternalErrorEventArgs> InternalError { add { } remove { } }
        public event EventHandler<ConnectionFailedEventArgs> ConnectionRestored { add { } remove { } }
        public event EventHandler<EndPointEventArgs> ConfigurationChanged { add { } remove { } }
        public event EventHandler<EndPointEventArgs> ConfigurationChangedBroadcast { add { } remove { } }
        public event EventHandler<ServerMaintenanceEvent> ServerMaintenanceEvent { add { } remove { } }
        public event EventHandler<HashSlotMovedEventArgs> HashSlotMoved { add { } remove { } }

        // --- methods (never-called stubs) ---
        public void RegisterProfiler(Func<ProfilingSession?> profilingSessionProvider) { }
        public ServerCounters GetCounters() => throw new NotSupportedException();
        public EndPoint[] GetEndPoints(bool configuredOnly = false) => Array.Empty<EndPoint>();
        public void Wait(Task task) => throw new NotSupportedException();
        public T Wait<T>(Task<T> task) => throw new NotSupportedException();
        public void WaitAll(params Task[] tasks) => throw new NotSupportedException();
        public int HashSlot(RedisKey key) => 0;
        public ISubscriber GetSubscriber(object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(string host, int port, object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(string hostAndPort, object? asyncState = null) => throw new NotSupportedException();
        public IServer GetServer(IPAddress host, int port) => throw new NotSupportedException();
        public IServer GetServer(EndPoint endpoint, object? asyncState = null) => throw new NotSupportedException();
        public IServer[] GetServers() => Array.Empty<IServer>();
        public Task<bool> ConfigureAsync(TextWriter? log = null) => Task.FromResult(true);
        public bool Configure(TextWriter? log = null) => true;
        public string GetStatus() => "guarded-fake";
        public void GetStatus(TextWriter log) { }
        public void Close(bool allowCommandsToComplete = true) { }
        public Task CloseAsync(bool allowCommandsToComplete = true) => Task.CompletedTask;
        public string? GetStormLog() => null;
        public void ResetStormLog() { }
        public long PublishReconfigure(CommandFlags flags = CommandFlags.None) => 0;
        public Task<long> PublishReconfigureAsync(CommandFlags flags = CommandFlags.None) => Task.FromResult(0L);
        public int GetHashSlot(RedisKey key) => 0;
        public void ExportConfiguration(Stream destination, ExportOptions options = ExportOptions.All) { }
        public void AddLibraryNameSuffix(string suffix) { }
    }
}
