using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace JeebGateway.Infrastructure;

/// <summary>
/// Global last-line <see cref="IExceptionHandler"/> that converts any exception
/// which would otherwise surface as a raw 500 into an RFC 7807
/// <c>application/problem+json</c> response.
///
/// <para><b>Why (S07 root-cause hardening).</b> Before this handler, an upstream
/// non-2xx that bubbled up as an <see cref="HttpRequestException"/> (e.g.
/// <c>EnsureSuccessStatusCode()</c> on a 404 from offer-service) reached the
/// pipeline with no handler and became an opaque 500 — masking the real
/// negative. This handler is the durable fix across <i>all</i> controllers: an
/// unhandled exception now produces a typed ProblemDetails, never a bare 500
/// string. The S07 accept rewire forwards offer-service statuses explicitly, so
/// this is defence-in-depth, not the primary path.</para>
///
/// <para>It is intentionally conservative: controllers that already return their
/// own typed results (the vast majority) are untouched because they never throw.
/// Only genuinely unhandled exceptions are mapped here.</para>
/// </summary>
public sealed class UpstreamExceptionHandler : IExceptionHandler
{
    private readonly IProblemDetailsService _problemDetails;
    private readonly ILogger<UpstreamExceptionHandler> _logger;

    public UpstreamExceptionHandler(
        IProblemDetailsService problemDetails,
        ILogger<UpstreamExceptionHandler> logger)
    {
        _problemDetails = problemDetails;
        _logger = logger;
    }

    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        var (status, title, type) = exception switch
        {
            HttpRequestException => (
                StatusCodes.Status502BadGateway,
                "An upstream service returned an unexpected response.",
                "https://jeeb.dev/errors/upstream-unavailable"),
            // JEBV4 UserPreferences-500 incident (2026-07-24): a dead/refused upstream
            // (e.g. remote-user-preferences down) is not always seen here as a raw
            // HttpRequestException. The org-standard resilience pipeline (the
            // AttachStandardPipeline / AddResilienceHandler ladder in
            // Extensions/ServiceClientExtensions.cs — Polly v8 via
            // Microsoft.Extensions.Http.Resilience) wraps it in ITS OWN exception once
            // a per-attempt timeout fires or the circuit breaker trips:
            // Polly.Timeout.TimeoutRejectedException or
            // Polly.CircuitBreaker.BrokenCircuitException, each carrying the real
            // HttpRequestException/SocketException as InnerException. Neither matched
            // the HttpRequestException arm above, so EVERY typed client guarded by the
            // standard pipeline — not only UserPreferences — fell through to the
            // generic 500 "unexpected error" below, indistinguishable from an actual
            // gateway bug. Confirmed live on MSI: POST /api/UserPreferences/preferences
            // threw Polly.CircuitBreaker.BrokenCircuitException ("The circuit is now
            // open and is not allowing calls.") ---> HttpRequestException("Connection
            // refused (127.0.0.1:10067)") ---> SocketException(111) and still surfaced
            // as a bare 500. Route both resilience wrapper types to the same
            // upstream-unavailable mapping as a raw HttpRequestException.
            BrokenCircuitException or TimeoutRejectedException => (
                StatusCodes.Status502BadGateway,
                "An upstream service returned an unexpected response.",
                "https://jeeb.dev/errors/upstream-unavailable"),
            // gwdbx W2-R11: settlement-service owns the money rows. An unreachable/refusing
            // upstream answers "unavailable", never a confident empty — the O10 lesson.
            JeebGateway.Financials.SettlementServiceUnavailableException => (
                StatusCodes.Status503ServiceUnavailable,
                "Settlement records are unavailable: settlement-service could not be reached.",
                JeebGateway.Financials.SettlementServiceUnavailableException.ProblemType),
            // The gateway holds the SERVICE scope only; ADMIN-scope batch/payout operations are
            // served by settlement-service directly and are deliberately not proxied.
            JeebGateway.Financials.SettlementAdminScopeException => (
                StatusCodes.Status503ServiceUnavailable,
                "Settlement payout batches are served by settlement-service.",
                JeebGateway.Financials.SettlementAdminScopeException.ProblemType),
            OperationCanceledException when cancellationToken.IsCancellationRequested => (
                499, // nginx "client closed request" — request aborted, nothing to serve
                "The request was cancelled by the client.",
                "https://jeeb.dev/errors/request-cancelled"),
            _ => (
                StatusCodes.Status500InternalServerError,
                "An unexpected error occurred.",
                "https://jeeb.dev/errors/internal-error")
        };

        // GW12-OBS-4 (Leg-12): stamp the single most important incident log line with
        // the correlation id (the value a client/support ticket quotes) and the OTel
        // trace id (the key that stitches this line to its distributed trace). Both are
        // resolved defensively so a missing id never disturbs the exception mapping.
        // Belt-and-suspenders alongside GW12-OBS-1's log-scope: an on-call engineer can
        // land on THIS line and immediately have both grep keys inline.
        var correlationId = httpContext.Items.TryGetValue("CorrelationId", out var cid) && cid is string c
            ? c
            : "(none)";
        var traceId = Activity.Current?.TraceId.ToString() ?? "(none)";

        // Log with the real exception so ops keeps full fidelity; the wire
        // response never leaks exception text.
        _logger.LogError(exception,
            "Unhandled exception on {Method} {Path} → {Status} (CorrelationId={CorrelationId} TraceId={TraceId})",
            httpContext.Request.Method, httpContext.Request.Path, status, correlationId, traceId);

        httpContext.Response.StatusCode = status;

        return await _problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            Exception = exception,
            ProblemDetails = new ProblemDetails
            {
                Status = status,
                Title = title,
                Type = type
            }
        });
    }
}
