namespace JeebGateway.FormSubmissions;

public sealed record FormSubmissionRecord(
    IReadOnlyDictionary<string, string> Answers, DateTimeOffset SubmittedAt, string? ZoneKey);
public sealed record CoverageDto(bool Checked, string? ZoneKey);
public sealed record FormSubmissionResponse(string TemplateName, string UserId,
    IReadOnlyDictionary<string, object?> Data, CoverageDto Coverage, DateTimeOffset SubmittedAt);

public interface IFormSubmissionStore
{
    Task<FormSubmissionRecord?> GetAsync(string userId, string templateName, CancellationToken ct);
    Task SetAsync(string userId, string templateName, FormSubmissionRecord record, CancellationToken ct);
}
