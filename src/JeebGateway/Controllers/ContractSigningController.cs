using System.Text.Json;
using JeebGateway.Services;
using JeebGateway.Services.Clients;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace JeebGateway.Controllers;

/// <summary>
/// Thin BFF surface over the real <c>contract-signing-service</c> (FastAPI,
/// immutable contract templates + per-party signatures; olivium-shared), reached
/// through <see cref="IContractSigningServiceClient"/>. The gateway holds NO
/// contract state — every read/write resolves to the upstream's PostgreSQL store.
///
/// Gated by <c>FeatureFlags:UseUpstream:ContractSigning</c>. Because this path is
/// net-new (there is no legacy in-memory contract store to fall back to) AND
/// contract-signing-service is NOT yet deployed to the Jeeb swarm
/// (<c>Services:ContractSigning:BaseUrl</c> is a placeholder pending deployment),
/// the flag is a runtime kill switch and DEFAULTS OFF: when off, the endpoints
/// return 503 ProblemDetails rather than calling an unconfigured/undeployed
/// downstream.
///
/// Tickets served: JEB-1441, JEB-1437, JEB-1430, JEB-626, JEB-519, JEB-40,
/// JEB-41 (the Jeeb Terms-of-Service template <c>jeeb_tos_v1</c> is registered via
/// <see cref="RegisterTemplate"/> -> <see cref="IContractSigningServiceClient.RegisterTemplateAsync"/>
/// and accepted via <see cref="Sign"/> -> <see cref="IContractSigningServiceClient.SignAsync"/>).
/// </summary>
[ApiController]
[Route("contract-signing")]
public class ContractSigningController : ControllerBase
{
    private const int MaxIdLength = 256;

    private readonly IContractSigningServiceClient _contractSigning;
    private readonly IOptionsMonitor<UpstreamFeatureFlags> _flags;

    public ContractSigningController(
        IContractSigningServiceClient contractSigning,
        IOptionsMonitor<UpstreamFeatureFlags> flags)
    {
        _contractSigning = contractSigning;
        _flags = flags;
    }

    /// <summary>
    /// Registers an immutable contract template. Real path:
    /// <c>POST /v1/templates</c>. For <c>name = jeeb_tos_v1</c> this registers the
    /// versioned Jeeb Terms-of-Service template (JEB-40 / JEB-41).
    /// </summary>
    [HttpPost("templates")]
    [ProducesResponseType(typeof(ContractTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> RegisterTemplate(
        [FromBody] RegisterTemplateRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.Name))
        {
            return Problem(
                title: "Invalid template",
                detail: "A non-empty template name is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.StageModel.Count == 0)
        {
            return Problem(
                title: "Invalid template",
                detail: "stageModel must contain at least one stage.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.PartyRequirements.Count == 0)
        {
            return Problem(
                title: "Invalid template",
                detail: "partyRequirements must contain at least one role.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var template = await _contractSigning.RegisterTemplateAsync(request, ct);
        return Ok(template);
    }

    /// <summary>
    /// Lists registered contract templates. Real path: <c>GET /v1/templates</c> —
    /// the upstream's PostgreSQL-backed catalog read (paginated
    /// <c>{ "items": [...], "total", "limit", "offset" }</c> from
    /// <c>contract_templates</c>). This is the provable DB read through the gateway.
    ///
    /// Routed as the literal <c>templates/list</c> so it is matched ahead of the
    /// parameterized <c>templates/{templateId}</c> route (the upstream itself has no
    /// <c>/v1/templates/list</c> route — "list" would otherwise resolve to the
    /// by-id route and 404 — so the client deliberately calls the bare
    /// <c>/v1/templates</c> collection path).
    /// </summary>
    [HttpGet("templates/list")]
    [ProducesResponseType(typeof(JsonElement), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ListTemplates(CancellationToken ct = default)
    {
        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var templates = await _contractSigning.ListTemplatesAsync(ct);
        return Ok(templates);
    }

    /// <summary>
    /// Reads a single template by id. Real path:
    /// <c>GET /v1/templates/{templateId}</c>.
    /// </summary>
    [HttpGet("templates/{templateId}")]
    [ProducesResponseType(typeof(ContractTemplate), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetTemplate(string templateId, CancellationToken ct = default)
    {
        if (!IsValidId(templateId)) return InvalidId("template id");
        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var template = await _contractSigning.GetTemplateAsync(templateId, ct);
        return Ok(template);
    }

    /// <summary>
    /// Instantiates a contract from a template. Real path:
    /// <c>POST /v1/contracts</c>.
    /// </summary>
    [HttpPost("contracts")]
    [ProducesResponseType(typeof(Contract), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> CreateContract(
        [FromBody] CreateContractRequest request,
        CancellationToken ct = default)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.TemplateId))
        {
            return Problem(
                title: "Invalid contract",
                detail: "A non-empty templateId is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (request.Parties.Count == 0)
        {
            return Problem(
                title: "Invalid contract",
                detail: "At least one party is required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var contract = await _contractSigning.CreateContractAsync(request, ct);
        return Ok(contract);
    }

    /// <summary>
    /// Reads a single contract by id. Real path:
    /// <c>GET /v1/contracts/{contractId}</c>.
    /// </summary>
    [HttpGet("contracts/{contractId}")]
    [ProducesResponseType(typeof(Contract), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> GetContract(string contractId, CancellationToken ct = default)
    {
        if (!IsValidId(contractId)) return InvalidId("contract id");
        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var contract = await _contractSigning.GetContractAsync(contractId, ct);
        return Ok(contract);
    }

    /// <summary>
    /// Records a party's signature on a contract. Real path:
    /// <c>POST /v1/contracts/{contractId}/signatures</c>. This is the Jeeb ToS
    /// acceptance seam (JEB-41).
    /// </summary>
    [HttpPost("contracts/{contractId}/signatures")]
    [ProducesResponseType(typeof(Signature), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Sign(
        string contractId,
        [FromBody] SignRequest request,
        CancellationToken ct = default)
    {
        if (!IsValidId(contractId)) return InvalidId("contract id");
        if (request is null
            || string.IsNullOrWhiteSpace(request.RoleKey)
            || string.IsNullOrWhiteSpace(request.PartyRef))
        {
            return Problem(
                title: "Invalid signature",
                detail: "roleKey and partyRef are required.",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!_flags.CurrentValue.ContractSigning) return UpstreamDisabled();

        var signature = await _contractSigning.SignAsync(contractId, request, ct);
        return Ok(signature);
    }

    private static bool IsValidId(string? id) =>
        !string.IsNullOrWhiteSpace(id) && id.Length <= MaxIdLength;

    private IActionResult InvalidId(string label) => Problem(
        title: $"Invalid {label}",
        detail: $"The {label} must be non-empty and at most {MaxIdLength} characters.",
        statusCode: StatusCodes.Status400BadRequest);

    private IActionResult UpstreamDisabled() => Problem(
        title: "Contract-signing upstream disabled",
        detail: "FeatureFlags:UseUpstream:ContractSigning is off in this environment "
              + "(contract-signing-service is not yet deployed to the Jeeb swarm).",
        statusCode: StatusCodes.Status503ServiceUnavailable);
}
