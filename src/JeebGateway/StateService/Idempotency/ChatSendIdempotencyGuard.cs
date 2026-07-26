using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// JEBV4-335 — collision guard for the chat SEND idempotency key.
///
/// <para><b>The defect.</b> The mobile client keys a chat send on
/// <c>msg-{conversationId}-{N}-u-{userId}</c> where <c>N</c> is a per-screen-session
/// counter (<c>ChatCubit._outboxCounter</c>) that RESTARTS AT 0 on every chat mount.
/// Re-entering a thread the user already used therefore re-presents keys the
/// gateway already stored, and <see cref="IdempotencyMiddleware"/> replayed the
/// cached <c>201</c> WITHOUT forwarding to chat-service. The sender saw success
/// (real 201 + optimistic local render) and the recipient never got the message:
/// silent, invisible data loss. Live-reproduced 2/2 on physical devices —
/// index 0 dropped, index 1 dropped, index 2 landed.</para>
///
/// <para><b>The rule.</b> Idempotency may only collapse "the SAME send retried".
/// A client counter cannot prove sameness, so the gateway REFUSES to trust it on
/// its own for this endpoint: the stored key is bound to a fingerprint of the
/// actual request (authenticated principal + exact request bytes). Two sends that
/// merely collide on a reused counter now hash to DIFFERENT keys and are BOTH
/// forwarded; a genuine retry re-presents identical bytes, hashes to the SAME key
/// and still dedupes exactly once.</para>
///
/// <para><b>Fail open, never closed.</b> If the body cannot be fingerprinted
/// (unreadable, or larger than <see cref="MaxFingerprintBytes"/>) the caller must
/// forward the request undeduped. A duplicated chat bubble is a cosmetic defect;
/// a silently dropped message is data loss. See
/// <see cref="TryDisambiguateAsync"/>.</para>
///
/// <para><b>Scope: this endpoint only.</b> The fingerprint is deliberately NOT
/// applied gateway-wide. Money/state paths (offer accept, dispute resolve, partner
/// wallet) send jittered fields on retry (e.g. <c>acceptedAt = now()</c>), so
/// body-sensitive keying there would DEFEAT dedup and double-execute a side effect
/// — strictly worse than the failure it fixes. Chat append is the inverse: a
/// double-execute is harmless, a false replay is loss.</para>
///
/// <para><b>Residual, needs the mobile lane.</b> Without a per-send nonce from the
/// client, re-sending byte-identical text at a re-used index inside the dedup
/// window is genuinely indistinguishable from a retry and still collapses. The
/// complete fix is a per-send UUID minted at draft time (see JEBV4-335 mobile
/// note); this guard makes the gateway safe with BOTH old and new clients.</para>
/// </summary>
public static class ChatSendIdempotencyGuard
{
    /// <summary>
    /// <see cref="HttpContext.Items"/> slot carrying the collision-guarded client
    /// key the middleware actually deduped on, so the controller forwards the SAME
    /// de-collided key to chat-service instead of the raw collidable counter.
    /// </summary>
    public const string EffectiveKeyItem = "Jeeb.Idempotency.EffectiveClientKey";

    /// <summary>Separator between the client key and the request fingerprint.</summary>
    private const char FingerprintSeparator = '~';

    /// <summary>Fingerprint length (base64url chars of a SHA-256 prefix).</summary>
    private const int FingerprintLength = 16;

    /// <summary>
    /// Upper bound on the bytes we will hash. Beyond this we fail OPEN (no dedup)
    /// rather than spend unbounded time/memory on a key derivation.
    /// </summary>
    public const long MaxFingerprintBytes = 2 * 1024 * 1024;

    /// <summary>Keys longer than this are truncated before the fingerprint is appended.</summary>
    private const int MaxComposedLength = 200;

    private static readonly Regex ChatAppendPath = new(
        @"^/v1/conversations/[^/]+/messages/?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// True for <c>POST /v1/conversations/{conversationId}/messages</c> — the one
    /// endpoint whose client-supplied key is a per-mount counter.
    /// </summary>
    public static bool AppliesTo(HttpRequest request)
    {
        if (!HttpMethods.IsPost(request.Method)) return false;
        var path = request.Path.Value;
        return !string.IsNullOrEmpty(path) && ChatAppendPath.IsMatch(path);
    }

    /// <summary>
    /// Binds <paramref name="clientKey"/> to a fingerprint of THIS request
    /// (authenticated principal + exact body bytes). Returns <c>null</c> when the
    /// body could not be fingerprinted — the caller MUST then fail open and forward
    /// the request without dedup.
    /// </summary>
    /// <remarks>
    /// Enables request buffering and rewinds the stream, so the endpoint still
    /// reads the full body afterwards. Hashing is incremental over a pooled 8 KiB
    /// buffer, so memory stays bounded regardless of body size.
    /// </remarks>
    public static async Task<string?> TryDisambiguateAsync(HttpContext context, string clientKey)
    {
        var request = context.Request;
        try
        {
            request.EnableBuffering();
        }
        catch (Exception)
        {
            return null; // Cannot rewind → cannot fingerprint → fail open.
        }

        using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hasher.AppendData(Encoding.UTF8.GetBytes(ResolvePrincipal(context)));
        hasher.AppendData("\n"u8.ToArray());

        var buffer = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            long total = 0;
            int read;
            while ((read = await request.Body.ReadAsync(buffer.AsMemory(0, buffer.Length), context.RequestAborted)) > 0)
            {
                total += read;
                if (total > MaxFingerprintBytes)
                {
                    RewindQuietly(request);
                    return null;
                }

                hasher.AppendData(buffer, 0, read);
            }
        }
        catch (Exception) when (!context.RequestAborted.IsCancellationRequested)
        {
            RewindQuietly(request);
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        RewindQuietly(request);
        return Compose(clientKey, hasher.GetHashAndReset());
    }

    /// <summary>
    /// Fingerprint from already-materialised material — used by the controller when
    /// the middleware is not mounted (no state-service configured), so the key
    /// forwarded to chat-service is de-collided in EVERY gateway configuration.
    /// </summary>
    public static string DisambiguateFromMaterial(string clientKey, string principal, string material)
    {
        var bytes = Encoding.UTF8.GetBytes($"{principal}\n{material}");
        return Compose(clientKey, SHA256.HashData(bytes));
    }

    /// <summary>
    /// Reads the collision-guarded key the middleware computed, if it ran.
    /// </summary>
    public static string? EffectiveKeyOrNull(HttpContext context) =>
        context.Items.TryGetValue(EffectiveKeyItem, out var value)
            && value is string key
            && key.Length > 0
            ? key
            : null;

    private static string Compose(string clientKey, byte[] hash)
    {
        var fingerprint = Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')[..FingerprintLength];

        // Keep the client key as a readable PREFIX (it stays greppable in the
        // state-service journal) while bounding the persisted key length.
        var room = MaxComposedLength - FingerprintLength - 1;
        var head = clientKey.Length > room ? clientKey[..room] : clientKey;
        return $"{head}{FingerprintSeparator}{fingerprint}";
    }

    private static void RewindQuietly(HttpRequest request)
    {
        try
        {
            if (request.Body.CanSeek) request.Body.Position = 0;
        }
        catch (Exception)
        {
            // Nothing further we can do; the endpoint will observe an empty body
            // and answer 400 — visible, never a silent drop.
        }
    }

    /// <summary>
    /// The authenticated principal the send is attributed to. Included so an OLD
    /// client that omits the <c>-u-{userId}</c> scope still cannot collide with
    /// the other participant's Nth message.
    /// </summary>
    private static string ResolvePrincipal(HttpContext context)
    {
        var user = context.User;
        if (user?.Identity?.IsAuthenticated == true)
        {
            var claim = user.FindFirst("sid")?.Value
                ?? user.FindFirst("sub")?.Value
                ?? user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
            if (!string.IsNullOrWhiteSpace(claim)) return claim;
        }

        return context.Request.Headers.TryGetValue("X-User-Id", out var header)
            ? header.ToString()
            : string.Empty;
    }
}
