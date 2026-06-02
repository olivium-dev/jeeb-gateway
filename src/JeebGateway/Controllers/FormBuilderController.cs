using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF surface over the real <c>form-builder-service</c> (FastAPI, dynamic
/// form templates; atlas pattern #15 "Dynamic forms"), reached through
/// <see cref="IFormBuilderServiceClient"/>. The gateway holds NO form state —
/// every read resolves to the upstream's datastore.
///
/// These are catalog reads (form templates / schemas / supported languages),
/// not user-scoped writes, so there is no userId binding; localization is via the
/// inbound <c>Accept-Language</c> header (forwarded to the upstream's own
/// <c>accept_language</c> dependency).
///
/// Gated by <c>FeatureFlags:UseUpstream:FormBuilder</c>. Because this path is
/// net-new (there is no legacy in-memory form store to fall back to) AND
/// form-builder-service is NOT yet deployed to the Jeeb swarm
/// (<c>Services:FormBuilder:BaseUrl</c> is a placeholder pending deployment), the
/// flag is a runtime kill switch and DEFAULTS OFF: when off, the endpoints return
/// 503 ProblemDetails rather than calling an unconfigured/undeployed downstream.
///
/// Tickets served: JEB-1441, JEB-1437, JEB-1430, JEB-626, JEB-507, JEB-40,
/// JEB-41 (the versioned Jeeb KYC form schema <c>jeeb_jeeber_v1</c> is read via
/// <see cref="GetSchema"/> -> <see cref="IFormBuilderServiceClient.SchemaAsync"/>).
/// </summary>
[ApiController]
[Route("form-builder")]
public class FormBuilderController : ControllerBase
{
    private const int MaxTemplateNameLength = 256;

    private readonly IFormBuilderServiceClient _formBuilder;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public FormBuilderController(
        IFormBuilderServiceClient formBuilder,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _formBuilder = formBuilder;
        _flags = flags;
    }

    /// <summary>
    /// Lists every available form template. Real path: <c>GET /templates</c>.
    /// </summary>
    [HttpGet("templates")]
    [ProducesResponseType(typeof(IReadOnlyList<FormTemplateSummary>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListTemplates(CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.FormBuilder) return UpstreamDisabled();

        var templates = await _formBuilder.ListTemplatesAsync(AcceptLanguage(), ct);
        return Ok(templates);
    }

    /// <summary>
    /// Reads a single template by name. Real path:
    /// <c>GET /templates/{templateName}</c>.
    /// </summary>
    [HttpGet("templates/{templateName}")]
    [ProducesResponseType(typeof(FormTemplateDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTemplate(string templateName, CancellationToken ct = default)
    {
        if (!IsValidTemplateName(templateName)) return InvalidTemplateName();
        if (!_flags.CurrentValue.FormBuilder) return UpstreamDisabled();

        var template = await _formBuilder.GetTemplateAsync(templateName, AcceptLanguage(), ct);
        return Ok(template);
    }

    /// <summary>
    /// Reads a template's render schema. Real path:
    /// <c>GET /templates/{templateName}/schema</c>. For
    /// <c>templateName = jeeb_jeeber_v1</c> this is the versioned Jeeb Jeeber KYC
    /// form schema (JEB-40 / JEB-41).
    /// </summary>
    [HttpGet("templates/{templateName}/schema")]
    [ProducesResponseType(typeof(FormSchemaDocument), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSchema(string templateName, CancellationToken ct = default)
    {
        if (!IsValidTemplateName(templateName)) return InvalidTemplateName();
        if (!_flags.CurrentValue.FormBuilder) return UpstreamDisabled();

        var schema = await _formBuilder.SchemaAsync(templateName, AcceptLanguage(), ct);
        return Ok(schema);
    }

    /// <summary>
    /// Lists the supported localization language codes. Real path:
    /// <c>GET /languages</c>.
    /// </summary>
    [HttpGet("languages")]
    [ProducesResponseType(typeof(IReadOnlyList<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetLanguages(CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.FormBuilder) return UpstreamDisabled();

        var languages = await _formBuilder.ListLanguagesAsync(ct);
        return Ok(languages);
    }

    /// <summary>
    /// Resolves the localization language from the inbound <c>Accept-Language</c>
    /// header, falling back to the upstream default ("en") when absent.
    /// </summary>
    private string AcceptLanguage()
    {
        var header = Request.Headers.AcceptLanguage.ToString();
        return string.IsNullOrWhiteSpace(header) ? "en" : header;
    }

    private static bool IsValidTemplateName(string? templateName) =>
        !string.IsNullOrWhiteSpace(templateName) && templateName.Length <= MaxTemplateNameLength;

    private IActionResult InvalidTemplateName() => Problem(
        title: "Invalid template name",
        detail: $"Template name must be non-empty and at most {MaxTemplateNameLength} characters.",
        statusCode: StatusCodes.Status400BadRequest);

    private IActionResult UpstreamDisabled() => Problem(
        title: "Form-builder upstream disabled",
        detail: "FeatureFlags:UseUpstream:FormBuilder is off in this environment "
              + "(form-builder-service is not yet deployed to the Jeeb swarm).",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
