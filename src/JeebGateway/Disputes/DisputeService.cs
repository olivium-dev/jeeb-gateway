using JeebGateway.Notifications;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Disputes;

public class DisputeService : IDisputeService
{
    public const int MaxPhotos = 3;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxResolutionLength = 2_000;

    private static readonly string[] AllowedPhotoSchemes = { "https://", "http://", "s3://" };

    private readonly IDisputeStore _store;
    private readonly IGenericEventDispatcher _events;
    private readonly TimeProvider _clock;
    private readonly ILogger<DisputeService> _log;

    public DisputeService(
        IDisputeStore store,
        IGenericEventDispatcher events,
        TimeProvider clock,
        ILogger<DisputeService> log)
    {
        _store = store;
        _events = events;
        _clock = clock;
        _log = log;
    }

    public async Task<Dispute> FileAsync(FileDisputeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var category = (input.Category ?? string.Empty).Trim();
        if (!DisputeCategory.IsValid(category))
        {
            throw new DisputeValidationException(
                $"category must be one of: {string.Join(", ", DisputeCategory.All)}.");
        }

        var description = (input.Description ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(description))
        {
            throw new DisputeValidationException("description is required.");
        }
        if (description.Length > MaxDescriptionLength)
        {
            throw new DisputeValidationException(
                $"description must be {MaxDescriptionLength} characters or fewer.");
        }

        var photos = NormalisePhotos(input.PhotoUrls);

        // One open dispute per delivery so the admin queue cannot accumulate
        // duplicates from a frustrated user mashing the submit button.
        var existing = await _store.GetOpenForDeliveryAsync(input.DeliveryId, ct);
        if (existing is not null)
        {
            throw new DisputeConflictException(
                $"delivery {input.DeliveryId} already has an open dispute ({existing.Id}).");
        }

        var dispute = new Dispute
        {
            Id = $"dsp_{Guid.NewGuid():N}",
            DeliveryId = input.DeliveryId,
            FiledByUserId = input.FiledByUserId,
            Category = category,
            Description = description,
            PhotoUrls = photos,
            State = DisputeState.Filed,
            FiledAt = _clock.GetUtcNow()
        };

        var saved = await _store.AddAsync(dispute, ct);

        // Best-effort for the ROW, not for the alarm: a lost hand-over is Error-logged
        // and counted by PushHandover, never swallowed.
        await SendFiledPushAsync(saved, ct);

        return saved;
    }

    public Task<Dispute?> GetAsync(string disputeId, CancellationToken ct) =>
        _store.GetByIdAsync(disputeId, ct);

    public Task<IReadOnlyList<Dispute>> ListForUserAsync(string userId, CancellationToken ct) =>
        _store.ListForUserAsync(userId, ct);

    public async Task<Dispute?> ResolveAsync(string disputeId, ResolveDisputeInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var existing = await _store.GetByIdAsync(disputeId, ct);
        if (existing is null) return null;

        var target = TargetStateFor(input.Action);
        if (!DisputeState.CanTransition(existing.State, target))
        {
            throw new DisputeConflictException(
                $"dispute {disputeId} cannot transition from '{existing.State}' to '{target}'.");
        }

        var resolution = (input.Resolution ?? string.Empty).Trim();
        if (input.Action != DisputeResolveAction.Open)
        {
            if (string.IsNullOrEmpty(resolution))
            {
                throw new DisputeValidationException(
                    "resolution is required when resolving or dismissing a dispute.");
            }
            if (resolution.Length > MaxResolutionLength)
            {
                throw new DisputeValidationException(
                    $"resolution must be {MaxResolutionLength} characters or fewer.");
            }
        }

        var updated = await _store.UpdateStateAsync(disputeId, new DisputeStatePatch
        {
            State = target,
            ReviewedAt = _clock.GetUtcNow(),
            ResolverAdminId = input.AdminUserId,
            Resolution = string.IsNullOrEmpty(resolution) ? existing.Resolution : resolution
        }, ct);

        if (updated is null) return null;

        await SendResolutionPushAsync(updated, input.Action, ct);
        return updated;
    }

    private static string TargetStateFor(DisputeResolveAction action) => action switch
    {
        DisputeResolveAction.Open => DisputeState.UnderReview,
        DisputeResolveAction.Resolve => DisputeState.Resolved,
        DisputeResolveAction.Dismiss => DisputeState.Dismissed,
        _ => throw new DisputeValidationException($"Unsupported action: {action}.")
    };

    private static IReadOnlyList<string> NormalisePhotos(IReadOnlyList<string>? raw)
    {
        if (raw is null || raw.Count == 0) return Array.Empty<string>();
        if (raw.Count > MaxPhotos)
        {
            throw new DisputeValidationException(
                $"a maximum of {MaxPhotos} photo URLs is allowed per dispute.");
        }

        var cleaned = new List<string>(raw.Count);
        foreach (var entry in raw)
        {
            if (string.IsNullOrWhiteSpace(entry)) continue;
            var url = entry.Trim();
            if (!AllowedPhotoSchemes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
            {
                throw new DisputeValidationException(
                    $"photo URL '{url}' must start with https://, http://, or s3://.");
            }
            cleaned.Add(url);
        }
        return cleaned;
    }

    private Task SendFiledPushAsync(Dispute dispute, CancellationToken ct)
    {
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "dispute",
            ["dispute_id"] = dispute.Id,
            ["delivery_id"] = dispute.DeliveryId,
            ["dispute_state"] = dispute.State,
            ["dispute_category"] = dispute.Category
        };

        // The pre-existing idempotency key IS the entity id; no new key is minted.
        return PushHandover.DispatchAsync(
            _events, _log,
            JeebGenericEventTypes.DisputeUpdateEventType,
            dispute.FiledByUserId,
            $"dispute:{dispute.Id}:filed",
            "Dispute filed",
            "We received your dispute and a reviewer will follow up shortly.",
            data,
            PushSilencePolicy.CategoryDispute,
            ct);
    }

    private Task SendResolutionPushAsync(
        Dispute dispute, DisputeResolveAction action, CancellationToken ct)
    {
        var (title, body) = action switch
        {
            DisputeResolveAction.Open => (
                "Dispute under review",
                "Your dispute is now under review by our support team."),
            DisputeResolveAction.Resolve => (
                "Dispute resolved",
                string.IsNullOrEmpty(dispute.Resolution)
                    ? "Your dispute has been resolved."
                    : $"Your dispute has been resolved: {dispute.Resolution}"),
            DisputeResolveAction.Dismiss => (
                "Dispute closed",
                string.IsNullOrEmpty(dispute.Resolution)
                    ? "Your dispute was reviewed and closed without further action."
                    : $"Your dispute was closed: {dispute.Resolution}"),
            _ => ("Dispute updated", "Your dispute status has changed.")
        };

        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["type"] = "dispute",
            ["dispute_id"] = dispute.Id,
            ["delivery_id"] = dispute.DeliveryId,
            ["dispute_state"] = dispute.State
        };

        // The pre-existing idempotency key IS the entity id; no new key is minted.
        return PushHandover.DispatchAsync(
            _events, _log,
            JeebGenericEventTypes.DisputeUpdateEventType,
            dispute.FiledByUserId,
            $"dispute:{dispute.Id}:{dispute.State}",
            title,
            body,
            data,
            PushSilencePolicy.CategoryDispute,
            ct);
    }
}
