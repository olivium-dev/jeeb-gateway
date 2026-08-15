using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using JeebGateway.Cases;
using JeebGateway.ProhibitedItems.Scanner;
using JeebGateway.Services.Clients;

namespace JeebGateway.ProhibitedItems.FlaggedRequests;

/// <summary>
/// Stateless Jeeb projection over jeeb-state-service's product-neutral case
/// engine. State-service owns the case row, CAS version, immutable messages,
/// audit trail, and callback outbox; the gateway owns only the moderation
/// vocabulary and JSON translation required by the existing public contract.
/// </summary>
public sealed class StateServiceFlaggedRequestStore(IGenericCaseStateClient cases)
    : IUpstreamFlaggedRequestStore
{
    private const string Kind = "moderation_review";
    private const string Category = "prohibited_item";
    private const string MetadataPrefix = "[jeeb.flagged-request.v1]";
    private const string DecisionPrefix = "[jeeb.flagged-decision.v1]";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<FlaggedRequest> CreateAsync(FlaggedRequestCreate input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var metadata = Metadata.From(input);
        var digest = Digest(metadata);
        var key = "jeeb:flagged:create:" + digest;
        var subjectRef = !string.IsNullOrWhiteSpace(input.RequestId) && input.RequestId.Trim().Length <= 450
            ? input.RequestId.Trim()
            : "scan:" + digest;

        var row = await cases.CreateCaseAsync(new CreateGenericCaseRequestV1
        {
            Kind = Kind,
            Category = Category,
            Subject = new GenericCaseSubjectV1
            {
                Type = string.IsNullOrWhiteSpace(input.RequestId) ? "moderation_scan" : "delivery_request",
                Ref = subjectRef
            },
            RequesterRef = input.UserId,
            ParticipantRefs = new[] { input.UserId },
            Status = "pending",
            Priority = GenericCasePriorities.Normal
        }, key, input.UserId, "client", ct);

        var messages = await GetMessagesAsync(row.CaseId, ct);
        if (FindMetadata(messages) is null)
        {
            var created = await cases.AddCaseMessageAsync(row.CaseId,
                new CreateGenericCaseMessageRequestV1
                {
                    ExpectedVersion = row.Version,
                    MessageType = "internal_note",
                    Body = MetadataPrefix + JsonSerializer.Serialize(metadata, Json)
                }, key + ":metadata", input.UserId, "client", ct);
            row = CopyVersion(row, created.CaseVersion);
            messages = messages.Append(created.Message).ToArray();
        }

        return Map(row, messages);
    }

    public async Task<FlaggedRequest?> GetAsync(string id, CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var caseId)) return null;
        try
        {
            var row = await cases.GetCaseAsync(caseId, ct);
            if (!IsFlagged(row)) return null;
            return Map(row, await GetMessagesAsync(caseId, ct));
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
    }

    public async Task<FlaggedRequestPage> ListAsync(
        FlaggedRequestStatus? status,
        int page,
        int pageSize,
        CancellationToken ct)
    {
        var all = new List<GenericCaseV1>();
        string? cursor = null;
        do
        {
            var batch = await cases.ListCasesAsync(new GenericCaseQueryV1
            {
                Kind = Kind,
                Status = status is null ? null : WireStatus(status.Value),
                Sort = GenericCaseSorts.Recent,
                Limit = 200,
                Cursor = cursor
            }, ct);
            all.AddRange(batch.Items.Where(IsFlagged));
            cursor = batch.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));

        var skip = checked((page - 1) * pageSize);
        var selected = all.Skip(skip).Take(pageSize).ToArray();
        var mapped = await Task.WhenAll(selected.Select(async row =>
            Map(row, await GetMessagesAsync(row.CaseId, ct))));
        return new FlaggedRequestPage { Items = mapped, Total = all.Count };
    }

    public async Task<FlaggedRequest?> DecideAsync(
        string id,
        FlaggedRequestStatus status,
        string adminUserId,
        string? note,
        CancellationToken ct)
    {
        if (!Guid.TryParse(id, out var caseId)) return null;
        GenericCaseV1 row;
        try
        {
            row = await cases.GetCaseAsync(caseId, ct);
        }
        catch (GenericCaseApiException ex) when (ex.StatusCode == StatusCodes.Status404NotFound)
        {
            return null;
        }
        if (!IsFlagged(row)) return null;

        var decision = new Decision(WireStatus(status), note);
        var digest = Digest(new { caseId, adminUserId, decision });
        var applied = await cases.ApplyCaseStatusMessageAsync(caseId,
            new ApplyGenericCaseStatusMessageRequestV1
            {
                ExpectedVersion = row.Version,
                Status = decision.Status,
                Body = DecisionPrefix + JsonSerializer.Serialize(decision, Json)
            }, "jeeb:flagged:decision:" + digest, adminUserId, "admin", ct);

        var messages = await GetMessagesAsync(caseId, ct);
        if (messages.All(message => message.MessageId != applied.Message.MessageId))
            messages = messages.Append(applied.Message).ToArray();
        return Map(applied.Case, messages);
    }

    private async Task<IReadOnlyList<GenericCaseMessageV1>> GetMessagesAsync(
        Guid caseId,
        CancellationToken ct)
    {
        var result = new List<GenericCaseMessageV1>();
        string? cursor = null;
        do
        {
            var page = await cases.GetCaseMessagesPageAsync(
                caseId, includeInternal: true, GenericCaseMessageOrders.Newest, 200, cursor, ct);
            result.AddRange(page.Items);
            cursor = page.NextCursor;
        } while (!string.IsNullOrWhiteSpace(cursor));
        return result;
    }

    private static FlaggedRequest Map(
        GenericCaseV1 row,
        IReadOnlyList<GenericCaseMessageV1> messages)
    {
        var metadata = FindMetadata(messages)
                       ?? throw new InvalidDataException(
                           $"Moderation case {row.CaseId:D} has no {MetadataPrefix} metadata message.");
        var decisionMessage = messages
            .Where(message => message.Body.StartsWith(DecisionPrefix, StringComparison.Ordinal))
            .OrderByDescending(message => message.CaseVersion)
            .FirstOrDefault();
        var decision = decisionMessage is null ? null : Parse<Decision>(decisionMessage.Body, DecisionPrefix);
        return new FlaggedRequest
        {
            Id = row.CaseId.ToString("D"),
            RequestId = metadata.RequestId,
            UserId = metadata.UserId,
            Description = metadata.Description,
            Matches = metadata.Matches.Select(item => item.ToDomain()).ToArray(),
            Status = ParseStatus(row.Status),
            CreatedAt = row.CreatedAt,
            DecidedBy = decisionMessage?.Actor.Ref,
            DecidedAt = decisionMessage?.CreatedAt,
            DecisionNote = decision?.Note
        };
    }

    private static Metadata? FindMetadata(IEnumerable<GenericCaseMessageV1> messages)
    {
        var message = messages
            .Where(item => item.Body.StartsWith(MetadataPrefix, StringComparison.Ordinal))
            .OrderBy(item => item.CaseVersion)
            .FirstOrDefault();
        return message is null ? null : Parse<Metadata>(message.Body, MetadataPrefix);
    }

    private static T Parse<T>(string body, string prefix) =>
        JsonSerializer.Deserialize<T>(body[prefix.Length..], Json)
        ?? throw new InvalidDataException($"State case message {prefix} contains invalid JSON.");

    private static bool IsFlagged(GenericCaseV1 row) =>
        string.Equals(row.Kind, Kind, StringComparison.Ordinal)
        && string.Equals(row.Category, Category, StringComparison.Ordinal);

    private static string WireStatus(FlaggedRequestStatus status) => status switch
    {
        FlaggedRequestStatus.Pending => "pending",
        FlaggedRequestStatus.Cleared => "cleared",
        FlaggedRequestStatus.Upheld => "upheld",
        _ => throw new ArgumentOutOfRangeException(nameof(status))
    };

    private static FlaggedRequestStatus ParseStatus(string status) => status switch
    {
        "pending" => FlaggedRequestStatus.Pending,
        "cleared" => FlaggedRequestStatus.Cleared,
        "upheld" => FlaggedRequestStatus.Upheld,
        _ => throw new InvalidDataException($"Moderation case has unsupported status '{status}'.")
    };

    private static string Digest<T>(T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    private static GenericCaseV1 CopyVersion(GenericCaseV1 row, int version) => new()
    {
        CaseId = row.CaseId,
        Kind = row.Kind,
        Category = row.Category,
        Subject = row.Subject,
        RequesterRef = row.RequesterRef,
        ParticipantRefs = row.ParticipantRefs,
        Status = row.Status,
        Priority = row.Priority,
        AssigneeRef = row.AssigneeRef,
        DueAt = row.DueAt,
        Version = version,
        ClosedAt = row.ClosedAt,
        CreatedAt = row.CreatedAt,
        UpdatedAt = row.UpdatedAt
    };

    private sealed record Metadata(
        string? RequestId,
        string UserId,
        string Description,
        IReadOnlyList<Match> Matches)
    {
        public static Metadata From(FlaggedRequestCreate input) => new(
            string.IsNullOrWhiteSpace(input.RequestId) ? null : input.RequestId.Trim(),
            input.UserId,
            input.Description,
            input.Matches.Select(Match.From).ToArray());
    }

    private sealed record Match(
        string ItemId,
        string ItemName,
        string Category,
        string MatchedTerm,
        string Evidence,
        string MatchType,
        double Confidence,
        string Severity)
    {
        public static Match From(ProhibitedItemMatch value) => new(
            value.ItemId,
            value.ItemName,
            value.Category,
            value.MatchedTerm,
            value.Evidence,
            value.MatchType.ToString().ToLowerInvariant(),
            value.Confidence,
            value.Severity.ToString().ToLowerInvariant());

        public ProhibitedItemMatch ToDomain() => new()
        {
            ItemId = ItemId,
            ItemName = ItemName,
            Category = Category,
            MatchedTerm = MatchedTerm,
            Evidence = Evidence,
            MatchType = Enum.Parse<ProhibitedMatchType>(MatchType, ignoreCase: true),
            Confidence = Confidence,
            Severity = Enum.Parse<ProhibitedSeverity>(Severity, ignoreCase: true)
        };
    }

    private sealed record Decision(string Status, string? Note);
}
