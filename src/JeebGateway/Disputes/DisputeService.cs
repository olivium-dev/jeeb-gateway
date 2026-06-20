using JeebGateway.Push;
using JeebGateway.StateService.Durable;
using Microsoft.Extensions.Logging;

namespace JeebGateway.Disputes;

public class DisputeService : IDisputeService
{
    public const int MaxPhotos = 3;
    public const int MaxDescriptionLength = 2_000;
    public const int MaxResolutionLength = 2_000;

    private static readonly string[] AllowedPhotoSchemes = { "https://", "http://", "s3://" };

    private readonly IDisputeStore _store;
    private readonly IStateDisputeWriter? _durableWriter;
    private readonly IPushNotificationService _push;
    private readonly TimeProvider _clock;
    private readonly ILogger<DisputeService> _log;

    public DisputeService(
        IDisputeStore store,
        IStateDisputeWriter? durableWriter,
        IPushNotificationService push,
        TimeProvider clock,
        ILogger<DisputeService> log)
    {
        _store = store;
        _durableWriter = durableWriter;
        _push = push;
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

        // Durable write-through: mirror the Open event to jeeb-state-service so the
        // row survives a gateway bounce (ADR-0001 durability, P1). Best-effort —
        // the in-memory store is the authoritative fast-read index; a state-service
        // outage must not block the user's dispute filing.
        if (_durableWriter is not null)
        {
            try
            {
                await _durableWriter.OpenAsync(saved.DeliveryId, saved.FiledByUserId, ct);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Durable dispute write failed for {DisputeId}; in-memory write succeeded", saved.Id);
            }
        }

        // Best-effort notify the filer so they know the case is in the
        // queue. Admin notification piggy-backs on the same trigger with
        // a distinct idempotency key — production wiring will route this
        // to a moderator inbox via notification-service.
        await SendBestEffortAsync(BuildFiledPush(saved), ct);

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

        await SendBestEffortAsync(BuildResolutionPush(updated, input.Action), ct);
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

    private static PushNotificationRequest BuildFiledPush(Dispute dispute) =>
        new(
            dispute.FiledByUserId,
            NotificationTrigger.StatusChange,
            "Dispute filed",
            "We received your dispute and a reviewer will follow up shortly.",
            new Dictionary<string, string>
            {
                ["dispute_id"] = dispute.Id,
                ["delivery_id"] = dispute.DeliveryId,
                ["dispute_state"] = dispute.State,
                ["dispute_category"] = dispute.Category
            },
            IdempotencyKey: $"dispute:{dispute.Id}:filed");

    private static PushNotificationRequest BuildResolutionPush(Dispute dispute, DisputeResolveAction action)
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

        return new PushNotificationRequest(
            dispute.FiledByUserId,
            NotificationTrigger.StatusChange,
            title,
            body,
            new Dictionary<string, string>
            {
                ["dispute_id"] = dispute.Id,
                ["delivery_id"] = dispute.DeliveryId,
                ["dispute_state"] = dispute.State
            },
            IdempotencyKey: $"dispute:{dispute.Id}:{dispute.State}");
    }

    private async Task SendBestEffortAsync(PushNotificationRequest request, CancellationToken ct)
    {
        try
        {
            await _push.SendAsync(request, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Push fan-out must never block the dispute mutation — the row
            // is already written and the user can read the latest state via
            // GET /disputes/{id} on next foreground.
            _log.LogWarning(ex,
                "dispute push fan-out failed for user {UserId} dispute {DisputeId} trigger {Trigger}",
                request.UserId, request.Data?["dispute_id"] ?? "?", request.Trigger);
        }
    }
}
