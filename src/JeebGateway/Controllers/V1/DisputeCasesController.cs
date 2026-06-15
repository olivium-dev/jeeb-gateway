using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Disputes.V2;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers.V1;

/// <summary>
/// T-BE-028 / JEB-64: dispute case orchestration (v1 surface).
///
/// <list type="bullet">
///   <item><c>POST /v1/deliveries/{id}/escalate</c> — opens a dispute
///     case for a delivery. Synchronously attaches the chat transcript
///     and GPS polyline as evidence (AC1).</item>
///   <item><c>GET /v1/disputes</c> — caller's cases (opener or
///     counter-party).</item>
///   <item><c>GET /v1/disputes/{id}</c> — single case lookup. Either
///     party of the delivery or any admin may read (AC4).</item>
///   <item><c>POST /admin/v1/disputes/{id}/resolve</c> — admin verdict
///     (<c>refund</c> with amount or <c>no_action</c>). Refund hits
///     <c>unified_payment_gateway</c>; both parties get a push (AC2).</item>
/// </list>
///
/// Coexists with the legacy <see cref="JeebGateway.Controllers.DisputesController"/>
/// (T-backend-025 / JEEB-43) — that surface keeps working, this one is
/// the additive v1 extension policy-aligned with the locked-in olivium
/// payments path (PO blocker review).
/// </summary>
[ApiController]
public sealed class DisputeCasesController : ControllerBase
{
    private const string EntityType = "dispute_case";
    private const string ActionEscalate = "escalate_case";
    private const string ActionResolve = "resolve_case";

    private readonly IDisputeCaseService _service;
    private readonly IAdminAuditLog _auditLog;

    public DisputeCasesController(IDisputeCaseService service, IAdminAuditLog auditLog)
    {
        _service = service;
        _auditLog = auditLog;
    }

    // -----------------------------------------------------------------
    // Open a case for a delivery (the filer is the authenticated user).
    // -----------------------------------------------------------------
    [HttpPost("v1/deliveries/{deliveryId}/escalate")]
    [RequireCapability(Capabilities.DisputeFile)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Escalate(
        string deliveryId,
        [FromBody] EscalateDeliveryRequest? body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        try
        {
            var result = await _service.EscalateAsync(new EscalateInput
            {
                DeliveryId = deliveryId,
                OpenedByUserId = userId,
                Reason = body.Reason ?? string.Empty,
                Comment = body.Comment,
                PhotoUrls = body.Photos ?? new List<string>(),
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim()
            }, ct);

            switch (result.Outcome)
            {
                case EscalateOutcome.DeliveryNotFound:
                    return NotFound(new ProblemDetails
                    {
                        Title = $"Delivery '{deliveryId}' not found.",
                        Status = StatusCodes.Status404NotFound
                    });

                case EscalateOutcome.NotAParty:
                    return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
                    {
                        Title = "not-a-party",
                        Detail = "Only a party to the delivery (its client or assigned jeeber) may escalate it.",
                        Status = StatusCodes.Status403Forbidden,
                        Type = "https://jeeb.dev/errors/dispute-not-a-party"
                    });

                case EscalateOutcome.AlreadyEscalated:
                    return Conflict(new ProblemDetails
                    {
                        Title = "An active dispute case already exists for this delivery.",
                        Detail = $"Existing case id: {result.Case!.Id}",
                        Status = StatusCodes.Status409Conflict,
                        Type = "https://jeeb.dev/errors/dispute-already-open"
                    });

                case EscalateOutcome.Replayed:
                    return Ok(DisputeCaseResponse.From(result.Case!));

                case EscalateOutcome.Created:
                default:
                    await _auditLog.AppendAsync(new AdminAuditAppend
                    {
                        AdminUserId = userId,
                        Action = ActionEscalate,
                        EntityType = EntityType,
                        EntityId = result.Case!.Id,
                        AfterState = Snapshot(result.Case),
                        RequestId = HttpContext.TraceIdentifier
                    }, ct);

                    return CreatedAtAction(
                        nameof(GetOne),
                        new { id = result.Case.Id },
                        DisputeCaseResponse.From(result.Case));
            }
        }
        catch (DisputeCaseValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
    }

    // -----------------------------------------------------------------
    // List caller's own cases (opener OR counter-party).
    // -----------------------------------------------------------------
    [HttpGet("v1/disputes")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    [ProducesResponseType(typeof(DisputeCaseListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var items = await _service.ListForUserAsync(userId, ct);
        return Ok(new DisputeCaseListResponse
        {
            Items = items.Select(DisputeCaseResponse.From).ToList(),
            Total = items.Count
        });
    }

    // -----------------------------------------------------------------
    // Single case lookup. Visibility: opener, counter-party, or admin.
    // -----------------------------------------------------------------
    [HttpGet("v1/disputes/{id}")]
    // ADR-005 L2 §G: coarse cap = dispute.read.mine {client, jeeber, admin}. Party/admin visibility
    // (opener|counterparty|admin) is STATE and stays in the action body — not an L2 policy.
    [RequireCapability(Capabilities.DisputeReadMine)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized))
        {
            return unauthorized;
        }

        var @case = await _service.GetAsync(id, ct);
        if (@case is null) return NotFound();

        var isAdmin = UserIdentity.IsAdmin(HttpContext);
        var isOpener = string.Equals(@case.OpenedByUserId, userId, StringComparison.Ordinal);
        var isCounterparty = !string.IsNullOrEmpty(@case.CounterpartyUserId)
            && string.Equals(@case.CounterpartyUserId, userId, StringComparison.Ordinal);

        if (!isAdmin && !isOpener && !isCounterparty)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden: dispute case belongs to a different delivery.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-resource"
            });
        }

        return Ok(DisputeCaseResponse.From(@case));
    }

    // -----------------------------------------------------------------
    // S14 / JEB-64 admin queue (T-CMS-004): cross-user dispute queue read.
    // Admin-only via [RequireCapability(dispute.read.queue)] (AdminOnly L2).
    // Optional ?state= filter (open | under_review | resolved-refund |
    // resolved-no-action | closed; hyphen or underscore both accepted).
    // -----------------------------------------------------------------
    [HttpGet("admin/v1/disputes")]
    [RequireCapability(Capabilities.DisputeReadQueue)]
    [ProducesResponseType(typeof(DisputeCaseListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> AdminQueue([FromQuery] string? state, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out _, out var unauthorized))
        {
            return unauthorized;
        }

        var items = await _service.ListAllAsync(state, ct);
        return Ok(new DisputeCaseListResponse
        {
            Items = items.Select(DisputeCaseResponse.From).ToList(),
            Total = items.Count
        });
    }

    // -----------------------------------------------------------------
    // S14 / JEB-64 state machine: admin claims a case for triage
    // (open → under_review). Admin-only via [RequireCapability(dispute.resolve)]
    // (the admin write capability). SM legality stays STATE in the service:
    // claiming a non-open case → 409 invalid-transition.
    // -----------------------------------------------------------------
    [HttpPost("admin/v1/disputes/{id}/review")]
    [RequireCapability(Capabilities.DisputeResolve)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Review(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
        {
            return unauthorized;
        }

        var result = await _service.ReviewAsync(new ReviewCaseInput
        {
            CaseId = id,
            AdminUserId = adminId
        }, ct);

        switch (result.Outcome)
        {
            case ReviewOutcome.NotFound:
                return NotFound();

            case ReviewOutcome.InvalidTransition:
                return Conflict(new ProblemDetails
                {
                    Title = "invalid-transition",
                    Detail = $"Case {id} cannot be claimed from state '{result.Case!.State}' (must be open).",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/dispute-invalid-transition"
                });

            case ReviewOutcome.Reviewed:
            default:
                await _auditLog.AppendAsync(new AdminAuditAppend
                {
                    AdminUserId = adminId,
                    Action = "review_case",
                    EntityType = EntityType,
                    EntityId = id,
                    AfterState = Snapshot(result.Case!),
                    RequestId = HttpContext.TraceIdentifier
                }, ct);
                return Ok(DisputeCaseResponse.From(result.Case!));
        }
    }

    // -----------------------------------------------------------------
    // S14 / JEB-64 terminal seal: admin closes a resolved case
    // (resolved_* → closed). Admin-only. SM legality stays STATE:
    // closing a non-resolved case → 409 invalid-transition (N6a).
    // -----------------------------------------------------------------
    [HttpPost("admin/v1/disputes/{id}/close")]
    [RequireCapability(Capabilities.DisputeResolve)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Close(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
        {
            return unauthorized;
        }

        var result = await _service.CloseAsync(new CloseCaseInput
        {
            CaseId = id,
            AdminUserId = adminId
        }, ct);

        switch (result.Outcome)
        {
            case CloseOutcome.NotFound:
                return NotFound();

            case CloseOutcome.InvalidTransition:
                return Conflict(new ProblemDetails
                {
                    Title = "invalid-transition",
                    Detail = $"Case {id} cannot be closed from state '{result.Case!.State}' (must be resolved first).",
                    Status = StatusCodes.Status409Conflict,
                    Type = "https://jeeb.dev/errors/dispute-invalid-transition"
                });

            case CloseOutcome.Closed:
            default:
                await _auditLog.AppendAsync(new AdminAuditAppend
                {
                    AdminUserId = adminId,
                    Action = "close_case",
                    EntityType = EntityType,
                    EntityId = id,
                    AfterState = Snapshot(result.Case!),
                    RequestId = HttpContext.TraceIdentifier
                }, ct);
                return Ok(DisputeCaseResponse.From(result.Case!));
        }
    }

    // -----------------------------------------------------------------
    // Admin resolve. ADR-005 L2: admin-only via [RequireCapability(dispute.resolve)]
    // (replaces the legacy [RequireRole(Roles.Admin)]). Refund-vs-no_action + payment
    // routing stay STATE in the owning service.
    // -----------------------------------------------------------------
    [HttpPost("admin/v1/disputes/{id}/resolve")]
    [RequireCapability(Capabilities.DisputeResolve)]
    [ProducesResponseType(typeof(DisputeCaseResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(
        string id,
        [FromBody] ResolveCaseRequest? body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized))
        {
            return unauthorized;
        }

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!TryParseDecision(body.Decision, out var decision, out var decisionError))
        {
            return BadRequest(decisionError);
        }

        try
        {
            var result = await _service.ResolveAsync(new ResolveCaseInput
            {
                CaseId = id,
                AdminUserId = adminId,
                Decision = decision,
                RefundUsd = body.RefundUsd,
                Notes = body.Notes,
                IdempotencyKey = string.IsNullOrWhiteSpace(idempotencyKey) ? null : idempotencyKey.Trim()
            }, ct);

            switch (result.Outcome)
            {
                case ResolveOutcome.NotFound:
                    return NotFound();

                case ResolveOutcome.AlreadyResolved:
                    return Conflict(new ProblemDetails
                    {
                        Title = "already_resolved",
                        Detail = $"Case {id} is in terminal state '{result.Case!.State}'.",
                        Status = StatusCodes.Status409Conflict,
                        Type = "https://jeeb.dev/errors/dispute-already-resolved"
                    });

                case ResolveOutcome.RefundFailed:
                    return StatusCode(StatusCodes.Status502BadGateway, new ProblemDetails
                    {
                        Title = "refund_failed",
                        Detail = result.FailureReason
                            ?? "Refund call to unified_payment_gateway failed; case left open for retry.",
                        Status = StatusCodes.Status502BadGateway,
                        Type = "https://jeeb.dev/errors/refund-failed"
                    });

                case ResolveOutcome.Replayed:
                case ResolveOutcome.Resolved:
                default:
                    if (result.Outcome == ResolveOutcome.Resolved)
                    {
                        await _auditLog.AppendAsync(new AdminAuditAppend
                        {
                            AdminUserId = adminId,
                            Action = ActionResolve,
                            EntityType = EntityType,
                            EntityId = id,
                            AfterState = Snapshot(result.Case!),
                            RequestId = HttpContext.TraceIdentifier
                        }, ct);
                    }
                    return Ok(DisputeCaseResponse.From(result.Case!));
            }
        }
        catch (DisputeCaseValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (DisputeCaseConflictException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    private static bool TryParseDecision(string? raw, out ResolveDecision decision, out ProblemDetails error)
    {
        decision = default;
        error = null!;

        if (string.IsNullOrWhiteSpace(raw))
        {
            error = new ProblemDetails
            {
                Title = "decision is required (refund or no_action).",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "refund":
            case "resolved_refund":
                decision = ResolveDecision.Refund;
                return true;
            case "no_action":
            case "no-action":
            case "noaction":
            case "resolved_no_action":
                decision = ResolveDecision.NoAction;
                return true;
            default:
                error = new ProblemDetails
                {
                    Title = $"Unknown decision '{raw}'. Allowed: refund, no_action.",
                    Status = StatusCodes.Status400BadRequest
                };
                return false;
        }
    }

    private static IReadOnlyDictionary<string, object?> Snapshot(DisputeCase c) =>
        new Dictionary<string, object?>
        {
            ["state"] = c.State,
            ["reason"] = c.Reason,
            ["opened_by_user_id"] = c.OpenedByUserId,
            ["delivery_id"] = c.DeliveryId,
            ["counterparty_user_id"] = c.CounterpartyUserId,
            ["resolved_at"] = c.ResolvedAt,
            ["resolver_admin_id"] = c.ResolverAdminId,
            ["resolution_notes"] = c.ResolutionNotes,
            ["refund_usd"] = c.RefundUsd,
            ["refund_ledger_entry_id"] = c.RefundLedgerEntryId,
            ["photo_count"] = c.PhotoUrls.Count,
            ["evidence_chat_messages"] = c.Evidence.ChatTranscriptMessageCount,
            ["evidence_gps_points"] = c.Evidence.GpsPolyline.Count,
            ["evidence_degraded"] = c.Evidence.Degraded
        };
}
