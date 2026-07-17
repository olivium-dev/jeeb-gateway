using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Partner;
using JeebGateway.Partner.JeeberSearch;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using WalletApiException = JeebGateway.service.ServiceWallet.ApiException;

namespace JeebGateway.Controllers;

/// <summary>
/// Jeeb Partner Portal jeeber-lookup BFF (partner-wallet-bff). The read-only steps a partner performs
/// to pick a top-up destination before choosing an amount:
///
/// <list type="bullet">
///   <item><c>GET /v1/partner/jeebers/search?query={q}&amp;limit={n}</c> — PP-3 free-text search over
///   jeeber name/phone; returns candidates with a MASKED phone.</item>
///   <item><c>GET /v1/partner/jeebers/{jeeberId}/wallet-target</c> — is this jeeber a valid top-up
///   destination (does it have a provisioned wallet)?</item>
/// </list>
///
/// <para>ADR-0001 thin BFF: no state, no money math. Capability <c>partner.jeeber.lookup</c>
/// ({partner}) is the class-level ADR-005 marker covering both read actions. The wallet-target read
/// resolves against the reused wallet-service holder read; the search read goes to the user-management
/// microservice via <see cref="IPartnerJeeberSearchClient"/> (no direct DB access from the gateway).</para>
/// </summary>
[Route("v1/partner/jeebers")]
[RequireCapability(Capabilities.PartnerJeeberLookup)]
public sealed class PartnerJeebersController : PartnerControllerBase
{
    /// <summary>Minimum trimmed length of the free-text query (frozen PP-3 contract).</summary>
    private const int MinQueryLength = 2;

    /// <summary>Default result cap when <c>limit</c> is omitted (frozen PP-3 contract).</summary>
    private const int DefaultSearchLimit = 10;

    /// <summary>Hard ceiling on results, regardless of the requested <c>limit</c> (frozen PP-3 contract).</summary>
    private const int MaxSearchLimit = 20;

    private readonly IPartnerWalletService _partner;
    private readonly IPartnerJeeberSearchClient _search;
    private readonly ILogger<PartnerJeebersController> _log;

    public PartnerJeebersController(
        IPartnerWalletService partner,
        IPartnerJeeberSearchClient search,
        ILogger<PartnerJeebersController> log)
    {
        _partner = partner;
        _search = search;
        _log = log;
    }

    /// <summary>
    /// GET /v1/partner/jeebers/search?query={q}&amp;limit={n} — PP-3 free-text jeeber discovery.
    /// <para>Validation (frozen contract): <c>query</c> is required and its trimmed length must be
    /// &gt;= 2 (else 400 RFC 7807); <c>limit</c> is optional, defaults to 10 and is clamped to 20.
    /// Each result's phone is MASKED to the last four digits server-side. An upstream user-management
    /// failure maps to a sanitized 502 ProblemDetails (never a raw exception).</para>
    /// </summary>
    [HttpGet("search")]
    [ProducesResponseType(typeof(IReadOnlyList<PartnerJeeberSearchItem>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> Search(
        [FromQuery] string? query,
        [FromQuery] int? limit,
        CancellationToken ct)
    {
        // Caller must be an identified partner (Layer 1/2 already enforced); resolve to fail closed.
        if (!TryResolveCallerId(out _, out var failure)) return failure;

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length < MinQueryLength)
        {
            return Problem(
                title: $"query is required and must be at least {MinQueryLength} characters.",
                statusCode: StatusCodes.Status400BadRequest,
                type: "https://jeeb.dev/errors/invalid-search-query");
        }

        // Optional limit: default 10, clamped to [1, 20]. Clamp (not 400) mirrors the ledger paging
        // guard so a portal that over-asks still gets a bounded page, never an error.
        var effectiveLimit = limit is null
            ? DefaultSearchLimit
            : Math.Clamp(limit.Value, 1, MaxSearchLimit);

        try
        {
            var hits = await _search.SearchJeebersAsync(trimmed, effectiveLimit, ct);

            var items = new List<PartnerJeeberSearchItem>(hits.Count);
            foreach (var hit in hits)
            {
                items.Add(new PartnerJeeberSearchItem
                {
                    JeeberId = hit.JeeberId,
                    DisplayName = hit.DisplayName,
                    Phone = MaskPhone(hit.Phone),
                });
            }

            return Ok(items);
        }
        catch (PartnerJeeberSearchUpstreamException ex)
        {
            // Sanitized RFC 7807 — an upstream-down fault is a 502, NOT the base PartnerProblem 409.
            // The upstream status is logged server-side only, never echoed (no info disclosure).
            _log.LogWarning(ex,
                "Partner jeeber search: user-management call failed on {Path} → {Status}.",
                Request.Path, ex.UpstreamStatusCode);
            return Problem(
                title: "The jeeber search could not be completed.",
                statusCode: StatusCodes.Status502BadGateway,
                type: "https://jeeb.dev/errors/partner-jeeber-search-upstream");
        }
    }

    /// <summary>GET /v1/partner/jeebers/{jeeberId}/wallet-target — validate a jeeber top-up destination.</summary>
    [HttpGet("{jeeberId:guid}/wallet-target")]
    [ProducesResponseType(typeof(PartnerJeeberTargetResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public async Task<IActionResult> GetWalletTarget(Guid jeeberId, CancellationToken ct)
    {
        // Caller must be an identified partner (Layer 1/2 already enforced); resolve to fail closed
        // on a malformed identity even though the id is not otherwise used for this read.
        if (!TryResolveCallerId(out _, out var failure)) return failure;

        try
        {
            return Ok(await _partner.ResolveJeeberTargetAsync(jeeberId, ct));
        }
        catch (WalletApiException ex)
        {
            return UpstreamProblem(ex, _log);
        }
    }

    /// <summary>
    /// Mask a phone to the last four DIGITS (e.g. <c>"+970 79 123 4567" -&gt; "***4567"</c>). A partner
    /// never receives a full jeeber phone number. Returns empty when the hit carries no phone digits.
    /// </summary>
    private static string MaskPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return string.Empty;
        }

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (digits.Length == 0)
        {
            return string.Empty;
        }

        var last4 = digits.Length <= 4 ? digits : digits[^4..];
        return "***" + last4;
    }
}
