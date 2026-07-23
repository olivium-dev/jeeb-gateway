using System;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Partner.Auth;

/// <summary>
/// Default <see cref="IPartnerCredentialStore"/>: an admin-provisioned roster loaded from
/// <see cref="PartnerAuthOptions"/> at construction, plus a runtime <see cref="Seed"/> seam for the
/// <c>[DevOnly]</c> hook. Registered as a singleton so a dev-seeded credential persists across scoped
/// requests within a host lifetime.
///
/// <para><b>Secrets.</b> Only the SHA-256 hash of a secret is ever held; the plaintext is hashed on
/// the way in and discarded. Verification hashes the presented secret and constant-time-compares the
/// 32-byte digests (<see cref="CryptographicOperations.FixedTimeEquals(ReadOnlySpan{byte}, ReadOnlySpan{byte})"/>),
/// avoiding a timing side channel on the secret. An unknown login runs the SAME compare against a
/// fixed dummy digest so a probe cannot distinguish "no such login" from "wrong secret" by timing —
/// and both return <c>null</c>, so neither can by response either (no user enumeration).</para>
/// </summary>
public sealed class PartnerCredentialStore : IPartnerCredentialStore
{
    private sealed record Record(Guid HolderId, string Login, string DisplayName, byte[] SecretHash);

    // Login (ordinal-ignore-case) -> record. Concurrent for the [DevOnly] Seed path.
    private readonly ConcurrentDictionary<string, Record> _byLogin =
        new(StringComparer.OrdinalIgnoreCase);

    // A fixed, non-matching digest used to equalize timing on the unknown-login branch.
    private static readonly byte[] DummyHash = new byte[32];

    private readonly ILogger<PartnerCredentialStore> _log;

    public PartnerCredentialStore(IOptions<PartnerAuthOptions> options, ILogger<PartnerCredentialStore> log)
    {
        _log = log;

        var rows = options.Value.Credentials;
        foreach (var row in rows)
        {
            // Row-shape is enforced at startup by the option validator; guard defensively so a
            // half-formed row can never silently register an unusable/insecure credential.
            if (string.IsNullOrWhiteSpace(row.Login)
                || string.IsNullOrWhiteSpace(row.HolderId)
                || string.IsNullOrWhiteSpace(row.SecretSha256)
                || !Guid.TryParse(row.HolderId, out var holderId)
                || !TryParseHex(row.SecretSha256, out var hash))
            {
                _log.LogError("Partner auth: skipping a malformed provisioned credential row (login present={HasLogin}).",
                    !string.IsNullOrWhiteSpace(row.Login));
                continue;
            }

            _byLogin[row.Login.Trim()] = new Record(holderId, row.Login.Trim(), row.DisplayName.Trim(), hash);
        }

        _log.LogInformation("Partner auth: loaded {Count} provisioned partner credential(s).", _byLogin.Count);
    }

    public Task<PartnerAccount?> VerifyAsync(string login, string secret, CancellationToken ct)
    {
        var presented = Sha256(secret ?? string.Empty);

        if (string.IsNullOrWhiteSpace(login) || !_byLogin.TryGetValue(login.Trim(), out var record))
        {
            // Unknown login: run the same compare against a dummy so timing does not leak existence,
            // then fail closed. NOTE we do not early-return before hashing above.
            CryptographicOperations.FixedTimeEquals(presented, DummyHash);
            return Task.FromResult<PartnerAccount?>(null);
        }

        if (!CryptographicOperations.FixedTimeEquals(presented, record.SecretHash))
        {
            return Task.FromResult<PartnerAccount?>(null);
        }

        return Task.FromResult<PartnerAccount?>(
            new PartnerAccount(record.HolderId, record.Login, record.DisplayName));
    }

    public void Seed(string login, Guid holderId, string displayName, string secret)
    {
        if (string.IsNullOrWhiteSpace(login))
        {
            throw new ArgumentException("login is required.", nameof(login));
        }

        _byLogin[login.Trim()] = new Record(holderId, login.Trim(),
            (displayName ?? string.Empty).Trim(), Sha256(secret ?? string.Empty));
        _log.LogInformation("Partner auth: [DevOnly] seeded a partner credential for login (holder set).");
    }

    private static byte[] Sha256(string value) => SHA256.HashData(Encoding.UTF8.GetBytes(value));

    private static bool TryParseHex(string hex, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var trimmed = hex.Trim();
        if (trimmed.Length != 64)
        {
            return false;
        }

        try
        {
            bytes = Convert.FromHexString(trimmed);
            return bytes.Length == 32;
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
