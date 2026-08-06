using System.Text;

namespace JeebGateway.StateService.Idempotency;

/// <summary>
/// Gateway-wide <c>Idempotency-Key</c> handler (R1 / JEB-1493). For any
/// mutating request (POST/PUT/PATCH/DELETE) that carries an
/// <c>Idempotency-Key</c> header, the first execution's response is captured
/// and persisted to jeeb-state-service; any replay with the same key returns
/// the ORIGINAL response verbatim without re-invoking the endpoint. This makes
/// a double-tapped <c>POST /requests</c> create exactly one order.
///
/// Durability is owned by jeeb-state-service (<c>ON CONFLICT (key)</c>), so the
/// guarantee survives a stop-first gateway bounce. The gateway holds no state.
/// </summary>
public sealed class IdempotencyMiddleware
{
    private const string HeaderName = "Idempotency-Key";
    private const int DefaultTtlSeconds = 24 * 60 * 60; // 24h dedup window
    private const int MaxKeyLength = 200;

    private static readonly HashSet<string> MutatingMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete
    };

    // JEBV4-45 (PP-8): per-key serialization for concurrent same-key requests.
    // The lookup→execute→persist sequence below is check-then-act: two TRULY
    // concurrent requests with the same key could both miss GetAsync, both run
    // the endpoint (double side-effects — e.g. two rows in IRequestsStore even
    // though PutOrGetAsync deduped the HTTP responses afterwards), which is the
    // confirmed server-side create double-submit defect (M5#9). Same-key
    // requests within one replica now serialize on a striped semaphore so the
    // loser's GetAsync observes the winner's persisted response and replays it
    // WITHOUT re-invoking the endpoint. Striping (256 slots by key hash) keeps
    // memory bounded; unrelated keys colliding on a stripe merely serialize —
    // they never share responses (the store lookup is still exact-key).
    // Cross-replica the durable store's ON CONFLICT semantics remain the
    // authority; this closes the dominant same-replica double-tap/retry race.
    private static readonly SemaphoreSlim[] KeyStripes = CreateKeyStripes();

    private static SemaphoreSlim[] CreateKeyStripes()
    {
        var stripes = new SemaphoreSlim[256];
        for (var i = 0; i < stripes.Length; i++) stripes[i] = new SemaphoreSlim(1, 1);
        return stripes;
    }

    private static SemaphoreSlim StripeFor(string key)
    {
        // Stable, non-randomized hash so the stripe choice is deterministic
        // for a given key within the process lifetime.
        var hash = 17;
        foreach (var ch in key) hash = unchecked(hash * 31 + ch);
        return KeyStripes[(hash & 0x7fffffff) % KeyStripes.Length];
    }

    private readonly RequestDelegate _next;
    private readonly ILogger<IdempotencyMiddleware> _logger;

    public IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context, IIdempotencyStore store)
    {
        if (!ShouldApply(context, out var clientKey))
        {
            await _next(context);
            return;
        }

        // JEBV4-335: the chat SEND key is a per-mount client counter that restarts
        // at 0, so a re-entered thread re-presents keys we already stored and this
        // middleware replayed a cached 201 without ever forwarding the message.
        // Bind the key to a fingerprint of THIS request so a distinct send can
        // never inherit another send's stored response. If the request cannot be
        // fingerprinted we FAIL OPEN and forward — a duplicate bubble is cosmetic,
        // a dropped message is data loss. See ChatSendIdempotencyGuard.
        if (ChatSendIdempotencyGuard.AppliesTo(context.Request))
        {
            var guarded = await ChatSendIdempotencyGuard.TryDisambiguateAsync(context, clientKey!);
            if (guarded is null)
            {
                _logger.LogWarning(
                    "Chat send key {Key} could not be collision-guarded; forwarding WITHOUT dedup (fail-open)",
                    clientKey);
                await _next(context);
                return;
            }

            context.Items[ChatSendIdempotencyGuard.EffectiveKeyItem] = guarded;
            clientKey = guarded;
        }

        var key = ScopeKey(context, clientKey!);

        // JEBV4-45: serialize same-key concurrent requests (see KeyStripes doc).
        var stripe = StripeFor(key);
        await stripe.WaitAsync(context.RequestAborted);
        try
        {
            await InvokeSerializedAsync(context, store, key);
        }
        finally
        {
            stripe.Release();
        }
    }

    private async Task InvokeSerializedAsync(HttpContext context, IIdempotencyStore store, string key)
    {
        // 1. Fast replay path: if we've already stored a response, return it.
        IdempotencyOutcome? existing;
        try
        {
            existing = await store.GetAsync(key!, context.RequestAborted);
        }
        catch (Exception ex)
        {
            // State-service unreachable / breaker open → fail open (do not
            // block the request). Degraded, not down. (ADR-001-rev2.)
            _logger.LogWarning(ex, "Idempotency lookup failed for key {Key}; proceeding without dedup", key);
            await _next(context);
            return;
        }

        if (existing is not null)
        {
            await ReplayAsync(context, existing);
            return;
        }

        // 2. First execution: buffer the response so we can persist it.
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await _next(context);
        }
        finally
        {
            context.Response.Body = originalBody;
        }

        buffer.Position = 0;
        var bodyBytes = buffer.ToArray();

        // Only persist successful, deterministic responses (2xx). Errors are
        // not deduped — the caller should be able to retry a failed attempt.
        if (context.Response.StatusCode is >= 200 and < 300)
        {
            var bodyJson = Encoding.UTF8.GetString(bodyBytes);
            try
            {
                var outcome = await store.PutOrGetAsync(
                    key!, context.Response.StatusCode, bodyJson, DefaultTtlSeconds, context.RequestAborted);

                // Lost the race: another concurrent caller stored first. Replay
                // THAT original so both callers observe the same single effect.
                if (!outcome.Inserted)
                {
                    _logger.LogInformation("Idempotency race on key {Key}; replaying stored original", key);
                    await OverwriteWithStoredAsync(context, originalBody, outcome);
                    return;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Idempotency persist failed for key {Key}; returning live response", key);
            }
        }

        // Flush the (newly produced) response body to the real stream.
        await originalBody.WriteAsync(bodyBytes, context.RequestAborted);
    }

    /// <summary>
    /// Extracts the raw CLIENT-supplied key. Scoping is applied separately by
    /// <see cref="ScopeKey"/> so a per-endpoint collision guard (JEBV4-335) can
    /// rewrite the client key before it is scoped and persisted.
    /// </summary>
    private static bool ShouldApply(HttpContext context, out string? clientKey)
    {
        clientKey = null;
        if (!MutatingMethods.Contains(context.Request.Method)) return false;
        // CDN upload tickets use an explicit state-backed reserve/result protocol
        // whose TTL is bounded by the signed ticket. The generic middleware's
        // 24-hour check-then-act cache is both too long and not principal-scoped.
        if (HttpMethods.IsPost(context.Request.Method)
            && context.Request.Path.Equals("/api/cdn/assets", StringComparison.OrdinalIgnoreCase))
            return false;
        // Generic case mutations already use jeeb-state-service's transactional
        // idempotency contract, which binds the key to the complete request hash
        // and returns 409 when a caller reuses a key with different input. The
        // gateway-wide response cache is key-only; allowing it to intercept these
        // routes would replay the first 2xx response before state-service can detect
        // that conflict.
        if (IsCaseMutation(context.Request.Path)) return false;
        if (!context.Request.Headers.TryGetValue(HeaderName, out var values)) return false;

        var candidate = values.ToString();
        if (string.IsNullOrWhiteSpace(candidate) || candidate.Length > MaxKeyLength) return false;

        clientKey = candidate;
        return true;
    }

    internal static bool IsCaseMutation(PathString path)
    {
        var value = path.Value ?? string.Empty;
        return value.Equals("/v1/disputes", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/v1/disputes/", StringComparison.OrdinalIgnoreCase)
               || value.Equals("/v1/support/tickets", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/v1/support/tickets/", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/v1/deliveries/", StringComparison.OrdinalIgnoreCase)
                  && value.EndsWith("/escalate", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/admin/v1/cases/", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/admin/v1/disputes/", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/admin/v1/support/tickets/", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/deliveries/", StringComparison.OrdinalIgnoreCase)
                  && value.EndsWith("/dispute", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("/admin/disputes/", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Scope the key by method+path so the same client key on two different
    /// endpoints cannot collide — but the persisted key must be SLASH-FREE
    /// because the state-service exposes GET /idempotency/{key} and a raw
    /// request path ("/prohibited-items/scan") would break path routing.
    /// We therefore hash the {method}:{path} scope into a compact, URL-safe
    /// prefix and append the client-supplied key.
    /// </summary>
    private static string ScopeKey(HttpContext context, string clientKey)
    {
        var scope = ScopeHash($"{context.Request.Method}:{context.Request.Path}");
        return $"{scope}.{clientKey}";
    }

    private static string ScopeHash(string scope)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(scope);
        Span<byte> hash = stackalloc byte[32];
        System.Security.Cryptography.SHA256.HashData(bytes, hash);
        // base64url, no padding/slashes — first 16 chars are plenty to scope.
        return Convert.ToBase64String(hash)
            .TrimEnd('=').Replace('+', '-').Replace('/', '_')[..16];
    }

    private static async Task ReplayAsync(HttpContext context, IdempotencyOutcome outcome)
    {
        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Idempotency-Replayed"] = "true";
        ReinstateLocation(context, outcome);
        await context.Response.WriteAsync(outcome.ResponseBodyJson, context.RequestAborted);
    }

    private static async Task OverwriteWithStoredAsync(
        HttpContext context, Stream realBody, IdempotencyOutcome outcome)
    {
        context.Response.Body = realBody;
        context.Response.StatusCode = outcome.StatusCode;
        context.Response.ContentType = "application/json";
        context.Response.Headers["Idempotency-Replayed"] = "true";
        ReinstateLocation(context, outcome);
        await context.Response.WriteAsync(outcome.ResponseBodyJson, context.RequestAborted);
    }

    /// <summary>
    /// WS-08 hardening: the persisted idempotency record stores only status + body
    /// (the state-service schema is a GATED contract — WS-13), so a <c>201 Created</c>
    /// replay would otherwise drop its <c>Location</c> header and break a
    /// create-then-locate client flow. We reconstruct <c>Location</c> for 201 replays
    /// from the response body's <c>$.id</c> (the org's canonical create-response field
    /// — see the offer/accept DTO contract). This is a pure replay-path concern; the
    /// first execution already carries the controller's real <c>Location</c> verbatim.
    /// </summary>
    private static void ReinstateLocation(HttpContext context, IdempotencyOutcome outcome)
    {
        if (outcome.StatusCode != StatusCodes.Status201Created) return;
        if (context.Response.Headers.ContainsKey("Location")) return;

        var id = TryReadId(outcome.ResponseBodyJson);
        if (string.IsNullOrEmpty(id)) return;

        var path = context.Request.Path.Value?.TrimEnd('/') ?? string.Empty;
        context.Response.Headers["Location"] = $"{path}/{id}";
    }

    private static string? TryReadId(string bodyJson)
    {
        if (string.IsNullOrWhiteSpace(bodyJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(bodyJson);
            if (doc.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) return null;
            // Accept the canonical "id" plus a couple of historical aliases, so a
            // create response shaped {id} | {orderId} | {requestId} all locate.
            foreach (var name in new[] { "id", "orderId", "requestId" })
            {
                if (doc.RootElement.TryGetProperty(name, out var prop) &&
                    prop.ValueKind is System.Text.Json.JsonValueKind.String or System.Text.Json.JsonValueKind.Number)
                {
                    return prop.ToString();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Non-JSON body — nothing to locate. Replay status+body unchanged.
        }
        return null;
    }
}
