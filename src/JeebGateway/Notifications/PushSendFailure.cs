using System;
using System.Net.Http;
using JeebGateway.service.ServicePushNotification;

namespace JeebGateway.Notifications;

/// <summary>
/// Why one per-recipient push did not land, split by the only question that matters
/// operationally: could repeating this call ever change the answer?
///
/// <para>PHASE-V D3, observed live 2026-08-16: one request produced 26 POSTs to
/// <c>POST /api/v1/sent-payload/user/{id}</c> — 1 × 201 and 25 × 404, because five recipients
/// with no registered device were each re-POSTed five times. A 404 there means literally
/// "Push notification records for user … not found": the user owns no device row. That is a
/// steady-state fact about the account, not a fault, and no number of retries invents a
/// device. Treating it as an ordinary failure both overstated the incident and multiplied the
/// upstream call volume by the size of the device-less population.</para>
///
/// <para>The counterpart lie is on the success side: a 404 must not be counted as
/// <c>sent</c> either. See <see cref="PushAcceptance"/> for what a 2xx actually proves.</para>
/// </summary>
public enum PushSendFailureKind
{
    /// <summary>:10040 answered 404 — the recipient has NO registered device. Terminal.</summary>
    NoRegisteredDevice,

    /// <summary>A refusal repeating cannot fix: malformed request, auth, contract drift.</summary>
    Terminal,

    /// <summary>A blip that may clear: 5xx, 408, 429, timeout, transport, open circuit.</summary>
    Retryable,
}

/// <summary>Classifies a per-recipient push fault. Unknown always classifies as retryable.</summary>
public static class PushSendFailure
{
    public static PushSendFailureKind Classify(Exception? exception)
        => exception switch
        {
            null => PushSendFailureKind.Retryable,
            ApiException api => FromStatus(api.StatusCode),
            HttpRequestException => PushSendFailureKind.Retryable,
            TimeoutException => PushSendFailureKind.Retryable,
            OperationCanceledException => PushSendFailureKind.Retryable,
            // Never silently downgrade an unrecognised fault to "expected".
            _ => PushSendFailureKind.Retryable,
        };

    private static PushSendFailureKind FromStatus(int status)
    {
        if (status == 404)
        {
            return PushSendFailureKind.NoRegisteredDevice;
        }

        // 5xx/408/429 may clear; every other 4xx is the caller's own bug and will not.
        return status >= 500 || status == 408 || status == 429
            ? PushSendFailureKind.Retryable
            : PushSendFailureKind.Terminal;
    }
}
