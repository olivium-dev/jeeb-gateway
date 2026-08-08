using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using JeebGateway.Availability;
using JeebGateway.Notifications;
using JeebGateway.Users;
using JeebGateway.service.ServicePushNotification;
using Microsoft.Extensions.Logging;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// P1 shared test doubles for the new-request fan-out. Used by
/// <see cref="NewRequestPushNotifierTests"/> (the fan-out suite) and by
/// <see cref="TierUnificationTests"/> (which asserts the body/tier resolution the fan-out
/// preserves verbatim).
/// </summary>
internal static class P1Fanout
{
    /// <summary>A jeeber_availability row, optionally with stored coordinates.</summary>
    public static JeeberAvailability Jeeber(string id, double? lat = null, double? lng = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new JeeberAvailability
        {
            UserId = id,
            IsOnline = true,
            VehicleType = VehicleType.Car,
            Zone = null,
            Latitude = lat,
            Longitude = lng,
            LastSeenAt = now,
            LastInteractionAt = now,
            UpdatedAt = now,
        };
    }
}

internal sealed record UserSendRecord(string UserId, object Payload);

internal sealed record TopicSendRecord(string Topic, object Payload);

/// <summary>
/// Recording stand-in for the deployed :10040 push client. Overrides BOTH seams — the
/// per-user rail the P1 fan-out uses AND the legacy topic seam — so "zero topic sends"
/// is assertable in the same recorder. The base ctor needs a base URL + HttpClient.
/// </summary>
internal sealed class RecordingPushClient : ServicePushNotificationClient
{
    public RecordingPushClient() : base("http://localhost", new HttpClient()) { }

    public ConcurrentQueue<UserSendRecord> UserSends { get; } = new();

    public ConcurrentQueue<TopicSendRecord> TopicSends { get; } = new();

    private int _attempts;

    public int Attempts => Volatile.Read(ref _attempts);

    /// <summary>Fail every send (degrade-don't-fail contract).</summary>
    public bool Throw { get; init; }

    /// <summary>Fail the sends for specific recipients (the relay's 404 for a device-less user).</summary>
    public Func<string, bool>? ThrowForUser { get; init; }

    /// <summary>Stall each send — proves the create 201 is not on the fan-out's critical path.</summary>
    public TimeSpan? Delay { get; init; }

    public IReadOnlyList<string> RecipientIds => UserSends.Select(s => s.UserId).ToArray();

    public override async Task<SentPayloadResponse> Send_notification_to_userAsync(
        string user_id, SentPayloadToUserRequest body, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        if (Delay is { } delay)
        {
            await Task.Delay(delay, cancellationToken);
        }

        if (Throw || (ThrowForUser?.Invoke(user_id) ?? false))
        {
            throw new InvalidOperationException($"push service unavailable for {user_id}");
        }

        UserSends.Enqueue(new UserSendRecord(user_id, body.Payload));
        return Ok();
    }

    public override Task<SentPayloadResponse> Send_notification_to_topicAsync(
        string topicName, SentPayloadToTopicRequest body, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _attempts);

        if (Throw)
        {
            throw new InvalidOperationException("push service unavailable");
        }

        TopicSends.Enqueue(new TopicSendRecord(topicName, body.Payload));
        return Task.FromResult(Ok());
    }

    private static SentPayloadResponse Ok()
        => new() { Message = "ok", Timestamp = DateTimeOffset.UtcNow };
}

/// <summary>
/// Settable <see cref="IAvailabilityStore"/> backing the two reads the fan-out performs.
/// The write methods throw — the fan-out must never mutate availability.
/// </summary>
internal sealed class FakeAvailabilityStore : IAvailabilityStore
{
    public IReadOnlyList<JeeberAvailability> Online { get; set; } = Array.Empty<JeeberAvailability>();

    public IReadOnlyList<JeeberAvailability> Known { get; set; } = Array.Empty<JeeberAvailability>();

    /// <summary>The window boundary the fan-out asked for, so the 30-day default is assertable.</summary>
    public DateTimeOffset? LastKnownSince { get; private set; }

    public Task<JeeberAvailability> GetAsync(string userId, CancellationToken ct)
        => Task.FromResult(P1Fanout.Jeeber(userId));

    public Task<GoOnlineResult> GoOnlineAsync(string userId, GoOnlineRequest request, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must never write availability");

    public Task<GoOfflineResult> GoOfflineAsync(string userId, GoOfflineReason reason, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must never write availability");

    public Task RecordInteractionAsync(string userId, DateTimeOffset at, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must never write availability");

    public Task<IReadOnlyList<JeeberAvailability>> ListOnlineAsync(CancellationToken ct)
        => Task.FromResult(Online);

    public Task<IReadOnlyList<JeeberAvailability>> ListKnownJeebersAsync(DateTimeOffset since, CancellationToken ct)
    {
        LastKnownSince = since;
        return Task.FromResult(Known);
    }
}

/// <summary>Settable <see cref="IUsersStore"/> for the fallback rung's send-time role re-check
/// (RC-2). Only <c>GetByIdAsync</c> answers; every other member throws — the fan-out only reads.</summary>
internal sealed class FakeUsersStore : IUsersStore
{
    private readonly ConcurrentDictionary<string, UserProfile> _profiles = new();

    private int _lookups;

    /// <summary>Fail every lookup — proves a users-store fault keeps, never drops, candidates.</summary>
    public bool Throw { get; init; }

    public int Lookups => Volatile.Read(ref _lookups);

    public FakeUsersStore WithActiveRole(string userId, string activeRole)
    {
        _profiles[userId] = new UserProfile
        {
            Id = userId,
            Phone = "+9610000000",
            Name = userId,
            ActiveRole = activeRole,
        };
        return this;
    }

    public Task<UserProfile?> GetByIdAsync(string userId, CancellationToken ct)
    {
        Interlocked.Increment(ref _lookups);
        if (Throw)
        {
            throw new InvalidOperationException($"users store unavailable for {userId}");
        }

        _profiles.TryGetValue(userId, out var profile);
        return Task.FromResult(profile);
    }

    public Task<UserProfile> GetOrCreateAsync(string userId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task UpsertProjectionAsync(UserProfile profile, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile> UpdateProfileAsync(string userId, ProfilePatch patch, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<IReadOnlyList<SavedAddress>> ListAddressesAsync(string userId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<SavedAddress?> GetAddressAsync(string userId, string addressId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<SavedAddress> CreateAddressAsync(string userId, AddressUpsert input, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<SavedAddress?> UpdateAddressAsync(string userId, string addressId, AddressUpsert patch, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<bool> DeleteAddressAsync(string userId, string addressId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserSearchResult> SearchAsync(UserSearchQuery query, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile?> SuspendAsync(string userId, string reason, string adminId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile?> UnsuspendAsync(string userId, string adminId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile?> SwitchRoleAsync(string userId, string newRole, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile?> GrantRoleAsync(string userId, string role, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<UserProfile?> RevokeRoleAsync(string userId, string role, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");

    public Task<bool> PurgePiiAsync(string userId, CancellationToken ct)
        => throw new NotSupportedException("the new-request fan-out must only read user profiles");
}

/// <summary>
/// Records what the create hot path ENQUEUES, deterministically — its reader never yields,
/// so the real hosted <see cref="NewRequestFanoutProcessor"/> idles instead of racing the
/// assertions.
/// </summary>
internal sealed class RecordingFanoutQueue : INewRequestFanoutQueue
{
    private readonly Channel<NewRequestNotification> _idle =
        Channel.CreateUnbounded<NewRequestNotification>();

    public ConcurrentQueue<NewRequestNotification> Jobs { get; } = new();

    public bool TryEnqueue(NewRequestNotification notification)
    {
        Jobs.Enqueue(notification);
        return true;
    }

    public ChannelReader<NewRequestNotification> Reader => _idle.Reader;

    public int PendingCount => Jobs.Count;
}

internal sealed record LogEntry(LogLevel Level, string Message, Exception? Exception);

/// <summary>
/// Minimal capturing <see cref="ILogger{T}"/> — the fan-out's structured
/// <c>newreq-fanout …</c> line IS the acceptance evidence (it is what is read from
/// <c>journalctl</c> on MSI), so the tests assert on it directly.
/// </summary>
internal sealed class CapturingLogger<T> : ILogger<T>
{
    public ConcurrentQueue<LogEntry> Entries { get; } = new();

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
        => Entries.Enqueue(new LogEntry(logLevel, formatter(state, exception), exception));

    public bool Has(LogLevel level, string fragment)
        => Entries.Any(e => e.Level == level
                            && e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));

    public bool HasAny(string fragment)
        => Entries.Any(e => e.Message.Contains(fragment, StringComparison.OrdinalIgnoreCase));
}
