using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Options;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Configured partner credentials plus a DevOnly runtime credential rail. Runtime reservations,
/// activation, one-shot consumption, deadline, and revocation live in the shared idempotency KV,
/// so login and cleanup remain correct across gateway replicas and start-first deployments.
/// Only secret hashes are persisted; plaintext credentials are never retained.
/// </summary>
public sealed class PartnerCredentialStore : IPartnerCredentialStore
{
    private sealed record ConfiguredRecord(
        Guid HolderId,
        string Login,
        string DisplayName,
        byte[] SecretHash);

    private sealed record RuntimeRecord(
        Guid HolderId,
        string Login,
        string DisplayName,
        string SecretHash,
        DateTimeOffset ExpiresAt);

    internal static readonly TimeSpan RuntimeCredentialLifetime = TimeSpan.FromMinutes(5);
    internal const string RuntimeIdentifierPrefix = "devtool-partner-";
    private const string ReservationPrefix = "dev-partner-credential:";
    private const string ActiveSuffix = ":active";
    private const string UsedSuffix = ":used";
    private const string RevokedSuffix = ":revoked";
    private static readonly byte[] DummyHash = new byte[32];
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Dictionary<string, ConfiguredRecord> _configured =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<Guid> _configuredHolderIds = new();
    private readonly IIdempotencyStore _runtime;
    private readonly TimeProvider _clock;
    private readonly ILogger<PartnerCredentialStore> _log;

    public PartnerCredentialStore(
        IOptions<PartnerAuthOptions> options,
        IIdempotencyStore runtime,
        TimeProvider clock,
        ILogger<PartnerCredentialStore> log)
    {
        _runtime = runtime;
        _clock = clock;
        _log = log;
        foreach (var row in options.Value.Credentials)
        {
            if (string.IsNullOrWhiteSpace(row.Login)
                || string.IsNullOrWhiteSpace(row.HolderId)
                || string.IsNullOrWhiteSpace(row.SecretSha256)
                || !Guid.TryParse(row.HolderId, out var holderId)
                || !TryParseHex(row.SecretSha256, out var hash))
            {
                _log.LogError(
                    "Partner auth: skipping malformed configured credential (login present={HasLogin}).",
                    !string.IsNullOrWhiteSpace(row.Login));
                continue;
            }

            var login = row.Login.Trim();
            _configured[login] = new ConfiguredRecord(holderId, login, row.DisplayName.Trim(), hash);
            _configuredHolderIds.Add(holderId);
        }
    }

    public async Task<PartnerAccount?> VerifyAsync(string login, string secret, CancellationToken ct)
    {
        var presented = Sha256(secret ?? string.Empty);
        var trimmed = login?.Trim() ?? string.Empty;
        if (_configured.TryGetValue(trimmed, out var configured))
        {
            return CryptographicOperations.FixedTimeEquals(presented, configured.SecretHash)
                ? new PartnerAccount(configured.HolderId, configured.Login, configured.DisplayName)
                : null;
        }

        if (!TryParseRuntimeIdentifier(trimmed, out var holderId))
        {
            CryptographicOperations.FixedTimeEquals(presented, DummyHash);
            return null;
        }

        var key = ReservationKey(holderId);
        var record = Deserialize((await _runtime.GetAsync(key, ct))?.ResponseBodyJson);
        if (record is null
            || record.HolderId != holderId
            || !string.Equals(record.Login, trimmed, StringComparison.Ordinal)
            || !SecretMatches(presented, record.SecretHash)
            || record.ExpiresAt <= _clock.GetUtcNow()
            || await _runtime.GetAsync(key + ActiveSuffix, ct) is null
            || await _runtime.GetAsync(key + RevokedSuffix, ct) is not null)
        {
            return null;
        }

        var claim = await _runtime.PutOrGetAsync(
            key + UsedSuffix, 200, "{}", RemainingSeconds(record.ExpiresAt), ct);
        if (!claim.Inserted) return null;

        return new PartnerAccount(record.HolderId, record.Login, record.DisplayName, record.ExpiresAt);
    }

    public async Task ReserveRuntimeSeedAsync(
        string login,
        Guid holderId,
        string displayName,
        string secret,
        CancellationToken ct)
    {
        ValidateRuntimeIdentity(login, holderId);
        var record = new RuntimeRecord(
            holderId,
            login.Trim(),
            (displayName ?? string.Empty).Trim(),
            Convert.ToHexString(Sha256(secret ?? string.Empty)),
            _clock.GetUtcNow().Add(RuntimeCredentialLifetime));
        var outcome = await _runtime.PutOrGetAsync(
            ReservationKey(holderId), 201, JsonSerializer.Serialize(record, Json),
            (int)RuntimeCredentialLifetime.TotalSeconds, ct);
        var winner = Deserialize(outcome.ResponseBodyJson)
            ?? throw new InvalidOperationException("Runtime credential reservation is unreadable.");
        if (winner.HolderId != record.HolderId
            || !string.Equals(winner.Login, record.Login, StringComparison.Ordinal)
            || !SecretMatches(Convert.FromHexString(record.SecretHash), winner.SecretHash))
        {
            throw new InvalidOperationException("Runtime credential reservation conflicts.");
        }
        if (winner.ExpiresAt <= _clock.GetUtcNow()
            || await _runtime.GetAsync(ReservationKey(holderId) + RevokedSuffix, ct) is not null)
        {
            throw new InvalidOperationException("Runtime credential reservation is expired or revoked.");
        }
    }

    public async Task ActivateRuntimeSeedAsync(string login, Guid holderId, CancellationToken ct)
    {
        ValidateRuntimeIdentity(login, holderId);
        var key = ReservationKey(holderId);
        var record = Deserialize((await _runtime.GetAsync(key, ct))?.ResponseBodyJson);
        if (record is null || record.ExpiresAt <= _clock.GetUtcNow())
            throw new InvalidOperationException("Runtime credential reservation is unavailable.");
        await _runtime.PutOrGetAsync(key + ActiveSuffix, 200, "{}", RemainingSeconds(record.ExpiresAt), ct);
    }

    public async Task<Guid> RemoveAsync(string login, Guid expectedHolderId, CancellationToken ct)
    {
        ValidateRuntimeIdentity(login, expectedHolderId);
        if (_configuredHolderIds.Contains(expectedHolderId))
            throw new InvalidOperationException("Configured credentials cannot be removed by DevOnly cleanup.");

        var key = ReservationKey(expectedHolderId);
        var record = Deserialize((await _runtime.GetAsync(key, ct))?.ResponseBodyJson);
        if (record is not null
            && (!string.Equals(record.Login, login.Trim(), StringComparison.Ordinal)
                || record.HolderId != expectedHolderId))
        {
            throw new InvalidOperationException("Cleanup identity does not match the runtime reservation.");
        }

        await _runtime.PutOrGetAsync(
            key + RevokedSuffix, 200, "{}", (int)RuntimeCredentialLifetime.TotalSeconds, ct);
        return expectedHolderId;
    }

    private void ValidateRuntimeIdentity(string login, Guid holderId)
    {
        var normalizedLogin = login?.Trim() ?? string.Empty;
        if (holderId == Guid.Empty
            || !TryParseRuntimeIdentifier(normalizedLogin, out var parsed)
            || parsed != holderId)
        {
            throw new ArgumentException(
                $"Runtime identifier must be {RuntimeIdentifierPrefix}<holderId-without-dashes>.", nameof(login));
        }
        if (_configured.ContainsKey(normalizedLogin) || _configuredHolderIds.Contains(holderId))
            throw new InvalidOperationException("Runtime credential conflicts with configured partner identity.");
    }

    internal static string RuntimeIdentifier(Guid holderId) =>
        RuntimeIdentifierPrefix + holderId.ToString("N");

    private static bool TryParseRuntimeIdentifier(string login, out Guid holderId)
    {
        holderId = Guid.Empty;
        return login.StartsWith(RuntimeIdentifierPrefix, StringComparison.Ordinal)
            && Guid.TryParseExact(login[RuntimeIdentifierPrefix.Length..], "N", out holderId)
            && string.Equals(login, RuntimeIdentifier(holderId), StringComparison.Ordinal);
    }

    private static string ReservationKey(Guid holderId) => ReservationPrefix + holderId.ToString("N");

    private int RemainingSeconds(DateTimeOffset expiresAt) =>
        Math.Max(1, (int)Math.Ceiling((expiresAt - _clock.GetUtcNow()).TotalSeconds));

    private static RuntimeRecord? Deserialize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        try { return JsonSerializer.Deserialize<RuntimeRecord>(value, Json); }
        catch (JsonException) { return null; }
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool SecretMatches(byte[] presented, string expectedHex)
    {
        if (!TryParseHex(expectedHex, out var expected))
        {
            CryptographicOperations.FixedTimeEquals(presented, DummyHash);
            return false;
        }
        return CryptographicOperations.FixedTimeEquals(presented, expected);
    }

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        try
        {
            bytes = Convert.FromHexString(hex.Trim());
            return bytes.Length == 32;
        }
        catch (FormatException) { return false; }
    }
}
