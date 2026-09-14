using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace JeebGateway.Auth.FirebaseDiagnostics;

public interface IUserManagementFirebaseTokenDiagnosticClient
{
    Task<FirebaseTokenDiagnosticResult> VerifyAsync(
        string idToken,
        string expectedProjectId,
        string expectedSubject,
        CancellationToken cancellationToken);
}

public sealed partial class UserManagementFirebaseTokenDiagnosticClient
    : IUserManagementFirebaseTokenDiagnosticClient
{
    public const string HttpClientName = "firebase-token-diagnostics-user-management";
    internal const string RelativePath = "api/User/diagnostics/firebase-token";
    internal static readonly TimeSpan OperationTimeout = TimeSpan.FromSeconds(15);
    private const int MaximumSuccessBodyBytes = 4 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _operationTimeout;

    public UserManagementFirebaseTokenDiagnosticClient(HttpClient http)
        : this(http, OperationTimeout)
    {
    }

    internal UserManagementFirebaseTokenDiagnosticClient(HttpClient http, TimeSpan operationTimeout)
    {
        _http = http;
        _operationTimeout = operationTimeout;
    }

    public async Task<FirebaseTokenDiagnosticResult> VerifyAsync(
        string idToken,
        string expectedProjectId,
        string expectedSubject,
        CancellationToken cancellationToken)
    {
        using var operationDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        operationDeadline.CancelAfter(_operationTimeout);
        var operationToken = operationDeadline.Token;
        using var request = new HttpRequestMessage(HttpMethod.Post, RelativePath)
        {
            Content = JsonContent.Create(new UserManagementFirebaseTokenDiagnosticRequest(
                idToken,
                expectedProjectId,
                expectedSubject)),
        };
        using var response = await _http.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            operationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            return new(FirebaseTokenDiagnosticOutcome.Invalid);
        }

        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return new(FirebaseTokenDiagnosticOutcome.Disabled);
        }

        if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
        {
            return new(FirebaseTokenDiagnosticOutcome.Unavailable);
        }

        if (response.StatusCode != HttpStatusCode.OK)
        {
            return new(FirebaseTokenDiagnosticOutcome.UpstreamFailure);
        }

        UserManagementFirebaseTokenDiagnosticResponse? upstream;
        try
        {
            upstream = JsonSerializer.Deserialize<UserManagementFirebaseTokenDiagnosticResponse>(
                await ReadBoundedSuccessBodyAsync(response.Content, operationToken));
        }
        catch (Exception exception) when (exception is JsonException or NotSupportedException or IOException)
        {
            return new(FirebaseTokenDiagnosticOutcome.UpstreamFailure);
        }

        if (upstream is not
            {
                Verified: true,
                ProjectId: not null,
                Provider: not null,
                SubjectSha256: not null,
            }
            || !string.Equals(upstream.ProjectId, expectedProjectId, StringComparison.Ordinal)
            || !SafeProviderRegex().IsMatch(upstream.Provider)
            || !Sha256Regex().IsMatch(upstream.SubjectSha256)
            || !string.Equals(
                upstream.SubjectSha256,
                Sha256(expectedSubject),
                StringComparison.Ordinal))
        {
            return new(FirebaseTokenDiagnosticOutcome.UpstreamFailure);
        }

        return new(
            FirebaseTokenDiagnosticOutcome.Verified,
            new FirebaseTokenDiagnosticResponse(
                true,
                upstream.ProjectId,
                upstream.Provider,
                upstream.SubjectSha256));
    }

    private static async Task<byte[]> ReadBoundedSuccessBodyAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is > MaximumSuccessBodyBytes)
        {
            throw new IOException("Firebase diagnostic success response exceeded its size limit.");
        }

        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        var buffer = new byte[MaximumSuccessBodyBytes + 1];
        var total = 0;
        while (total < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken);
            if (read == 0)
            {
                break;
            }
            total += read;
        }

        if (total > MaximumSuccessBodyBytes)
        {
            throw new IOException("Firebase diagnostic success response exceeded its size limit.");
        }

        return buffer[..total];
    }

    private static string Sha256(string value) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    [GeneratedRegex("\\A[a-zA-Z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex SafeProviderRegex();

    [GeneratedRegex("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Regex();
}
