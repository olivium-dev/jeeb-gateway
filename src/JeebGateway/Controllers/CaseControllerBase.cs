using JeebGateway.Cases;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

public abstract class CaseControllerBase : ControllerBase
{
    protected IActionResult CaseProblem(Exception error, string kind = "unknown", string operation = "unknown")
    {
        var outcome = error switch
        {
            CaseValidationException => "validation",
            CaseAccessDeniedException => "forbidden",
            CaseNotFoundException => "not_found",
            CaseConflictException => "cas_conflict",
            GenericCaseApiException => "upstream_failure",
            _ => "failure",
        };
        CaseTelemetry.Requests.Add(1, new("kind", kind), new("operation", operation), new("outcome", outcome));
        return error switch
        {
            CaseValidationException e => Problem(e.Message, statusCode: StatusCodes.Status400BadRequest),
            CaseAccessDeniedException => Problem("You are not a party to this case.", statusCode: StatusCodes.Status403Forbidden),
            CaseNotFoundException => NotFound(),
            CaseConflictException e => ConflictProblem(e),
            GenericCaseApiException e => Problem(
                "The case service could not complete the request.",
                statusCode: e.StatusCode is >= 400 and < 600 ? e.StatusCode : StatusCodes.Status502BadGateway),
            _ => Problem("The case request could not be completed.", statusCode: StatusCodes.Status502BadGateway),
        };
    }

    private IActionResult ConflictProblem(CaseConflictException error)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status409Conflict,
            Title = "Conflict",
            Detail = "The case changed concurrently or an active case already exists.",
        };
        if (!string.IsNullOrWhiteSpace(error.ExistingCaseId))
        {
            problem.Extensions["existingCaseId"] = error.ExistingCaseId;
            problem.Extensions["caseId"] = error.ExistingCaseId;
        }
        if (!string.IsNullOrWhiteSpace(error.Kind)) problem.Extensions["kind"] = error.Kind;
        return Conflict(problem);
    }

    protected long RequireVersion(long? bodyVersion)
    {
        var suppliedIfMatch = Request.Headers.IfMatch.ToString();
        var hasIfMatch = !string.IsNullOrWhiteSpace(suppliedIfMatch);
        var validIfMatch = long.TryParse(suppliedIfMatch.Trim().Trim('"'), out var headerVersion)
            && headerVersion >= 1;
        if (bodyVersion is not null)
        {
            if (bodyVersion < 1)
                throw new CaseValidationException("expectedVersion must be at least 1.");
            if (hasIfMatch && (!validIfMatch || headerVersion != bodyVersion.Value))
                throw new CaseValidationException("expectedVersion and If-Match must agree.");
            return bodyVersion.Value;
        }
        if (validIfMatch) return headerVersion;
        throw new CaseValidationException("expectedVersion or If-Match is required.");
    }

    protected string RequireIdempotencyKey(string? supplied) =>
        !string.IsNullOrWhiteSpace(supplied)
            ? supplied.Trim()
            : throw new CaseValidationException("Idempotency-Key is required.");

}
