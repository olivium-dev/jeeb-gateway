using System;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Notifications;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// Shared test double base for <see cref="INotificationRecordWriter"/>.
///
/// <para><b>Why every member throws by default.</b> b02 step 6a took the interface from two members
/// to eight. A base that returned a benign
/// <see cref="NotificationRecordWriteClassification.Disabled"/> for the members a given test does
/// not override would make "no row was written" the silent default — and "no row was written" is
/// precisely the bug these tests exist to catch. Throwing means a test that reaches an unexpected
/// writer fails loudly and names the method, instead of passing for the wrong reason.</para>
///
/// <para>Each fake overrides only the members its scenario legitimately exercises. Adding a ninth
/// notification type therefore does not require touching any existing fake, which is the other
/// reason this base exists: three separate fakes each grew six stub methods otherwise.</para>
/// </summary>
public abstract class FakeNotificationRecordWriterBase : INotificationRecordWriter
{
    public virtual Task<NotificationRecordWriteOutcome> WriteOfferReceivedAsync(
        OfferReceivedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteOfferReceivedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteOfferAcceptedAsync(
        OfferAcceptedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteOfferAcceptedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteDeliveryStatusUpdatedAsync(
        DeliveryStatusUpdatedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteDeliveryStatusUpdatedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteSettlementPaidAsync(
        SettlementPaidNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteSettlementPaidAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteKycApprovedAsync(
        KycApprovedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteKycApprovedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteKycRejectedAsync(
        KycRejectedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteKycRejectedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteDisputeResolvedAsync(
        DisputeResolvedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteDisputeResolvedAsync));

    public virtual Task<NotificationRecordWriteOutcome> WriteRatingAutoRevealedAsync(
        RatingAutoRevealedNotificationRecord record,
        CancellationToken requestToken)
        => throw Unexpected(nameof(WriteRatingAutoRevealedAsync));

    private static NotSupportedException Unexpected(string member)
        => new($"{member} was called on a fake that does not expect it.");
}
