using JeebGateway.Requests.OtpHandover;

namespace JeebGateway.Requests;

/// <summary>
/// Stateless compatibility seam used by mobile-auth controllers whose legacy
/// constructors still mention requests. Production request/domain routes are not
/// mapped by the essential profile; no request data is retained here.
/// </summary>
public sealed class UnavailableRequestsStore : IRequestsStore
{
    public Task<int> CountActiveForClientAsync(string clientId, CancellationToken ct) => Task.FromResult(0);
    public Task<int> CountActiveForJeeberAsync(string jeeberId, CancellationToken ct) => Task.FromResult(0);
    public Task<DeliveryRequest?> GetAsync(string requestId, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
    public Task<DeliveryRequest?> GetByConversationIdAsync(string conversationId, CancellationToken ct) => Task.FromResult<DeliveryRequest?>(null);
    public Task<IReadOnlyList<DeliveryRequest>> ListForClientAsync(string clientId, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListForJeeberAsync(string jeeberId, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListPendingCreatedAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListAssignedSinceAsync(DateTimeOffset since, int limit, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListScheduledDueAsync(DateTimeOffset cutoff, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListJeeberCancelledAsync(string jeeberId, CancellationToken ct) => Empty();
    public Task<IReadOnlyList<DeliveryRequest>> ListUnreachableAtOrBeforeAsync(DateTimeOffset cutoff, CancellationToken ct) => Empty();
    public Task<(IReadOnlyList<DeliveryRequest> Items, int Total)> ListPendingCancellationsAsync(int page, int pageSize, CancellationToken ct) =>
        Task.FromResult<(IReadOnlyList<DeliveryRequest>, int)>((Array.Empty<DeliveryRequest>(), 0));

    public Task<DeliveryRequest> CreateAsync(CreateRequestInput input, CancellationToken ct) => Unsupported<DeliveryRequest>();
    public Task<DeliveryRequest> TryCreateWithLimitAsync(CreateRequestInput input, int limit, CancellationToken ct) => Unsupported<DeliveryRequest>();
    public Task<bool> SetStatusAsync(string requestId, string status, CancellationToken ct) => Unsupported<bool>();
    public Task<bool> SetJeeberIdAsync(string requestId, string jeeberId, CancellationToken ct) => Unsupported<bool>();
    public Task<bool> TrySetAcceptedFeeAsync(string requestId, decimal fee, CancellationToken ct) => Unsupported<bool>();
    public Task<bool> TryExpireAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Unsupported<bool>();
    public Task<int> AnonymizeForClientAsync(string userId, string anonymizedHash, CancellationToken ct) => Unsupported<int>();
    public Task<bool> TryActivateScheduledAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Unsupported<bool>();
    public Task SetConversationIdAsync(string requestId, string conversationId, CancellationToken ct) => Task.CompletedTask;
    public Task<DeliveryRequest?> TryAcceptByJeeberAsync(string requestId, string jeeberId, int limit, DateTimeOffset at, CancellationToken ct) => Unsupported<DeliveryRequest?>();
    public Task<CancellationStoreResult?> TryCancelAsync(string requestId, IReadOnlySet<string> allowedFromStates, string targetStatus, string cancelledBy, string? reason, DateTimeOffset at, CancellationToken ct) => Unsupported<CancellationStoreResult?>();
    public Task<CancellationStoreResult?> TryDecideCancellationAsync(string requestId, bool approve, DateTimeOffset at, CancellationToken ct) => Unsupported<CancellationStoreResult?>();
    public Task<OtpVerificationResult> TryVerifyOtpAsync(string requestId, string otpCode, int maxAttempts, DateTimeOffset at, CancellationToken ct) => Unsupported<OtpVerificationResult>();
    public Task<DeliveryRequest?> MarkClientUnreachableAsync(string requestId, DateTimeOffset at, CancellationToken ct) => Unsupported<DeliveryRequest?>();
    public Task<bool> TrySetEscalationIdAsync(string requestId, string escalationId, CancellationToken ct) => Unsupported<bool>();

    private static Task<IReadOnlyList<DeliveryRequest>> Empty() =>
        Task.FromResult<IReadOnlyList<DeliveryRequest>>(Array.Empty<DeliveryRequest>());

    private static Task<T> Unsupported<T>() => Task.FromException<T>(
        new NotSupportedException("The legacy request surface is disabled in the stateless essential gateway."));
}
