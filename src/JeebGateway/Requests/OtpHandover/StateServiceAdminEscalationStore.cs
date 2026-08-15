using JeebGateway.Cases;
using JeebGateway.Services.Clients;

namespace JeebGateway.Requests.OtpHandover;

/// <summary>Admin handover escalations persisted as generic state-service cases.</summary>
public sealed class StateServiceAdminEscalationStore : IAdminEscalationStore
{
    private const string CategoryPrefix = "otp_handover:";
    private readonly IGenericCaseStateClient _owner;

    public StateServiceAdminEscalationStore(IGenericCaseStateClient owner) => _owner = owner;

    public async Task<AdminEscalation> CreateAsync(AdminEscalation entry, CancellationToken ct)
    {
        var row = await _owner.CreateCaseAsync(new CreateGenericCaseRequestV1
        {
            Kind = GenericCaseKinds.Support,
            Category = CategoryPrefix + entry.Reason,
            Subject = new GenericCaseSubjectV1 { Type = "delivery", Ref = entry.DeliveryId },
            RequesterRef = entry.ClientId,
            ParticipantRefs = string.IsNullOrWhiteSpace(entry.JeeberId)
                ? Array.Empty<string>()
                : new[] { entry.JeeberId },
            Status = GenericCaseStatuses.Open,
            Priority = GenericCasePriorities.Urgent,
        }, "otp-escalation:" + entry.Id, entry.ClientId, "system", ct);
        return Map(row);
    }

    public async Task<AdminEscalation?> GetForDeliveryAsync(
        string deliveryId, string reason, CancellationToken ct)
    {
        var page = await _owner.ListCasesAsync(new GenericCaseQueryV1
        {
            Kind = GenericCaseKinds.Support,
            SubjectType = "delivery",
            SubjectRef = deliveryId,
            Limit = 200,
        }, ct);
        return page.Items
            .Where(row => string.Equals(row.Category, CategoryPrefix + reason, StringComparison.Ordinal))
            .Select(Map)
            .FirstOrDefault();
    }

    public async Task<IReadOnlyList<AdminEscalation>> ListAsync(CancellationToken ct)
    {
        var page = await _owner.ListCasesAsync(new GenericCaseQueryV1
        {
            Kind = GenericCaseKinds.Support,
            SubjectType = "delivery",
            Limit = 200,
        }, ct);
        return page.Items
            .Where(row => row.Category.StartsWith(CategoryPrefix, StringComparison.Ordinal))
            .Select(Map)
            .ToArray();
    }

    private static AdminEscalation Map(GenericCaseV1 row) => new()
    {
        Id = row.CaseId.ToString("D"),
        DeliveryId = row.Subject.Ref,
        ClientId = row.RequesterRef,
        JeeberId = row.ParticipantRefs.FirstOrDefault(),
        Reason = row.Category.StartsWith(CategoryPrefix, StringComparison.Ordinal)
            ? row.Category[CategoryPrefix.Length..]
            : row.Category,
        Status = row.Status is GenericCaseStatuses.Closed or GenericCaseStatuses.Fixed
            ? EscalationStatus.Resolved
            : EscalationStatus.Pending,
        CreatedAt = row.CreatedAt,
        OtpAttemptCount = 0,
    };
}
