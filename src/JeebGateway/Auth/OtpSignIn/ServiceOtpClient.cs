using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace JeebGateway.Auth.OtpSignIn;

/// <summary>
/// Production <see cref="IServiceOtpClient"/> adapter over the
/// <c>olivium-dev/one-time-password</c> service (OTPApi). Hand-coded against
/// the verified upstream routes — <c>POST api/OTP/send</c> and
/// <c>POST api/OTP/validate</c> (see
/// <c>one-time-password/OTPApi/Controllers/OTPController.cs</c>) — because the
/// NSwag-generated <see cref="JeebGateway.Services.Clients.ServiceOTPClient"/>
/// targets the legacy <c>api/User/*</c> handover routes AND returns
/// <c>Task</c> (void), discarding the <c>OTPResponse</c> body this adapter
/// must inspect to classify validate failures.
///
/// The typed <see cref="HttpClient"/> injected here is registered in
/// <see cref="OtpSignInServiceCollectionExtensions.AddJeebOtpSignIn"/> with the
/// <c>ServiceOTPApi:BaseUrl</c> base address and the org-standard Polly
/// resilience pipeline, so this class never deals with retry/timeout/breaker.
///
/// Field-mapping notes (audit #14764 / #14769):
///   - <b>SendAsync.Code</b>: the downstream <c>OTPResponse</c> is
///     <c>{ success, message }</c> ONLY — the generated 6-digit code is stored
///     in the OTP service's Postgres and delivered out-of-band via Twilio SMS.
///     It is NEVER on the wire, so <see cref="SendOtpResult.Code"/> is returned
///     as <see cref="string.Empty"/>. The OTP sign-in flow is code-based: the
///     user-entered code is round-tripped straight to <c>api/OTP/validate</c>;
///     the gateway never reads <c>Code</c> (only <c>ExpiresAt</c> and
///     <c>Reused</c> — see <c>AuthOtpController.RequestOtp</c>). A test harness
///     that needs the real code must read it from the OTP service DB / SMS sink,
///     not from this response.
///   - <b>SendAsync.ExpiresAt</b>: the downstream does not return an expiry;
///     <c>OTPService.SendOTPAsync</c> hard-codes <c>UtcNow.AddMinutes(5)</c>.
///     Synthesised here as <c>now + 300 s</c> from the injected
///     <see cref="TimeProvider"/> so the controller's audit-locked
///     <c>ttlSeconds = 300</c> holds.
///   - <b>SendAsync.Reused</b>: the downstream overwrites the active record on
///     every send (no idempotency signal on the response), so reuse is not
///     observable from the wire — reported as <c>false</c>.
/// </summary>
public sealed class ServiceOtpClient : IServiceOtpClient
{
    private const int OtpTtlSeconds = 300;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TimeProvider _clock;
    private readonly string _applicationId;

    public ServiceOtpClient(
        HttpClient http,
        TimeProvider clock,
        IOptions<ServiceOtpApiOptions> options)
    {
        _http = http;
        _clock = clock;
        _applicationId = options.Value.ApplicationId;
    }

    public async Task<SendOtpResult> SendAsync(
        string normalizedE164Phone, string purpose, CancellationToken ct = default)
    {
        // `purpose` is a gateway-domain concept; the downstream multiplexes by
        // ApplicationId (a GUID), not by a free-text purpose. The login flow
        // uses the single configured ServiceOTPApi:ApplicationId.
        using var response = await _http.PostAsJsonAsync(
            "api/OTP/send",
            new OtpSendRequest(normalizedE164Phone, _applicationId),
            JsonOptions,
            ct);

        var payload = await ReadOtpResponseAsync(response, ct);
        if (!response.IsSuccessStatusCode || payload is not { Success: true })
        {
            // Non-2xx OR success:false (e.g. Twilio failure) — surface as a
            // downstream fault so AuthOtpController returns 502. The gateway
            // must NEVER treat a failed send as a success.
            throw new HttpRequestException(
                $"one-time-password send failed (status {(int)response.StatusCode}): " +
                $"{payload?.Message ?? "no body"}");
        }

        var expiresAt = _clock.GetUtcNow().AddSeconds(OtpTtlSeconds);
        return new SendOtpResult(Code: string.Empty, ExpiresAt: expiresAt, Reused: false);
    }

    public async Task<ValidateOtpResult> ValidateAsync(
        string normalizedE164Phone, string code, CancellationToken ct = default)
    {
        using var response = await _http.PostAsJsonAsync(
            "api/OTP/validate",
            new OtpValidateRequest(normalizedE164Phone, code, _applicationId),
            JsonOptions,
            ct);

        if (response.IsSuccessStatusCode)
        {
            var ok = await ReadOtpResponseAsync(response, ct);
            return ok is { Success: true }
                ? ValidateOtpResult.Ok()
                : ValidateOtpResult.InvalidOtp();
        }

        // The downstream returns 401 Unauthorized for every validation failure
        // (wrong code, expired, no record, too-many-attempts) and distinguishes
        // them only via the message string. Map the attempt-cap case to
        // too_many_attempts so AuthOtpController emits 429; everything else is
        // invalid_otp (401). 5xx is NOT a validation outcome — rethrow so the
        // controller emits 502.
        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.BadRequest)
        {
            var failure = await ReadOtpResponseAsync(response, ct);
            var message = failure?.Message ?? string.Empty;
            return message.Contains("too many", StringComparison.OrdinalIgnoreCase)
                ? ValidateOtpResult.TooManyAttempts()
                : ValidateOtpResult.InvalidOtp();
        }

        throw new HttpRequestException(
            $"one-time-password validate returned unexpected status {(int)response.StatusCode}.");
    }

    private static async Task<OtpResponse?> ReadOtpResponseAsync(
        HttpResponseMessage response, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength is 0)
        {
            return null;
        }

        try
        {
            return await response.Content.ReadFromJsonAsync<OtpResponse>(JsonOptions, ct);
        }
        catch (JsonException)
        {
            // Non-JSON error body (e.g. an HTML 502 from a reverse proxy).
            return null;
        }
    }

    private sealed record OtpSendRequest(
        [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
        [property: JsonPropertyName("applicationId")] string ApplicationId);

    private sealed record OtpValidateRequest(
        [property: JsonPropertyName("phoneNumber")] string PhoneNumber,
        [property: JsonPropertyName("otp")] string Otp,
        [property: JsonPropertyName("applicationId")] string ApplicationId);

    private sealed record OtpResponse(
        [property: JsonPropertyName("success")] bool Success,
        [property: JsonPropertyName("message")] string? Message);
}
