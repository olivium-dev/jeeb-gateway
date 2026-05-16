using System.Collections.Concurrent;

namespace JeebGateway.Kyc;

/// <summary>
/// MVP in-memory implementation of <see cref="IKycStore"/>. Mirrors the
/// lifecycle the Postgres-backed store will implement so the controller,
/// service, and tests share a single seam.
/// </summary>
public class InMemoryKycStore : IKycStore
{
    private readonly ConcurrentDictionary<string, KycSubmission> _byId = new();

    public Task<KycSubmission> AddAsync(KycSubmission submission, CancellationToken ct)
    {
        _byId[submission.Id] = submission;
        return Task.FromResult(submission);
    }

    public Task<KycSubmission?> GetLatestForUserAsync(string userId, CancellationToken ct)
    {
        var latest = _byId.Values
            .Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal))
            .OrderByDescending(s => s.SubmittedAt)
            .FirstOrDefault();
        return Task.FromResult(latest);
    }

    public Task<IReadOnlyList<KycSubmission>> ListForUserAsync(string userId, CancellationToken ct)
    {
        var items = _byId.Values
            .Where(s => string.Equals(s.UserId, userId, StringComparison.Ordinal))
            .OrderByDescending(s => s.SubmittedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<KycSubmission>>(items);
    }

    public Task<KycSubmission?> GetByIdAsync(string submissionId, CancellationToken ct)
    {
        _byId.TryGetValue(submissionId, out var submission);
        return Task.FromResult(submission);
    }

    public Task<KycQueuePage> ListPendingForReviewAsync(int page, int pageSize, CancellationToken ct)
    {
        var pending = _byId.Values
            .Where(s => string.Equals(s.Status, KycStatus.PendingReview, StringComparison.Ordinal))
            // AC #1: oldest first so reviewers drain the queue in
            // submission order. Tie-break on id to keep the page stable
            // across calls when two rows share the same timestamp.
            .OrderBy(s => s.SubmittedAt)
            .ThenBy(s => s.Id, StringComparer.Ordinal)
            .ToList();

        var total = pending.Count;
        var slice = pending
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToList();

        return Task.FromResult(new KycQueuePage
        {
            Items = slice,
            Page = page,
            PageSize = pageSize,
            Total = total
        });
    }

    public Task<KycSubmission?> ApplyReviewAsync(string submissionId, KycReviewPatch patch, CancellationToken ct)
    {
        if (!_byId.TryGetValue(submissionId, out var existing))
        {
            return Task.FromResult<KycSubmission?>(null);
        }

        existing.Status = patch.Status;
        existing.ReviewedAt = patch.ReviewedAt;
        existing.ReviewerId = patch.ReviewerId;
        existing.RejectionReason = patch.RejectionReason;
        existing.ResubmitSteps = patch.ResubmitSteps is null
            ? new List<string>()
            : patch.ResubmitSteps.ToList();
        return Task.FromResult<KycSubmission?>(existing);
    }
}
