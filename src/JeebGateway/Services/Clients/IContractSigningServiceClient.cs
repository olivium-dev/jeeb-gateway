using System.Text.Json;

namespace JeebGateway.Services.Clients;

/// <summary>
/// Typed proxy over the real <c>contract-signing-service</c> (FastAPI / Python,
/// PostgreSQL-backed, "Contract Signing Service") — the fleet-wide
/// <b>contract / e-signature</b> upstream (atlas <c>services.json</c> entry
/// <c>contract-signing-service</c>, olivium-shared product line). The service
/// models immutable contract <i>templates</i> (an ordered stage model +
/// per-role party requirements), instantiates <i>contracts</i> from a template,
/// and records per-party <i>signatures</i> with an optional proof reference.
///
/// <para>
/// DEPLOYMENT STATUS (read before wiring anything live). contract-signing-service
/// is NOT yet on the Jeeb swarm — there is no live <c>192.168.2.50</c> port for it,
/// and <c>Services:ContractSigning:BaseUrl</c> /
/// <c>ServiceContractSigningApi:BaseUrl</c> in <c>appsettings.Production.json</c>
/// are CLEARLY-MARKED PLACEHOLDERS (<c>http://192.168.2.50:PORT_TBD/</c>) pending
/// deployment + port assignment. Consequently the upstream feature flag
/// (<c>FeatureFlags:UseUpstream:ContractSigning</c>) defaults OFF in every
/// environment, the gateway controller returns 503 ProblemDetails when off
/// (net-new path — there is no legacy in-memory contract store to fall back to),
/// and NO readiness probe is registered (the gateway treats it as liveness-only
/// until a real BaseUrl exists; the upstream <i>does</i> expose <c>GET /health</c>,
/// so once deployed a real readiness probe can be added). This mirrors the
/// treatment feedback-service and form-builder-service got. Flip the flag on and
/// set a real BaseUrl once the service is deployed.
/// </para>
///
/// <para>
/// CONTRACT MAPPING. Hand-coded against the verified FastAPI routes in
/// <c>contract-signing-service/app/routers/*.py</c> (api_prefix <c>/v1</c>). The
/// service publishes an OpenAPI doc at <c>/openapi.json</c>, but it is not yet
/// deployed, so it is unreachable from this build host — this therefore follows
/// the <see cref="INotificationServiceClient"/> / <see cref="IOfferServiceClient"/>
/// hand-coded precedent rather than an NSwag-generated artifact. The gateway
/// surfaces only the small write/read seam the Jeeb ToS tickets need:
/// </para>
/// <list type="bullet">
///   <item><c>POST /v1/templates</c> — register an immutable contract template.
///     The Jeeb Terms-of-Service template <c>jeeb_tos_v1</c> (template name) is
///     registered through <see cref="RegisterTemplateAsync"/> (JEB-40 / JEB-41).</item>
///   <item><c>GET /v1/templates/{templateId}</c> — read a template by id, via
///     <see cref="GetTemplateAsync"/>.</item>
///   <item><c>POST /v1/contracts</c> — instantiate a contract from a template,
///     via <see cref="CreateContractAsync"/>.</item>
///   <item><c>GET /v1/contracts/{contractId}</c> — read a contract by id, via
///     <see cref="GetContractAsync"/>.</item>
///   <item><c>POST /v1/contracts/{contractId}/signatures</c> — record a party's
///     signature, via <see cref="SignAsync"/> (the Jeeb ToS acceptance seam).</item>
/// </list>
///
/// The named "contract-signing" HttpClient (registered in
/// <see cref="JeebGateway.Extensions.ServiceClientExtensions"/>) supplies the
/// BaseAddress (<c>Services:ContractSigning:BaseUrl</c>) + the org-standard
/// bearer / X-Service-Auth / Polly resilience pipeline, so this class never
/// thinks about retry/timeout/circuit-breaker.
///
/// Tickets served: JEB-1441, JEB-1437, JEB-1430, JEB-626, JEB-519, JEB-40,
/// JEB-41 (the Jeeb ToS template <c>jeeb_tos_v1</c> is registered via
/// <see cref="RegisterTemplateAsync"/> and accepted via <see cref="SignAsync"/>).
///
/// All methods throw <see cref="HttpRequestException"/> on non-2xx.
/// </summary>
public interface IContractSigningServiceClient
{
    /// <summary>
    /// Registers an immutable contract template via <c>POST /v1/templates</c>.
    /// This is the path JEB-40 / JEB-41 use to register the versioned Jeeb
    /// Terms-of-Service template <c>jeeb_tos_v1</c> (<paramref name="request"/>'s
    /// <see cref="RegisterTemplateRequest.Name"/>).
    /// </summary>
    Task<ContractTemplate> RegisterTemplateAsync(
        RegisterTemplateRequest request,
        CancellationToken ct);

    /// <summary>
    /// Lists registered contract templates via <c>GET /v1/templates</c> — the
    /// upstream's PostgreSQL-backed catalog read (a paginated
    /// <c>{ "items": [...], "total", "limit", "offset" }</c> envelope from
    /// <c>contract_templates</c>). Carried through verbatim as
    /// <see cref="System.Text.Json.JsonElement"/> so the gateway never couples to
    /// the (configuration-driven) template payload shape.
    /// </summary>
    Task<System.Text.Json.JsonElement> ListTemplatesAsync(CancellationToken ct);

    /// <summary>
    /// Reads a single template by id via <c>GET /v1/templates/{templateId}</c>.
    /// </summary>
    Task<ContractTemplate> GetTemplateAsync(
        string templateId,
        CancellationToken ct);

    /// <summary>
    /// Instantiates a contract from a template via <c>POST /v1/contracts</c>.
    /// </summary>
    Task<Contract> CreateContractAsync(
        CreateContractRequest request,
        CancellationToken ct);

    /// <summary>
    /// Reads a single contract by id via <c>GET /v1/contracts/{contractId}</c>.
    /// </summary>
    Task<Contract> GetContractAsync(
        string contractId,
        CancellationToken ct);

    /// <summary>
    /// Records a party's signature on a contract via
    /// <c>POST /v1/contracts/{contractId}/signatures</c>. For the Jeeb ToS this
    /// is the acceptance seam: a party (<paramref name="request"/>'s
    /// <see cref="SignRequest.PartyRef"/>) signs the role
    /// (<see cref="SignRequest.RoleKey"/>) on the <c>jeeb_tos_v1</c>-derived
    /// contract (JEB-41).
    /// </summary>
    Task<Signature> SignAsync(
        string contractId,
        SignRequest request,
        CancellationToken ct);
}

// --- request / response DTOs (verified against contract-signing-service schemas) ---

/// <summary>
/// One required party role on a template (upstream <c>PartyRequirement</c>):
/// e.g. <c>role_key=acceptor</c>, <c>required_count=1</c>. Optional
/// <see cref="SignatureOrder"/> enforces a signing sequence.
/// </summary>
public sealed class PartyRequirement
{
    public string RoleKey { get; init; } = string.Empty;
    public int RequiredCount { get; init; } = 1;
    public int? SignatureOrder { get; init; }
}

/// <summary>
/// Body for <c>POST /v1/templates</c> (upstream <c>CreateTemplateRequest</c>).
/// For the Jeeb ToS, <see cref="Name"/> = <c>jeeb_tos_v1</c>,
/// <see cref="StageModel"/> = the ordered acceptance stages, and
/// <see cref="PartyRequirements"/> = the single accepting party.
/// </summary>
public sealed class RegisterTemplateRequest
{
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }

    /// <summary>Ordered list of stage names (upstream requires non-empty).</summary>
    public IReadOnlyList<string> StageModel { get; init; } = new List<string>();

    /// <summary>Per-role party requirements (upstream requires at least one).</summary>
    public IReadOnlyList<PartyRequirement> PartyRequirements { get; init; } = new List<PartyRequirement>();
}

/// <summary>
/// A template document (upstream <c>TemplateResponse</c>). The variable,
/// configuration-driven media/references/payload-hints are carried verbatim as
/// <see cref="JsonElement"/> so the gateway never couples to their shape.
/// </summary>
public sealed class ContractTemplate
{
    public string? TemplateId { get; init; }
    public string? Name { get; init; }
    public string? Status { get; init; }
    public IReadOnlyList<string>? StageModel { get; init; }

    /// <summary>The full upstream template payload, unmodified.</summary>
    public JsonElement Document { get; init; }
}

/// <summary>One party binding on a contract (upstream <c>ContractPartySchema</c>).</summary>
public sealed class ContractParty
{
    public string RoleKey { get; init; } = string.Empty;
    public string PartyRef { get; init; } = string.Empty;
}

/// <summary>
/// The acting principal recorded on a contract write (upstream <c>ActorInfo</c>).
/// contract-signing-service requires this on <c>POST /v1/contracts</c> (it stamps
/// the version-1 payload snapshot's actor). For the Jeeb ToS ceremony the actor is
/// the signing user themselves (<c>type="PARTY"</c>, <c>ref=userId</c>).
/// </summary>
public sealed class ActorInfo
{
    public string Type { get; init; } = "PARTY";
    public string Ref { get; init; } = string.Empty;
    public string? Reason { get; init; }
}

/// <summary>
/// Body for <c>POST /v1/contracts</c> (upstream <c>CreateContractRequest</c>):
/// instantiate <see cref="TemplateId"/> with the listed <see cref="Parties"/>.
/// <see cref="Actor"/> is REQUIRED by the upstream (it records the actor on the
/// initial payload snapshot); omitting it yields an upstream 422.
/// </summary>
public sealed class CreateContractRequest
{
    public string TemplateId { get; init; } = string.Empty;
    public IReadOnlyList<ContractParty> Parties { get; init; } = new List<ContractParty>();
    public ActorInfo Actor { get; init; } = new();
}

/// <summary>
/// A contract document (upstream <c>ContractResponse</c>). The variable payload
/// / cancellation envelopes are carried verbatim as <see cref="JsonElement"/>.
/// </summary>
public sealed class Contract
{
    public string? ContractId { get; init; }
    public string? TemplateId { get; init; }
    public string? Status { get; init; }
    public string? Stage { get; init; }

    /// <summary>The full upstream contract payload, unmodified.</summary>
    public JsonElement Document { get; init; }
}

/// <summary>
/// Body for <c>POST /v1/contracts/{contractId}/signatures</c> (upstream
/// <c>CreateSignatureRequest</c>): the <see cref="RoleKey"/> the
/// <see cref="PartyRef"/> is signing, with an optional verifiable proof ref.
/// </summary>
public sealed class SignRequest
{
    public string RoleKey { get; init; } = string.Empty;
    public string PartyRef { get; init; } = string.Empty;
    public string? SignatureProofRef { get; init; }
}

/// <summary>A recorded signature (upstream <c>SignatureResponse</c>).</summary>
public sealed class Signature
{
    public string? ContractId { get; init; }
    public string? RoleKey { get; init; }
    public string? PartyRef { get; init; }
    public DateTimeOffset? SignedAt { get; init; }
    public string? SignatureProofRef { get; init; }
}
