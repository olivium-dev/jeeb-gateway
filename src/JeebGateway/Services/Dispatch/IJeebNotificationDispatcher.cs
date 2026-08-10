namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Gateway notification render→dispatch primitive (JEB-1494).
///
/// Renders the legacy template envelope and submits one stable command to the
/// notification-service owner. No delivery state is retained in the gateway.
/// </summary>
public interface IJeebNotificationDispatcher
{
    /// <summary>
    /// Dispatches a notification asynchronously.
    /// </summary>
    /// <param name="templateKey">Template identifier (e.g. <c>jeeb.request.received</c>).</param>
    /// <param name="locale">BCP-47 locale tag (e.g. <c>en</c>, <c>ar</c>).</param>
    /// <param name="parameters">Template substitution parameters.</param>
    /// <param name="recipientUserId">Recipient user identifier.</param>
    /// <param name="idempotencyKey">Optional caller-supplied idempotency key; duplicate calls with the same key are silently deduplicated.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The dispatch result including status.</returns>
    Task<NotificationDispatchResult> DispatchAsync(
        string templateKey,
        string locale,
        Dictionary<string, string> parameters,
        Guid recipientUserId,
        string? idempotencyKey = null,
        CancellationToken ct = default);
}

public sealed record NotificationDispatchResult(
    Guid EntryId,
    bool WasDeduplicated,
    NotificationDispatchStatus Status,
    string? Error = null);
