using System.Text.Json.Serialization;

namespace JeebGateway.Auth.FirebaseDiagnostics;

public sealed record FirebaseTokenDiagnosticRequest(
    [property: JsonPropertyName("idToken")] string? IdToken,
    [property: JsonPropertyName("expectedProjectId")] string? ExpectedProjectId,
    [property: JsonPropertyName("expectedSubject")] string? ExpectedSubject);

public sealed record FirebaseTokenDiagnosticResponse(
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("projectId")] string ProjectId,
    [property: JsonPropertyName("provider")] string Provider,
    [property: JsonPropertyName("subjectSha256")] string SubjectSha256);

internal sealed record UserManagementFirebaseTokenDiagnosticRequest(
    [property: JsonPropertyName("idToken")] string IdToken,
    [property: JsonPropertyName("expectedProjectId")] string ExpectedProjectId,
    [property: JsonPropertyName("expectedSubject")] string ExpectedSubject);

internal sealed record UserManagementFirebaseTokenDiagnosticResponse(
    [property: JsonPropertyName("verified")] bool Verified,
    [property: JsonPropertyName("projectId")] string? ProjectId,
    [property: JsonPropertyName("provider")] string? Provider,
    [property: JsonPropertyName("subjectSha256")] string? SubjectSha256);

public enum FirebaseTokenDiagnosticOutcome
{
    Verified,
    Invalid,
    Disabled,
    Unavailable,
    UpstreamFailure,
}

public sealed record FirebaseTokenDiagnosticResult(
    FirebaseTokenDiagnosticOutcome Outcome,
    FirebaseTokenDiagnosticResponse? Response = null);
