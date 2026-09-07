using System.Net;
using System.Text;
using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.FormSubmissions;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Polly.CircuitBreaker;
using Polly.Timeout;

namespace JeebGateway.Controllers;

[ApiController]
[Route("form-builder")]
public sealed class FormSubmissionsBffController(
    IFormBuilderServiceClient formBuilder,
    IOptionsMonitor<UpstreamFeatureFlags> flags,
    IFormSubmissionStore store,
    IJeeberOnboardingCoverageResolver coverage) : ControllerBase
{
    private const int MaxSubmissionBytes = 16 * 1024;
    [HttpPost("templates/{templateName}/submit")]
    [RequireCapability(Capabilities.ProfileWriteSelf)]
    [Consumes("application/json")]
    [RequestSizeLimit(MaxSubmissionBytes)]
    [ProducesResponseType(typeof(FormSubmissionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Submit(string templateName, [FromBody] JsonElement body, CancellationToken ct)
    {
        if (!FormTemplateRegistry.IsKnown(templateName)) return Error(404, "not-found", "Unknown form template.");
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var identityError)) return identityError;
        if (!flags.CurrentValue.FormBuilder) return Error(503, "upstream-disabled", "Form builder is disabled.");
        if (body.ValueKind != JsonValueKind.Object) return Error(400, "invalid-request", "Body must be a JSON object.");
        if (Encoding.UTF8.GetByteCount(body.GetRawText()) > MaxSubmissionBytes)
            return Error(413, "invalid-request", "Submission exceeds the 16 KiB form budget.");
        FormSchemaDocument schema;
        FormTemplateDocument document;
        try
        {
            schema = await formBuilder.SchemaAsync(templateName, Language(), ct);
            document = await formBuilder.GetTemplateAsync(templateName, Language(), ct);
            if (TemplateSchemaValidator.Validate(schema.Schema, document.Document, body) is { } violation)
            {
                var problem = new ProblemDetails { Status = 400, Type = "https://jeeb.dev/errors/validation",
                    Title = "Invalid submission field", Detail = violation.Detail };
                problem.Extensions["field"] = violation.Field;
                return BadRequest(problem);
            }
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Error(404, "not-found", "Template is not registered.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or TimeoutException or TimeoutRejectedException or BrokenCircuitException)
        {
            return Error(502, "upstream-unavailable", "Form definition is temporarily unavailable.");
        }
        // The registry has one onboarding template; its required location is schema-owned.
        if (!body.TryGetProperty("home_base", out var home) || HomeBaseRules.Validate(home) is not null)
            return Error(502, "upstream-unavailable", "Form definition is missing its location contract.");
        var resolved = coverage.Resolve(home.GetProperty("lat").GetDouble(), home.GetProperty("lng").GetDouble());
        if (!resolved.InCoverage)
        {
            var problem = new ProblemDetails { Status = 409, Type = "https://jeeb.dev/errors/out_of_coverage",
                Title = "Outside the service area", Detail = "The home base is outside every served zone." };
            problem.Extensions["reasonCode"] = "out_of_coverage";
            return Conflict(problem);
        }
        var record = new FormSubmissionRecord(TemplateSchemaValidator.ToAnswers(body), DateTimeOffset.UtcNow, resolved.ZoneKey);
        try
        {
            // Idempotency-Key is accepted but ignored: this route upserts, not creates.
            await store.SetAsync(userId, templateName, record, ct);
            return StatusCode(201, ResponseFor(templateName, userId, schema.Schema, document.Document, record, resolved.Checked));
        }
        catch (TimeoutException) { return Error(504, "upstream-timeout", "Preferences service timed out."); }
        catch (UserPreferencesUnavailableException) { return Error(502, "dependency-unavailable", "Preferences service is unavailable."); }
    }

    [HttpGet("templates/{templateName}/submission")]
    [RequireCapability(Capabilities.ProfileReadSelf)]
    [ProducesResponseType(typeof(FormSubmissionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status504GatewayTimeout)]
    public async Task<IActionResult> Get(string templateName, CancellationToken ct)
    {
        if (!FormTemplateRegistry.IsKnown(templateName)) return Error(404, "not-found", "Unknown form template.");
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var identityError)) return identityError;
        if (!flags.CurrentValue.FormBuilder) return Error(503, "upstream-disabled", "Form builder is disabled.");
        FormSubmissionRecord? record;
        try { record = await store.GetAsync(userId, templateName, ct); }
        catch (TimeoutException) { return Error(504, "upstream-timeout", "Preferences service timed out."); }
        catch (UserPreferencesUnavailableException) { return Error(502, "dependency-unavailable", "Preferences service is unavailable."); }
        if (record is null) return Error(404, "not-found", "No completed submission.");
        try
        {
            var schema = await formBuilder.SchemaAsync(templateName, Language(), ct);
            var document = await formBuilder.GetTemplateAsync(templateName, Language(), ct);
            return Ok(ResponseFor(templateName, userId, schema.Schema, document.Document, record, record.ZoneKey is not null));
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Error(404, "not-found", "Template is not registered.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or InvalidOperationException or OperationCanceledException or TimeoutException or TimeoutRejectedException or BrokenCircuitException)
        {
            return Error(502, "upstream-unavailable", "Submission could not be reconstructed.");
        }
    }

    private static FormSubmissionResponse ResponseFor(string template, string user, JsonElement schema, JsonElement document,
        FormSubmissionRecord record, bool checkedCoverage) => new(template, user,
        TemplateSchemaValidator.Restore(schema, document, record.Answers), new CoverageDto(checkedCoverage, record.ZoneKey), record.SubmittedAt);
    private string Language() => FormSubmissionLanguage.Resolve(Request.Headers.AcceptLanguage.ToString());
    private ObjectResult Error(int status, string code, string title) =>
        Problem(statusCode: status, type: "https://jeeb.dev/errors/" + code, title: title);
}
