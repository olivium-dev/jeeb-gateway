using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Presents delivery-service's <c>importauth</c> bearer on the request-owner surface
/// (<c>/api/v1/requests*</c>).
///
/// <para>Deliberately NOT <see cref="DeliveryServiceCredentialHandler"/>'s
/// <c>X-Delivery-Service-Token</c>: delivery-service does not read that header anywhere
/// in its source, so building a cutover on it would authenticate against nothing. The
/// <c>importauth</c> guard is the credential that surface actually checks, and it fails
/// closed when unset.</para>
///
/// <para>Secret handling mirrors the sibling handler exactly — the mounted file is re-read
/// per request so rotation needs no restart, the bytes are zeroed after decoding, and a
/// configuration-held value is refused outside Development/Testing so production cannot
/// drift back to an environment-held secret.</para>
/// </summary>
public sealed class DeliveryImportCredentialHandler(
    IConfiguration configuration,
    IHostEnvironment environment) : DelegatingHandler
{
    internal const string Scheme = "Bearer";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await ReadTokenAsync(configuration, environment, cancellationToken);
        // Replace rather than append: this client must never forward a caller's bearer,
        // which would present an end-user token to a service credential check.
        request.Headers.Authorization = new AuthenticationHeaderValue(Scheme, token);
        return await base.SendAsync(request, cancellationToken);
    }

    internal static async Task<string> ReadTokenAsync(
        IConfiguration configuration,
        IHostEnvironment environment,
        CancellationToken cancellationToken)
    {
        var tokenFile = configuration["DELIVERY_IMPORT_TOKEN_FILE"]
                        ?? configuration["Services:Delivery:ImportTokenFile"];
        if (!string.IsNullOrWhiteSpace(tokenFile))
            return await ReadFileAsync(tokenFile, cancellationToken);

        if (!environment.IsDevelopment() && !environment.IsEnvironment("Testing"))
            throw new InvalidOperationException(
                "DELIVERY_IMPORT_TOKEN_FILE must name an absolute mounted-secret path.");

        var token = configuration["DELIVERY_IMPORT_TOKEN"]
                    ?? configuration["Services:Delivery:ImportToken"];
        if (!DeliveryServiceCredentialHandler.IsValidToken(token))
            throw new InvalidOperationException(
                "Delivery import credential is not configured or is invalid.");

        return token!;
    }

    private static async Task<string> ReadFileAsync(string tokenFile, CancellationToken ct)
    {
        if (!Path.IsPathFullyQualified(tokenFile))
            throw new InvalidOperationException(
                "DELIVERY_IMPORT_TOKEN_FILE must name an absolute mounted-secret path.");

        var info = new FileInfo(tokenFile);
        if (!info.Exists
            || info.Length is < 1 or > DeliveryServiceCredentialHandler.MaximumTokenBytes + 2)
        {
            throw new InvalidOperationException(
                "Delivery import-token file is missing or outside the allowed size.");
        }

        byte[] bytes;
        try
        {
            bytes = await File.ReadAllBytesAsync(tokenFile, ct);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException(
                "Delivery import-token file could not be read.", ex);
        }

        try
        {
            return DeliveryServiceCredentialHandler.DecodeMountedToken(bytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
        }
    }
}
