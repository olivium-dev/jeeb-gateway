using JeebGateway.Auth.Capabilities;
using JeebGateway.Cases;
using JeebGateway.Observability;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[ApiController]
[Route("admin/v1/case-recovery")]
// W6-02 compat window: unversioned twin(s) of the v1 route(s) here; versioned paths unchanged.
[Route("admin/case-recovery")]
[RequireCapability(Capabilities.DisputeResolve)]
public sealed class AdminCaseRecoveryController : ControllerBase
{
    private readonly IPushDispatchRecoveryClient _push;
    private readonly IGenericCaseStateClient _state;
    private readonly ILogger<AdminCaseRecoveryController> _log;

    public AdminCaseRecoveryController(
        IPushDispatchRecoveryClient push,
        IGenericCaseStateClient state,
        ILogger<AdminCaseRecoveryController> log)
    {
        _push = push;
        _state = state;
        _log = log;
    }

    [HttpGet("callback-dead-letters")]
    public Task<IActionResult> ListCallbackDeadLetters(
        [FromQuery] int limit = 100,
        [FromQuery] string? cursor = null,
        CancellationToken ct = default) => ExecuteStateAsync("callback_dead_letters", async () =>
            Ok(await _state.GetCaseDeadLettersAsync(Math.Clamp(limit, 1, 200), cursor, ct)));

    [HttpPost("callback-dead-letters/{eventId:guid}/requeue")]
    public Task<IActionResult> RequeueCallbackDeadLetter(
        Guid eventId,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
            return Task.FromResult(unauthorized);
        if (!ValidKey(idempotencyKey)) return InvalidKey();
        return ExecuteStateAsync("callback_requeue", async () => Ok(
            await _state.RequeueCaseDeadLetterAsync(
                eventId, idempotencyKey!.Trim(), adminId, ct)));
    }

    [HttpGet("push-dispatches/stale")]
    public Task<IActionResult> ListStalePushDispatches(
        [FromQuery] int olderThanSeconds = 300,
        [FromQuery] int limit = 100,
        CancellationToken ct = default) => ExecuteAsync("push_list_stale", async () =>
            Ok(await _push.ListStaleAsync(
                Math.Clamp(olderThanSeconds, 60, 30 * 24 * 60 * 60),
                Math.Clamp(limit, 1, 500), ct)));

    [HttpGet("push-dispatches/{idempotencyKey}")]
    public Task<IActionResult> GetPushDispatch(
        string idempotencyKey,
        [FromQuery] int staleAfterSeconds = 300,
        CancellationToken ct = default)
    {
        if (!ValidKey(idempotencyKey)) return InvalidKey();
        return ExecuteAsync("push_get", async () => Ok(await _push.GetAsync(
            idempotencyKey.Trim(), Math.Clamp(staleAfterSeconds, 60, 30 * 24 * 60 * 60), ct)));
    }

    [HttpPost("push-dispatches/{idempotencyKey}/resolve")]
    public Task<IActionResult> ResolvePushDispatch(
        string idempotencyKey,
        [FromBody] PushDispatchResolutionV1? request,
        CancellationToken ct = default)
    {
        if (!ValidKey(idempotencyKey)) return InvalidKey();
        if (request is null
            || request.Outcome is not ("succeeded" or "failed")
            || string.IsNullOrWhiteSpace(request.Note)
            || request.Note.Length > 2000
            || request.ResponseMessage?.Length > 2000
            || request.ObservedVersion <= 0
            || request.ObservedUpdatedAt == default)
        {
            Record("push_resolve", "validation");
            return Task.FromResult<IActionResult>(Problem(
                title: "Invalid push resolution",
                detail: "outcome must be succeeded or failed; note, observed_version, and observed_updated_at are required; observed_version must be positive and text fields are limited to 2000 characters.",
                statusCode: StatusCodes.Status400BadRequest));
        }

        var normalized = new PushDispatchResolutionV1
        {
            Outcome = request.Outcome,
            Note = request.Note.Trim(),
            ResponseMessage = string.IsNullOrWhiteSpace(request.ResponseMessage)
                ? null : request.ResponseMessage.Trim(),
            ObservedVersion = request.ObservedVersion,
            ObservedUpdatedAt = request.ObservedUpdatedAt,
        };
        return ExecuteAsync("push_resolve", async () => Ok(await _push.ResolveAsync(
            idempotencyKey.Trim(), normalized, ct)));
    }

    private async Task<IActionResult> ExecuteAsync(string operation, Func<Task<IActionResult>> action)
    {
        try
        {
            var result = await action();
            Record(operation, "success");
            _log.LogInformation(
                "event=case_recovery operation={Operation} outcome=success correlation_id={CorrelationId}",
                operation, HttpContext.TraceIdentifier);
            return result;
        }
        catch (PushDispatchRecoveryApiException error)
        {
            var status = error.StatusCode is 404 or 409
                ? error.StatusCode : StatusCodes.Status502BadGateway;
            Record(operation, error.StatusCode == 409 ? "conflict" : "upstream_failure");
            _log.LogWarning(
                "event=case_recovery operation={Operation} outcome=upstream_failure upstream_status={Status} "
                + "correlation_id={CorrelationId}",
                operation, error.StatusCode, HttpContext.TraceIdentifier);
            return Problem(
                title: status == 404 ? "Recovery record not found"
                    : status == 409 ? "Recovery conflict" : "Push recovery unavailable",
                detail: status == 409
                    ? PushConflictDetail(error.Detail)
                    : status == 404 ? "The requested recovery record was not found."
                    : "The private push recovery endpoint could not complete the request.",
                statusCode: status);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Record(operation, "upstream_failure");
            _log.LogError(error,
                "event=case_recovery operation={Operation} outcome=upstream_failure correlation_id={CorrelationId}",
                operation, HttpContext.TraceIdentifier);
            return Problem(
                title: "Push recovery unavailable",
                detail: "The private push recovery endpoint could not complete the request.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private async Task<IActionResult> ExecuteStateAsync(string operation, Func<Task<IActionResult>> action)
    {
        try
        {
            var result = await action();
            Record(operation, "success");
            _log.LogInformation(
                "event=case_recovery operation={Operation} outcome=success correlation_id={CorrelationId}",
                operation, HttpContext.TraceIdentifier);
            return result;
        }
        catch (GenericCaseApiException error)
        {
            var status = error.StatusCode is 400 or 403 or 404 or 409
                ? error.StatusCode : StatusCodes.Status502BadGateway;
            Record(operation, error.StatusCode == 409 ? "conflict" : "upstream_failure");
            _log.LogWarning(
                "event=case_recovery operation={Operation} outcome=upstream_failure upstream_status={Status} "
                + "correlation_id={CorrelationId}",
                operation, error.StatusCode, HttpContext.TraceIdentifier);
            return Problem(
                title: status switch
                {
                    400 => "Invalid recovery cursor or request",
                    404 => "Recovery record not found",
                    409 => "Recovery conflict",
                    _ => "Case callback recovery unavailable",
                },
                detail: status switch
                {
                    400 => "The recovery request was invalid.",
                    404 => "The requested callback dead letter was not found.",
                    409 => "The callback event cannot be requeued in its current state.",
                    _ => "The private state recovery endpoint could not complete the request.",
                },
                statusCode: status);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            Record(operation, "upstream_failure");
            _log.LogError(error,
                "event=case_recovery operation={Operation} outcome=upstream_failure correlation_id={CorrelationId}",
                operation, HttpContext.TraceIdentifier);
            return Problem(
                title: "Case callback recovery unavailable",
                detail: "The private state recovery endpoint could not complete the request.",
                statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private Task<IActionResult> InvalidKey()
    {
        Record("push_validation", "validation");
        return Task.FromResult<IActionResult>(Problem(
            title: "Invalid idempotency key",
            detail: "idempotencyKey is required and must be at most 200 characters.",
            statusCode: StatusCodes.Status400BadRequest));
    }

    private static bool ValidKey(string? value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 200;

    private static string PushConflictDetail(string? detail) => detail switch
    {
        "Dispatch claim is not stale" => detail,
        "Dispatch changed after it was observed" => detail,
        "Dispatch changed while resolution was being applied" => detail,
        not null when detail.StartsWith("Dispatch is already terminal as ", StringComparison.Ordinal) => detail,
        _ => "The dispatch is not stale, is terminal, or changed after it was observed.",
    };

    private static void Record(string operation, string outcome) =>
        BusinessOutcomeTelemetry.CaseRecoveryOperations.Add(
            1, new("operation", operation), new("outcome", outcome));
}
