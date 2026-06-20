using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using JeebGateway.Ratings.Jeeb;
using JeebGateway.service.ServiceFeedback;
using Microsoft.Extensions.DependencyInjection;
using FeedbackApiException = JeebGateway.service.ServiceFeedback.ApiException;

namespace JeebGateway.Ratings;

/// <summary>
/// Gap 8 (JEB-1489 / GR2) — feedback-service-backed implementation of
/// <see cref="IRatingStore"/>. Becomes the delivery-ratings record-of-truth when
/// <c>FeatureFlags:UseUpstream:Ratings</c> is ON; otherwise
/// <see cref="InMemoryRatingStore"/> remains the flag-OFF rollback fallback
/// (the <see cref="JeebGateway.Requests.Cancellation.BanServiceJeeberRestrictionStore"/>
/// store-swap precedent).
///
/// <para><b>Division of labour.</b> The shared feedback-service owns ONLY the
/// generic, product-agnostic two-party blind-rating primitive: opaque
/// <c>(correlationId, raterId, rateeId, score, comment, tags[])</c> rows with
/// idempotency on <c>(correlationId, raterId)</c> and a mutual reveal projection.
/// Everything Jeeb-specific — the dual-party Sami/Kamal vocabulary, the
/// <c>jeeb:delivery:*</c> correlationId linkage, the partition + role tag stamping,
/// and the 7-day blind/reveal window state machine — stays HERE in the gateway
/// (<see cref="JeebRatingVocabulary"/> + <see cref="BlindRevealPolicy"/>). This
/// store never leaks Jeeb semantics downstream; it only translates the gateway's
/// <see cref="RatingPair"/> model to/from the opaque upstream surface.</para>
///
/// <para><b>Local party/anchor map.</b> The upstream is keyed by an opaque
/// correlationId and stores neither the Jeeb party ids nor the delivered-at
/// anchor the rating window pivots on. Mirroring the in-memory store's row, this
/// store keeps a per-delivery seed (<see cref="EnsureAsync"/>) of
/// <c>(clientId, jeeberId, deliveredAt)</c> so that (a) Submit-without-Ensure
/// throws <see cref="InvalidOperationException"/> exactly like
/// <see cref="InMemoryRatingStore"/>, and (b) reveal results can be projected
/// back onto the correct Sami/Kamal sides with the original window anchor.</para>
///
/// <para>The injected <see cref="ServiceFeedbackClient"/> is resolved per call
/// from an <see cref="IServiceScopeFactory"/> so this singleton store does not
/// capture a scoped/typed HttpClient.</para>
/// </summary>
public sealed class FeedbackServiceRatingStore : IRatingStore
{
    /// <summary>
    /// The fixed namespace for the deterministic RFC-4122 v5-style derivation of a
    /// stable <see cref="Guid"/> from a non-GUID Jeeb user id. Kept gateway-side so
    /// the opaque rater/ratee ids the shared service stores are reproducible for the
    /// SAME Jeeb user across both the /v1/ratings/jeeb/* surface and this store.
    /// </summary>
    private static readonly Guid JeebRatingIdNamespace =
        new("3f2c8d6e-4b1a-4f7c-9e2d-1a6b5c4d3e2f");

    private readonly IServiceScopeFactory _scopeFactory;

    // Per-delivery party/anchor seed — the gateway-side row the upstream does not
    // hold. Keyed by deliveryId, ordinal, as in InMemoryRatingStore.
    private readonly ConcurrentDictionary<string, RatingPair> _seeds =
        new(StringComparer.Ordinal);

    public FeedbackServiceRatingStore(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<RatingPair?> GetAsync(string deliveryId, CancellationToken ct)
    {
        if (!_seeds.TryGetValue(deliveryId, out var seed))
        {
            // Unknown delivery — no party map to project a reveal onto.
            return null;
        }

        var correlationId = JeebRatingVocabulary.CorrelationForDelivery(deliveryId);

        // Read the upstream reveal as the CLIENT (Sami) viewer: Self => client side,
        // Counterparty => jeeber side. This fixes a stable projection of the
        // symmetric upstream state onto the gateway's asymmetric Client/Jeeber pair.
        var viewerId = StableGuid(seed.ClientId);

        BlindRevealStateResponse? reveal;
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ServiceFeedbackClient>();
            reveal = await client.RatingsRevealAsync(correlationId, viewerId, ct).ConfigureAwait(false);
        }
        catch (FeedbackApiException ex) when (ex.StatusCode is 404)
        {
            // No upstream row yet — neither side has submitted. Return the seeded
            // pair with both sides null so BlindRevealPolicy projects PendingMine.
            reveal = null;
        }

        var pair = new RatingPair
        {
            DeliveryId = seed.DeliveryId,
            ClientId = seed.ClientId,
            JeeberId = seed.JeeberId,
            DeliveredAt = seed.DeliveredAt,
        };

        if (reveal is not null)
        {
            // Self => client (the viewer); Counterparty => jeeber.
            pair.ClientRating = ToEntry(reveal.Self, seed.ClientId, seed.DeliveredAt);
            pair.JeeberRating = ToEntry(reveal.Counterparty, seed.JeeberId, seed.DeliveredAt);
        }

        return pair;
    }

    public Task<RatingPair> EnsureAsync(
        string deliveryId,
        string clientId,
        string jeeberId,
        DateTimeOffset deliveredAt,
        CancellationToken ct)
    {
        // Idempotent seed: capture party ids + delivered-at on FIRST call only so
        // the rating-window anchor is stable (parity with InMemoryRatingStore).
        var seed = _seeds.GetOrAdd(deliveryId, _ => new RatingPair
        {
            DeliveryId = deliveryId,
            ClientId = clientId,
            JeeberId = jeeberId,
            DeliveredAt = deliveredAt,
        });
        return Task.FromResult(seed);
    }

    public async Task<RatingPair> SubmitAsync(
        string deliveryId,
        bool callerIsClient,
        RatingEntry entry,
        CancellationToken ct)
    {
        if (!_seeds.TryGetValue(deliveryId, out var seed))
        {
            // Parity with InMemoryRatingStore: a submit before the row is seeded is
            // a caller error, not an upstream round trip.
            throw new InvalidOperationException(
                $"Rating row for delivery {deliveryId} has not been initialised. Call EnsureAsync first.");
        }

        var role = JeebRatingVocabulary.RoleFor(callerIsClient);
        var raterId = callerIsClient ? seed.ClientId : seed.JeeberId;
        var rateeId = callerIsClient ? seed.JeeberId : seed.ClientId;

        var request = new SubmitBlindRatingRequest
        {
            CorrelationId = JeebRatingVocabulary.CorrelationForDelivery(deliveryId),
            RaterId = StableGuid(raterId),
            RateeId = StableGuid(rateeId),
            Score = entry.Stars,
            Comment = entry.Comment,
            Tags = JeebRatingVocabulary.BuildTags(role, requestedTags: null),
        };

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var client = scope.ServiceProvider.GetRequiredService<ServiceFeedbackClient>();
            await client.RatingsSubmitAsync(request, ct).ConfigureAwait(false);
        }
        catch (FeedbackApiException ex) when (ex.StatusCode is 400 or 404 or 409)
        {
            // Upstream idempotency rejection of a second submit for the same
            // (correlationId, raterId). Maps to the in-memory store's "already
            // rated" InvalidOperationException so RatingService -> AlreadyRated.
            throw new InvalidOperationException(
                callerIsClient
                    ? $"Client has already rated delivery {deliveryId}."
                    : $"Jeeber has already rated delivery {deliveryId}.",
                ex);
        }

        // Reflect the just-submitted side locally so a subsequent GetAsync before
        // upstream propagation still projects the caller's own rating.
        if (callerIsClient)
        {
            seed.ClientRating = entry;
        }
        else
        {
            seed.JeeberRating = entry;
        }

        return seed;
    }

    /// <summary>
    /// Deterministically derive a stable <see cref="Guid"/> for a Jeeb user id.
    /// A caller that already passes a real GUID round-trips unchanged; any other
    /// id is mapped via an RFC-4122 v5-style (SHA-1, name-based) derivation under
    /// <see cref="JeebRatingIdNamespace"/>, so the SAME Jeeb user always yields the
    /// SAME opaque rater/ratee id upstream.
    /// </summary>
    public static Guid StableGuid(string userId)
    {
        if (string.IsNullOrWhiteSpace(userId))
            throw new ArgumentException("userId is required.", nameof(userId));

        if (Guid.TryParse(userId, out var real))
        {
            return real;
        }

        Span<byte> namespaceBytes = stackalloc byte[16];
        WriteGuidBigEndian(JeebRatingIdNamespace, namespaceBytes);

        var nameBytes = Encoding.UTF8.GetBytes(userId);
        var input = new byte[namespaceBytes.Length + nameBytes.Length];
        namespaceBytes.CopyTo(input);
        Buffer.BlockCopy(nameBytes, 0, input, namespaceBytes.Length, nameBytes.Length);

        var hash = SHA1.HashData(input);

        Span<byte> guidBytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(guidBytes);

        // Version 5 (name-based, SHA-1) + RFC-4122 variant bits.
        guidBytes[6] = (byte)((guidBytes[6] & 0x0F) | 0x50);
        guidBytes[8] = (byte)((guidBytes[8] & 0x3F) | 0x80);

        return ReadGuidBigEndian(guidBytes);
    }

    private static RatingEntry? ToEntry(
        BlindRatingPartyState? party, string authorUserId, DateTimeOffset fallbackSubmittedAt)
    {
        if (party is null || !party.Submitted || party.Score is not int score)
        {
            return null;
        }

        return new RatingEntry(
            AuthorUserId: authorUserId,
            Stars: score,
            Comment: party.Comment,
            SubmittedAt: party.SubmittedAt ?? fallbackSubmittedAt);
    }

    // Guid byte order on little-endian platforms differs from the RFC-4122 wire
    // order; normalise to big-endian for the name-based hash and back again so the
    // derivation is platform-stable.
    private static void WriteGuidBigEndian(Guid value, Span<byte> destination)
    {
        value.TryWriteBytes(destination);
        SwapGuidEndianness(destination);
    }

    private static Guid ReadGuidBigEndian(Span<byte> source)
    {
        SwapGuidEndianness(source);
        return new Guid(source);
    }

    private static void SwapGuidEndianness(Span<byte> bytes)
    {
        (bytes[0], bytes[3]) = (bytes[3], bytes[0]);
        (bytes[1], bytes[2]) = (bytes[2], bytes[1]);
        (bytes[4], bytes[5]) = (bytes[5], bytes[4]);
        (bytes[6], bytes[7]) = (bytes[7], bytes[6]);
    }
}
