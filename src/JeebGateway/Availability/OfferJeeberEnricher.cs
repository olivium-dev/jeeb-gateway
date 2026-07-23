using FeedbackClient = JeebGateway.service.ServiceFeedback.ServiceFeedbackClient;
using UserManagementClient = JeebGateway.service.ServiceUserManagement.ServiceUserManagementClient;
using UserManagementProfile = JeebGateway.service.ServiceUserManagement.UserProfileResponse;

namespace JeebGateway.Availability;

/// <summary>
/// Stateless BFF aggregation for the CLIENT-owned offers list. Offer-service
/// remains the bid source of truth; user-management supplies display identity
/// and feedback-service supplies the revealed-review aggregate.
/// </summary>
public interface IOfferJeeberEnricher
{
    Task<IReadOnlyList<OfferDto>> EnrichAsync(
        IReadOnlyList<PendingOffer> offers,
        CancellationToken ct);
}

/// <inheritdoc />
public sealed class OfferJeeberEnricher : IOfferJeeberEnricher
{
    private const int AggregateProbeLength = 1;
    private const int AggregateProbeOffset = 0;
    private const int NoRatingFilter = 0;

    private readonly UserManagementClient _profiles;
    private readonly FeedbackClient _reviews;
    private readonly ILogger<OfferJeeberEnricher> _logger;

    public OfferJeeberEnricher(
        UserManagementClient profiles,
        FeedbackClient reviews,
        ILogger<OfferJeeberEnricher> logger)
    {
        _profiles = profiles;
        _reviews = reviews;
        _logger = logger;
    }

    public async Task<IReadOnlyList<OfferDto>> EnrichAsync(
        IReadOnlyList<PendingOffer> offers,
        CancellationToken ct)
    {
        if (offers.Count == 0)
        {
            return Array.Empty<OfferDto>();
        }

        var jeeberIds = offers
            .Select(o => o.JeeberId)
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var detailTasks = jeeberIds.Select(id => ResolveAsync(id, ct));
        var details = (await Task.WhenAll(detailTasks))
            .ToDictionary(d => d.JeeberId, StringComparer.Ordinal);

        return offers.Select(o =>
        {
            details.TryGetValue(o.JeeberId, out var detail);
            return ToDto(o, detail);
        }).ToList();
    }

    private async Task<JeeberDetails> ResolveAsync(string jeeberId, CancellationToken ct)
    {
        var profileTask = ResolveCanonicalProfileAsync(jeeberId, ct);
        var ratingTask = ResolveRatingAsync(jeeberId, ct);
        await Task.WhenAll(profileTask, ratingTask);

        var profile = await profileTask;
        var rating = await ratingTask;

        return new JeeberDetails(
            jeeberId,
            Clean(profile?.Username),
            Clean(profile?.ProfilePic),
            rating.Average,
            rating.Count);
    }

    private async Task<UserManagementProfile?> ResolveCanonicalProfileAsync(
        string jeeberId,
        CancellationToken ct)
    {
        try
        {
            return await _profiles.ProfileAsync(jeeberId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Offer-list profile enrichment failed for Jeeber {JeeberId}; identity fields stay null.",
                jeeberId);
            return null;
        }
    }

    private async Task<RatingLookup> ResolveRatingAsync(
        string jeeberId,
        CancellationToken ct)
    {
        try
        {
            // The existing Jeeb reviews BFF uses this same generic per-tag read.
            // Length=1 keeps the aggregate lookup cheap; totalReviewCount and
            // averageRating describe the full revealed-review set.
            var aggregate = await _reviews.CommentGETAsync(
                jeeberId,
                AggregateProbeLength,
                AggregateProbeOffset,
                NoRatingFilter,
                ct);

            return RatingLookup.Available(
                aggregate.AverageRating,
                aggregate.TotalReviewCount);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Offer-list rating enrichment failed for Jeeber {JeeberId}; rating stays null with count zero.",
                jeeberId);
            return RatingLookup.Unavailable;
        }
    }

    private static string? Clean(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OfferDto ToDto(PendingOffer offer, JeeberDetails? detail) => new()
    {
        Id = offer.Id,
        RequestId = offer.RequestId,
        JeeberId = offer.JeeberId,
        JeeberName = detail?.Name,
        JeeberAvatarUrl = detail?.AvatarUrl,
        Rating = detail?.Rating,
        RatingCount = detail?.RatingCount ?? 0,
        Status = offer.Status,
        Fee = offer.Fee,
        EtaMinutes = offer.EtaMinutes,
        Note = offer.Note,
        CreatedAt = offer.CreatedAt,
        UpdatedAt = offer.UpdatedAt,
    };

    private sealed record JeeberDetails(
        string JeeberId,
        string? Name,
        string? AvatarUrl,
        double? Rating,
        int RatingCount);

    private readonly record struct RatingLookup(
        bool SourceAvailable,
        double? Average,
        int Count)
    {
        public static RatingLookup Unavailable => new(false, null, 0);

        public static RatingLookup Available(double average, int count)
        {
            var safeCount = Math.Max(0, count);
            if (safeCount == 0)
            {
                return new RatingLookup(true, null, 0);
            }

            // Reject an internally inconsistent aggregate instead of emitting a
            // non-JSON number or a fabricated/clamped score.
            if (!double.IsFinite(average) || average is < 0 or > 5)
            {
                return Unavailable;
            }

            return new RatingLookup(true, Math.Round(average, 2), safeCount);
        }
    }
}
