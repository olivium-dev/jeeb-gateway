namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Gateway notification render→dispatch primitive (JEB-1494).
///
/// <para>Encapsulates the full outbound notification pipeline:
///   1. Idempotency check against the outbox.
///   2. Persist job to the outbox (durable in Postgres, in-memory for MVP).
///   3. Render the template via <see cref="INotificationTemplateRenderer"/>.
///   4. Dispatch through <see cref="JeebGateway.Push.IPushNotificationService"/>
///      (preference filtering, device resolution, transport fan-out, retry).
///   5. On success mark the outbox entry Delivered; on failure schedule a
///      retry up to <c>MaxAttempts</c> then move to DLQ.
/// </para>
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
