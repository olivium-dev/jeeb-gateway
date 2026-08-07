namespace JeebGateway.Users;

/// <summary>
/// Stateless compatibility dependency for the legacy mobile user controller.
/// Account deletion is disabled until its owning service exposes the lifecycle
/// contract; the gateway never queues or advances deletion state locally.
/// </summary>
public sealed class UnavailableAccountDeletionStore : IAccountDeletionStore
{
    public Task<AccountDeletionRequest> RequestAsync(
        string userId,
        bool hasActiveDelivery,
        CancellationToken ct) => Unsupported<AccountDeletionRequest>();

    public Task<AccountDeletionRequest?> GetAsync(string userId, CancellationToken ct) =>
        Task.FromResult<AccountDeletionRequest?>(null);

    public Task AdvanceAsync(DateTimeOffset now, CancellationToken ct) =>
        Task.FromException(new NotSupportedException(
            "Account-deletion lifecycle work belongs to its authoritative owner."));

    private static Task<T> Unsupported<T>() => Task.FromException<T>(
        new NotSupportedException("Account deletion is unavailable until an authoritative owner contract is configured."));
}
