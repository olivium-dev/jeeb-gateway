using System;
using System.Globalization;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Partner;

/// <summary>
/// Shared thin base for the Partner Portal controllers: caller-id resolution and the sanitized
/// RFC 7807 mappings for upstream wallet-service errors and gateway-side partner preconditions.
/// Mirrors the JeebWalletController / WalletController UpstreamProblem idiom (JEBV4-249/253) — the
/// upstream body is logged server-side ONLY, never echoed to the caller (no info disclosure).
/// </summary>
[ApiController]
[Produces("application/json", "application/problem+json")]
public abstract class PartnerControllerBase : ControllerBase
{
    /// <summary>Resolve the caller's own wallet-holder id (their user GUID) from the bearer token.</summary>
    protected bool TryResolveCallerId(out Guid callerId, out IActionResult failure)
    {
        callerId = Guid.Empty;

        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            failure = unauthorized;
            return false;
        }

        if (!Guid.TryParse(userId, out callerId))
        {
            failure = StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Caller identity is not a valid wallet-holder id.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/invalid-wallet-holder",
            });
            return false;
        }

        failure = null!;
        return true;
    }

    /// <summary>Map an upstream wallet-service failure to a sanitized RFC 7807 result (body never echoed).</summary>
    protected IActionResult UpstreamProblem(WalletApiException ex, ILogger log)
    {
        var status = ex.StatusCode is >= 400 and < 600 ? ex.StatusCode : StatusCodes.Status502BadGateway;
        log.LogWarning(ex,
            "Partner wallet BFF: wallet-service call failed on {Method} {Path} → {Status}.",
            Request.Method, Request.Path, status);
        return Problem(title: "The wallet request could not be completed.", statusCode: status);
    }

    /// <summary>
    /// Map a gateway-side precondition failure (missing wallet, un-executable saga) to a 409 the
    /// caller can act on. Carries the gateway-authored message only (no upstream body).
    /// </summary>
    protected IActionResult PartnerProblem(PartnerWalletException ex)
        => Problem(
            title: ex.Message,
            statusCode: StatusCodes.Status409Conflict,
            type: "https://jeeb.dev/errors/partner-wallet-precondition");

    /// <summary>
    /// Map an idempotency-dedup refusal (a matching money move is already pending or awaiting
    /// reconciliation) to a 409 — the caller must NOT retry; money never moves twice.
    /// </summary>
    protected IActionResult InFlightProblem(PartnerWalletInFlightException ex)
        => Problem(
            title: ex.Message,
            statusCode: StatusCodes.Status409Conflict,
            type: "https://jeeb.dev/errors/partner-wallet-in-flight");

    /// <summary>
    /// Map an ambiguous, possibly-committed wallet move to a 502. Logged server-side as an error (the
    /// key is locked for operator reconciliation); the caller is told not to blindly resubmit.
    /// </summary>
    protected IActionResult UncertainProblem(PartnerWalletUncertainException ex, ILogger log)
    {
        log.LogError(ex,
            "Partner wallet move outcome UNCERTAIN on {Method} {Path}; key locked for reconciliation.",
            Request.Method, Request.Path);
        return Problem(
            title: ex.Message,
            statusCode: StatusCodes.Status502BadGateway,
            type: "https://jeeb.dev/errors/partner-wallet-uncertain");
    }

    /// <summary>
    /// Cheap fat-finger guardrail shared by every money-moving action: reject an amount above the
    /// configured ceiling BEFORE any wallet-service call (a 400, NOT a fee rule — the authoritative
    /// limits stay wallet-service's). Applied on the admin cash-CREATE path too (money creation).
    /// </summary>
    protected bool TryEnforceAmountCeiling(double amount, double maxAmount, out IActionResult problem)
    {
        if (amount > maxAmount)
        {
            problem = Problem(
                title: $"amount exceeds the maximum permitted per operation ({maxAmount}).",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://jeeb.dev/errors/amount-too-large");
            return false;
        }
        problem = null!;
        return true;
    }

    /// <summary>
    /// Parse an OPTIONAL ISO-8601 (<c>yyyy-MM-dd</c>) ledger-filter date. An absent/blank value is a
    /// valid "no bound" (returns <c>true</c>, <c>value = null</c>). A malformed value is rejected with a
    /// sanitized RFC 7807 400 (<c>type = .../invalid-ledger-date</c>) — never a framework binding page or
    /// a 500. The bound is a UTC calendar date; the reader applies it inclusively (from-day .. to-day).
    /// Strict invariant-culture parse so the result is deterministic regardless of server culture.
    /// </summary>
    protected bool TryParseLedgerDate(string? raw, string field, out DateOnly? value, out IActionResult problem)
    {
        value = null;
        problem = null!;
        if (string.IsNullOrWhiteSpace(raw)) return true;

        if (DateOnly.TryParseExact(raw.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
        {
            value = parsed;
            return true;
        }

        problem = Problem(
            title: $"'{field}' must be an ISO-8601 date (yyyy-MM-dd).",
            statusCode: StatusCodes.Status400BadRequest,
            type: "https://jeeb.dev/errors/invalid-ledger-date");
        return false;
    }

    // ── PP-7 OTP step-up (RFC 7807) ──────────────────────────────────────────────────────────
    //
    // The frozen problem types: otp-not-required (challenge below threshold), otp-required (an
    // above-threshold confirm arrived without a code), otp-invalid (unknown/mismatched/expired/wrong
    // code), otp-exhausted (too many wrong guesses), otp-consumed (single-use challenge replayed).
    // Built as ProblemDetails + application/problem+json so the {otpRequired}/{attemptsRemaining}
    // extensions the portal keys on serialize as top-level members (ProblemDetails.Extensions).

    /// <summary>Challenge endpoint refusal: an amount at or below the threshold needs no step-up code.</summary>
    protected IActionResult OtpNotRequiredProblem(double threshold)
        => OtpProblem(
            StatusCodes.Status400BadRequest,
            "https://jeeb.dev/errors/otp-not-required",
            $"A step-up code is not required for an amount at or below the OTP threshold ({threshold}).");

    /// <summary>Confirm gate: an above-threshold transfer arrived without an OTP challenge id + code.</summary>
    protected IActionResult OtpRequiredProblem()
        => OtpProblem(
            StatusCodes.Status403Forbidden,
            "https://jeeb.dev/errors/otp-required",
            "A one-time step-up code is required for a transfer above the OTP threshold.",
            ("otpRequired", true));

    /// <summary>Wrong / unknown / mismatched / expired code. Carries attempts-remaining when known.</summary>
    protected IActionResult OtpInvalidProblem(int? attemptsRemaining)
        => attemptsRemaining is int remaining
            ? OtpProblem(
                StatusCodes.Status403Forbidden,
                "https://jeeb.dev/errors/otp-invalid",
                "The step-up code is invalid.",
                ("attemptsRemaining", remaining))
            : OtpProblem(
                StatusCodes.Status403Forbidden,
                "https://jeeb.dev/errors/otp-invalid",
                "The step-up code is invalid.");

    /// <summary>Too many wrong guesses — the challenge is hard-expired; the partner must request a new one.</summary>
    protected IActionResult OtpExhaustedProblem()
        => OtpProblem(
            StatusCodes.Status403Forbidden,
            "https://jeeb.dev/errors/otp-exhausted",
            "Too many incorrect attempts; request a new step-up code.");

    /// <summary>The challenge was already used by a prior successful confirm (single-use replay).</summary>
    protected IActionResult OtpConsumedProblem()
        => OtpProblem(
            StatusCodes.Status403Forbidden,
            "https://jeeb.dev/errors/otp-consumed",
            "This step-up code has already been used; request a new one.");

    /// <summary>
    /// Map an OTP verdict to its RFC 7807 refusal, or return <c>false</c> (no problem) when the verdict
    /// is <see cref="PartnerOtpOutcome.Valid"/> and the transfer may proceed.
    /// </summary>
    protected bool TryOtpFailureProblem(PartnerOtpValidation verdict, out IActionResult problem)
    {
        problem = verdict.Outcome switch
        {
            PartnerOtpOutcome.Valid => null!,
            PartnerOtpOutcome.WrongCode => OtpInvalidProblem(verdict.AttemptsRemaining),
            PartnerOtpOutcome.Exhausted => OtpExhaustedProblem(),
            PartnerOtpOutcome.Consumed => OtpConsumedProblem(),
            // NotFound / Mismatch / Expired all read to the caller as an invalid code (no oracle).
            _ => OtpInvalidProblem(null),
        };
        return verdict.Outcome != PartnerOtpOutcome.Valid;
    }

    private IActionResult OtpProblem(
        int status, string type, string title, params (string Key, object? Value)[] extensions)
    {
        var problem = new ProblemDetails { Status = status, Title = title, Type = type };
        foreach (var (key, value) in extensions)
        {
            problem.Extensions[key] = value;
        }

        return new ObjectResult(problem)
        {
            StatusCode = status,
            ContentTypes = { "application/problem+json" },
        };
    }
}
