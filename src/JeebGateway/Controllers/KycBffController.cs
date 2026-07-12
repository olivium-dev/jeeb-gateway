using System.Net;
using System.Text.Json;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin Jeeb KYC BFF surface (S03 / JEB-40 / JEB-41). The gateway holds NO KYC
/// state here — both endpoints are pure PROJECTIONS of data already owned by the
/// shared upstreams:
/// <list type="bullet">
///   <item><see cref="GetFormSchema"/> projects the live
///     <c>form-builder-service</c> render schema for the versioned KYC template
///     <c>jeeb_jeeber_v1</c> into the Jeeb-shaped <c>{template_version, ...}</c>
///     envelope the mobile app consumes — wrapping
///     <see cref="IFormBuilderServiceClient.SchemaAsync"/>.</item>
///   <item><see cref="GetContractTemplate"/> resolves the Jeeb Terms-of-Service
///     template by NAME (<c>jeeb_tos_v1</c>) out of the live
///     <c>contract-signing-service</c> template catalog and projects
///     <c>{template_id, tos_version, document_url}</c> — wrapping
///     <see cref="IContractSigningServiceClient.ListTemplatesAsync"/>.</item>
/// </list>
///
/// No business logic, no persistence, no invented data: every field returned is
/// either read straight from the upstream payload or a deterministic projection
/// of it. The Jeeb domain semantics (the <c>type=tos -> jeeb_tos_v1</c> mapping,
/// the <c>variant -> jeeb_jeeber_v1</c> mapping, the response envelope shape) live
/// ONLY in this gateway controller, never in the shared upstreams (BR-1).
///
/// Each path is gated by the SAME upstream feature flag as its sibling generic
/// controller (<see cref="FormBuilderController"/> /
/// <see cref="ContractSigningController"/>), so it returns 503 ProblemDetails when
/// the upstream is disabled rather than dialing an unconfigured downstream.
/// </summary>
[ApiController]
// ADR-005 §A: the KYC form-schema and contract-template READS are classified public at L2
// (no user-type capability gate). [PublicEndpoint] opts the whole controller out of the L2
// capability layer and satisfies the default-deny coverage guard. It does NOT add
// [AllowAnonymous]: Layer-1 authentication is preserved exactly as today via the imperative
// UserIdentity.TryGetUserId in each action (still 401 without an identified caller). This is
// additive and behavior-preserving — no auth posture change.
[PublicEndpoint("KYC template/schema reads — L2-public per ADR-005 §A; L1 auth preserved in-action")]
public sealed class KycBffController : ControllerBase
{
    // The canonical Jeeb KYC form template name in form-builder-service.
    private const string JeebKycTemplateName = "jeeb_jeeber_v1";

    // The canonical Jeeb Terms-of-Service template NAME in
    // contract-signing-service (resolved to a service-minted template_id at
    // runtime — never a hardcoded literal id).
    private const string JeebTosTemplateName = "jeeb_tos_v1";

    // JEB-1473 — Jeeb-domain submission/approval-unlock wiring. This was leaked
    // into the shared form-builder-service (its removed GET
    // /templates/{name}/submission endpoint hardcoded
    // "auth-service:/api/jeeb/users/me/role" as the approval-unlock default).
    // Per Golden Rule 2 the shared service now holds only generic template data;
    // the Jeeb-domain downstream wiring is owned HERE in the gateway. On approve
    // the gateway appends the unlocked role in user-management (see KycBffSeam
    // review flow), so the canonical approval-unlock descriptor is the UM role
    // toggle route.
    private const string ApprovalUnlocksRoute = "user-management:/api/v1/users/me/role";

    // The submission seam metadata the shared step-based template declares
    // (endpoint/method/outputStatus). Kept here as the gateway-owned projection
    // so callers that previously read form-builder's /submission get the same
    // shape from the product gateway.
    private const string SubmissionEndpoint = "/kyc/submit";
    private const string SubmissionMethod = "POST";
    private const string SubmissionOutputStatus = "pending_review";

    private readonly IFormBuilderServiceClient _formBuilder;
    private readonly IContractSigningServiceClient _contractSigning;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public KycBffController(
        IFormBuilderServiceClient formBuilder,
        IContractSigningServiceClient contractSigning,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _formBuilder = formBuilder;
        _contractSigning = contractSigning;
        _flags = flags;
    }

    /// <summary>
    /// S03 H1 / A1 / A5. Fetches the dynamic Jeeb KYC form schema, projecting the
    /// live form-builder render schema for <c>jeeb_jeeber_v1</c> into the
    /// Jeeb-shaped envelope. The <c>variant</c> (national_id | passport |
    /// residency) selects the id-number validation flavour; the underlying field
    /// set is the same configuration-driven template.
    /// </summary>
    [HttpGet("v1/kyc/jeeb/form-schema")]
    [ProducesResponseType(typeof(JsonElement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetFormSchema(
        [FromQuery] string? variant,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        if (!_flags.CurrentValue.FormBuilder) return FormBuilderDisabled();

        var schema = await _formBuilder.SchemaAsync(JeebKycTemplateName, AcceptLanguage(), ct);

        // template_version: the versioned template identity the analytics +
        // tos_accepted_version round-trip key off (JEB-40). The upstream schema
        // does not stamp a version on the render payload, so the gateway derives
        // it deterministically from the template name (jeeb_jeeber_v1 -> "v1").
        var payload = new Dictionary<string, object?>
        {
            ["template_version"] = TemplateVersionFor(JeebKycTemplateName),
            ["template_name"] = JeebKycTemplateName,
            ["variant"] = string.IsNullOrWhiteSpace(variant) ? "national_id" : variant.Trim(),
            ["schema"] = schema.Schema,
        };

        return Ok(payload);
    }

    /// <summary>
    /// S03 H4 / A5.2. Fetches the Jeeb ToS contract template, resolving
    /// <c>jeeb_tos_v1</c> by name out of the live contract-signing catalog and
    /// projecting <c>{template_id, tos_version, document_url}</c>. The
    /// <c>template_id</c> is service-minted (read from the upstream), never a
    /// hardcoded literal.
    /// </summary>
    [HttpGet("v1/kyc/contract-template")]
    [ProducesResponseType(typeof(JsonElement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetContractTemplate(
        [FromQuery] string? type,
        CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        if (!_flags.CurrentValue.ContractSigning) return ContractSigningDisabled();

        var catalog = await _contractSigning.ListTemplatesAsync(ct);

        if (!TryResolveTosTemplate(catalog, out var template))
        {
            return Problem(
                title: "ToS template not found",
                detail: $"No contract-signing template named '{JeebTosTemplateName}' is registered.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var templateId = ReadString(template, "template_id");
        var locale = PreferredLocale();
        var documentUrl = ResolveDocumentUrl(template, locale);

        var payload = new Dictionary<string, object?>
        {
            ["template_id"] = templateId,
            ["tos_version"] = TemplateVersionFor(JeebTosTemplateName),
            ["document_url"] = documentUrl,
            ["locale"] = locale,
            ["name"] = JeebTosTemplateName,
        };

        return Ok(payload);
    }

    /// <summary>
    /// JEB-1473. Resolves the Jeeb KYC <b>submission / approval-unlock wiring</b>
    /// for the canonical template <c>jeeb_jeeber_v1</c>. This is the gateway-owned
    /// replacement for the form-builder-service <c>GET /templates/{name}/submission</c>
    /// endpoint that was removed (it leaked the hardcoded
    /// <c>auth-service:/api/jeeb/users/me/role</c> approval-unlock default into the
    /// shared service). The template existence is confirmed against the live
    /// upstream via the typed <see cref="IFormBuilderServiceClient.SchemaAsync"/>
    /// (Golden Rule 4 — BFF aggregation through the generated/typed client, no
    /// hand-rolled HttpClient); the downstream wiring (<c>approvalUnlocks</c>,
    /// accepted ToS template id) is composed HERE from the gateway-owned
    /// Jeeb-domain mapping (Golden Rule 2).
    /// </summary>
    [HttpGet("v1/kyc/jeeb/submission-wiring")]
    [ProducesResponseType(typeof(JsonElement), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetSubmissionWiring(CancellationToken ct = default)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized)) return unauthorized;
        if (!_flags.CurrentValue.FormBuilder) return FormBuilderDisabled();

        // Confirm the canonical template resolves upstream via the typed client.
        // A 404 from the upstream means the template is not registered as data in
        // the (now-generic) form-builder-service.
        try
        {
            _ = await _formBuilder.SchemaAsync(JeebKycTemplateName, AcceptLanguage(), ct);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return Problem(
                title: "KYC template not registered",
                detail: $"form-builder-service has no template registered under '{JeebKycTemplateName}'.",
                statusCode: StatusCodes.Status404NotFound);
        }

        var payload = new Dictionary<string, object?>
        {
            ["template_name"] = JeebKycTemplateName,
            ["canonical_template_id"] = JeebKycTemplateName,
            ["template_version"] = TemplateVersionFor(JeebKycTemplateName),
            ["submission"] = new Dictionary<string, object?>
            {
                ["endpoint"] = SubmissionEndpoint,
                ["method"] = SubmissionMethod,
                ["outputStatus"] = SubmissionOutputStatus,
                ["downstream"] = new Dictionary<string, object?>
                {
                    ["approvalUnlocks"] = ApprovalUnlocksRoute,
                    ["acceptedTosTemplateId"] = JeebTosTemplateName,
                },
            },
        };

        return Ok(payload);
    }

    // --- projection helpers (deterministic; no business state) -------------

    private static bool TryResolveTosTemplate(JsonElement catalog, out JsonElement template)
    {
        template = default;
        if (catalog.ValueKind != JsonValueKind.Object
            || !catalog.TryGetProperty("items", out var items)
            || items.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            var name = ReadString(item, "name");
            if (string.Equals(name, JeebTosTemplateName, StringComparison.OrdinalIgnoreCase))
            {
                template = item;
                return true;
            }
        }

        return false;
    }

    // Picks the media url whose locale matches the caller's preferred locale;
    // falls back to the first media entry. The upstream contract-signing MediaRef
    // schema carries the locale tag on "label" (seeded "en"/"ar"), so match that,
    // falling back to a legacy "locale" field if ever present. The signed-PDF mint
    // (https + expires_in<=300) is owned by cdn-service which is not yet wired
    // for ToS docs — the document_url here is the upstream's record-of-truth ref.
    private static string? ResolveDocumentUrl(JsonElement template, string locale)
    {
        if (!template.TryGetProperty("media", out var media) || media.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? firstUrl = null;
        foreach (var entry in media.EnumerateArray())
        {
            var url = ReadString(entry, "url");
            firstUrl ??= url;
            var entryLocale = ReadString(entry, "label") ?? ReadString(entry, "locale");
            if (string.Equals(entryLocale, locale, StringComparison.OrdinalIgnoreCase))
            {
                return url;
            }
        }

        return firstUrl;
    }

    private static string? ReadString(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(property, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    // jeeb_jeeber_v1 -> "v1"; jeeb_tos_v1 -> "v1". The trailing _vN segment is
    // the stable version marker baked into the canonical template name.
    private static string TemplateVersionFor(string templateName)
    {
        var idx = templateName.LastIndexOf("_v", StringComparison.OrdinalIgnoreCase);
        return idx >= 0 ? templateName[(idx + 1)..] : templateName;
    }

    private string AcceptLanguage()
    {
        var header = Request.Headers.AcceptLanguage.ToString();
        return string.IsNullOrWhiteSpace(header) ? "en" : header;
    }

    private string PreferredLocale()
    {
        var header = Request.Headers.AcceptLanguage.ToString();
        if (string.IsNullOrWhiteSpace(header)) return "en";
        // Take the primary subtag of the first language range (e.g. "ar-LB,ar;q=0.9" -> "ar").
        var first = header.Split(',')[0].Split(';')[0].Trim();
        var primary = first.Split('-')[0].Trim();
        return string.IsNullOrWhiteSpace(primary) ? "en" : primary.ToLowerInvariant();
    }

    private IActionResult FormBuilderDisabled() => Problem(
        title: "Form-builder upstream disabled",
        detail: "FeatureFlags:UseUpstream:FormBuilder is off in this environment.",
        statusCode: StatusCodes.Status503ServiceUnavailable);

    private IActionResult ContractSigningDisabled() => Problem(
        title: "Contract-signing upstream disabled",
        detail: "FeatureFlags:UseUpstream:ContractSigning is off in this environment.",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
