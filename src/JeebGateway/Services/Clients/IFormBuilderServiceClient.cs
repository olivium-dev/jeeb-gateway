namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real <c>form-builder-service</c> (FastAPI / Python,
/// PostgreSQL-backed, "Form Builder Microservice" v1.0.0). The service is the
/// fleet-wide <b>dynamic forms</b> upstream (atlas pattern #15 "Dynamic forms",
/// atlas <c>services.json</c> entry <c>form-builder-service</c>) that renders
/// configuration-driven form templates + component definitions, localized by an
/// <c>Accept-Language</c> header.
///
/// <para>
/// DEPLOYMENT STATUS (read before wiring anything live). form-builder-service is
/// NOT yet on the Jeeb swarm — there is no live <c>192.168.2.50</c> port for it,
/// and <c>Services:FormBuilder:BaseUrl</c> in
/// <c>appsettings.Production.json</c> is a CLEARLY-MARKED PLACEHOLDER
/// (<c>http://192.168.2.50:PORT_TBD/</c>) pending deployment + port assignment.
/// Consequently the upstream feature flag (<c>FeatureFlags:UseUpstream:FormBuilder</c>)
/// defaults OFF in every environment, the gateway controller returns 503
/// ProblemDetails when off (net-new path — there is no legacy in-memory form
/// store to fall back to), and the readiness probe is LIVENESS-ONLY (no
/// <c>/health</c> route exists on the FastAPI app — it exposes only <c>/docs</c>
/// and <c>/openapi.json</c>), exactly mirroring the treatment feedback-service
/// got. Flip the flag on and set a real BaseUrl once the service is deployed.
/// </para>
///
/// <para>
/// CONTRACT MAPPING. Hand-coded against the verified FastAPI routes in
/// <c>form-builder-service/app/main.py</c> (the service publishes an OpenAPI doc
/// at <c>/openapi.json</c>, but it is not reachable from this build host, so this
/// follows the <see cref="INotificationServiceClient"/> / <see cref="IOfferServiceClient"/>
/// hand-coded precedent rather than an NSwag-generated artifact — and the KYC
/// schema seam the rating/KYC tickets need is a small read surface):
/// </para>
/// <list type="bullet">
///   <item><c>GET /templates</c> — all templates (localized).</item>
///   <item><c>GET /templates/{template_name}</c> — one template by name.</item>
///   <item><c>GET /templates/{template_name}/schema</c> — the template's render
///     schema. This is the seam JEB-40 / JEB-41 use for the versioned Jeeb
///     <b>KYC</b> form schema <c>jeeb_jeeber_v1</c> (template name), surfaced via
///     <see cref="SchemaAsync"/>.</item>
///   <item><c>GET /languages</c> — supported localization codes.</item>
/// </list>
///
/// The named "form-builder" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:FormBuilder:BaseUrl</c>) + the org-standard
/// bearer / X-Service-Auth / Polly resilience pipeline, so this class never
/// thinks about retry/timeout/circuit-breaker.
///
/// Tickets served: JEB-1441, JEB-1437, JEB-1430, JEB-626, JEB-507, JEB-40,
/// JEB-41 (versioned Jeeb KYC form schema <c>jeeb_jeeber_v1</c> via
/// <see cref="SchemaAsync"/>).
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IFormBuilderServiceClient
{
    /// <summary>
    /// Lists every available form template via <c>GET /templates</c>, localized
    /// by <paramref name="acceptLanguage"/> (ISO code, default "en").
    /// </summary>
    Task<IReadOnlyList<FormTemplateSummary>> ListTemplatesAsync(
        string acceptLanguage,
        CancellationToken ct);

    /// <summary>
    /// Reads a single template by name via <c>GET /templates/{templateName}</c>.
    /// Returns the raw template document the upstream emits (component tree),
    /// localized by <paramref name="acceptLanguage"/>.
    /// </summary>
    Task<FormTemplateDocument> GetTemplateAsync(
        string templateName,
        string acceptLanguage,
        CancellationToken ct);

    /// <summary>
    /// Reads a template's render schema via
    /// <c>GET /templates/{templateName}/schema</c>. This is the path the KYC
    /// tickets consume: <paramref name="templateName"/> = <c>jeeb_jeeber_v1</c>
    /// returns the versioned Jeeb Jeeber KYC form schema (JEB-40 / JEB-41).
    /// </summary>
    Task<FormSchemaDocument> SchemaAsync(
        string templateName,
        string acceptLanguage,
        CancellationToken ct);

    /// <summary>
    /// Lists the supported localization language codes via <c>GET /languages</c>.
    /// </summary>
    Task<IReadOnlyList<string>> ListLanguagesAsync(CancellationToken ct);
}

/// <summary>
/// Lightweight summary of a form template as returned in the <c>GET /templates</c>
/// list. The upstream template documents are configuration-driven and evolve
/// independently, so the gateway carries only the stable identity fields and
/// passes the full document through as raw JSON (<see cref="FormTemplateDocument"/>)
/// when callers need the component tree.
/// </summary>
public sealed class FormTemplateSummary
{
    /// <summary>Template name / id (e.g. <c>jeeb_jeeber_v1</c>).</summary>
    public string? Name { get; init; }

    /// <summary>Human-readable, localized title.</summary>
    public string? Title { get; init; }

    /// <summary>Optional version marker the upstream stamps on the template.</summary>
    public string? Version { get; init; }
}

/// <summary>
/// Raw passthrough of a single template document (<c>GET /templates/{name}</c>).
/// The component tree is upstream-defined and configuration-driven; the gateway
/// does not reshape it, so it is carried as a <see cref="System.Text.Json.JsonElement"/>
/// to stay forward-compatible with template evolution.
/// </summary>
public sealed class FormTemplateDocument
{
    public string? Name { get; init; }

    /// <summary>The full upstream template payload, unmodified.</summary>
    public System.Text.Json.JsonElement Document { get; init; }
}

/// <summary>
/// Raw passthrough of a template's render schema
/// (<c>GET /templates/{name}/schema</c>). For <c>jeeb_jeeber_v1</c> this is the
/// versioned Jeeb KYC form schema. Carried as a
/// <see cref="System.Text.Json.JsonElement"/> so schema evolution never requires
/// a gateway redeploy.
/// </summary>
public sealed class FormSchemaDocument
{
    public string? TemplateName { get; init; }

    /// <summary>The full upstream schema payload, unmodified.</summary>
    public System.Text.Json.JsonElement Schema { get; init; }
}
