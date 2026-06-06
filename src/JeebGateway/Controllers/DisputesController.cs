using JeebGateway.Admin;
using JeebGateway.Auth.Capabilities;
using JeebGateway.Disputes;
using JeebGateway.Requests;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// T-backend-025 / JEEB-43: dispute reporting API.
///
/// <list type="bullet">
///   <item>POST /deliveries/{id}/dispute — Client or Jeeber files a
///     dispute against a delivery (one open dispute per delivery).</item>
///   <item>GET /disputes — returns every dispute the caller filed.</item>
///   <item>GET /disputes/{id} — single dispute lookup. Filers may read
///     only their own rows; admins may read any row.</item>
///   <item>PUT /admin/disputes/{id}/resolve — admin transitions the case
///     through <c>filed → under_review → resolved | dismissed</c> and
///     records the resolution notes.</item>
/// </list>
///
/// Photos: the mobile app uploads bytes to upload-service directly and
/// then hands the resulting URLs to this endpoint, mirroring how parcel
/// photos already work on <see cref="DeliveryRequest.Photos"/>. The cap
/// is 3 photos per dispute (<see cref="DisputeService.MaxPhotos"/>).
///
/// Notifications: state changes fan out via <see cref="JeebGateway.Push.IPushNotificationService"/>,
/// the same pipeline every other gateway trigger uses (which proxies
/// to push-notification in production via the unified push pipeline).
/// </summary>
[ApiController]
public class DisputesController : ControllerBase
{
    private const string EntityType = "dispute";
    private const string ActionFile = "file_dispute";
    private const string ActionOpen = "open_dispute_review";
    private const string ActionResolve = "resolve_dispute";
    private const string ActionDismiss = "dismiss_dispute";

    private readonly IDisputeService _service;
    private readonly IDisputeStore _store;
    private readonly IRequestsStore _requests;
    private readonly IAdminAuditLog _auditLog;

    public DisputesController(
        IDisputeService service,
        IDisputeStore store,
        IRequestsStore requests,
        IAdminAuditLog auditLog)
    {
        _service = service;
        _store = store;
        _requests = requests;
        _auditLog = auditLog;
    }

    [HttpPost("deliveries/{deliveryId}/dispute")]
    [RequireCapability(Capabilities.DisputeFile)]
    [ProducesResponseType(typeof(DisputeResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> File(
        string deliveryId,
        [FromBody] FileDisputeRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        // Verify the delivery exists. The dispute references a delivery id;
        // dangling references would muddle the admin queue.
        var delivery = await _requests.GetAsync(deliveryId, ct);
        if (delivery is null) return NotFound();

        try
        {
            var dispute = await _service.FileAsync(new FileDisputeInput
            {
                DeliveryId = deliveryId,
                FiledByUserId = userId,
                Category = body.Category ?? string.Empty,
                Description = body.Description ?? string.Empty,
                PhotoUrls = body.PhotoUrls ?? new List<string>()
            }, ct);

            await _auditLog.AppendAsync(new AdminAuditAppend
            {
                AdminUserId = userId,
                Action = ActionFile,
                EntityType = EntityType,
                EntityId = dispute.Id,
                AfterState = Snapshot(dispute),
                RequestId = HttpContext.TraceIdentifier
            }, ct);

            return CreatedAtAction(nameof(GetOne), new { id = dispute.Id }, DisputeResponse.From(dispute));
        }
        catch (DisputeValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (DisputeConflictException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpGet("disputes")]
    [RequireCapability(Capabilities.DisputeReadMine)]
    [ProducesResponseType(typeof(DisputeListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ListMine(CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        var items = await _service.ListForUserAsync(userId, ct);
        return Ok(new DisputeListResponse
        {
            Items = items.Select(DisputeResponse.From).ToList(),
            Total = items.Count
        });
    }

    [HttpGet("disputes/{id}")]
    // ADR-005 L2 §G: coarse cap = dispute.read.mine {client, jeeber, admin}. The own-row-vs-admin
    // visibility (IsAdmin reads any row, filer reads only their own) is STATE/ownership and
    // stays in the action body below — never expressed as an L2 policy.
    [RequireCapability(Capabilities.DisputeReadMine)]
    [ProducesResponseType(typeof(DisputeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetOne(string id, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var userId, out var unauthorized)) return unauthorized;

        var dispute = await _service.GetAsync(id, ct);
        if (dispute is null) return NotFound();

        // Filers may read only their own rows; admins read any row. This
        // mirrors the visibility model used by /admin/kyc/queue.
        var isAdmin = UserIdentity.IsAdmin(HttpContext);
        if (!isAdmin && !string.Equals(dispute.FiledByUserId, userId, StringComparison.Ordinal))
        {
            return StatusCode(StatusCodes.Status403Forbidden, new ProblemDetails
            {
                Title = "Forbidden: dispute belongs to a different user.",
                Status = StatusCodes.Status403Forbidden,
                Type = "https://jeeb.dev/errors/forbidden-resource"
            });
        }

        return Ok(DisputeResponse.From(dispute));
    }

    [HttpPut("admin/disputes/{id}/resolve")]
    [RequireCapability(Capabilities.DisputeResolve)]
    [ProducesResponseType(typeof(DisputeResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Resolve(
        string id,
        [FromBody] ResolveDisputeRequest? body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var unauthorized)) return unauthorized;

        if (body is null)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "request body is required.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (!TryParseAction(body.Action, out var action, out var actionError))
        {
            return BadRequest(actionError);
        }

        var before = await _store.GetByIdAsync(id, ct);
        if (before is null) return NotFound();

        Dispute? updated;
        try
        {
            updated = await _service.ResolveAsync(id, new ResolveDisputeInput
            {
                Action = action,
                AdminUserId = adminId,
                Resolution = body.Resolution
            }, ct);
        }
        catch (DisputeValidationException ex)
        {
            return BadRequest(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status400BadRequest
            });
        }
        catch (DisputeConflictException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }

        if (updated is null) return NotFound();

        await _auditLog.AppendAsync(new AdminAuditAppend
        {
            AdminUserId = adminId,
            Action = AuditActionFor(action),
            EntityType = EntityType,
            EntityId = id,
            BeforeState = Snapshot(before),
            AfterState = Snapshot(updated),
            RequestId = HttpContext.TraceIdentifier
        }, ct);

        return Ok(DisputeResponse.From(updated));
    }

    private static bool TryParseAction(string? raw, out DisputeResolveAction action, out ProblemDetails error)
    {
        action = default;
        error = null!;
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = new ProblemDetails
            {
                Title = "action is required (open, resolve, or dismiss).",
                Status = StatusCodes.Status400BadRequest
            };
            return false;
        }

        switch (raw.Trim().ToLowerInvariant())
        {
            case "open":
            case "under_review":
                action = DisputeResolveAction.Open;
                return true;
            case "resolve":
            case "resolved":
                action = DisputeResolveAction.Resolve;
                return true;
            case "dismiss":
            case "dismissed":
                action = DisputeResolveAction.Dismiss;
                return true;
            default:
                error = new ProblemDetails
                {
                    Title = $"Unknown action '{raw}'. Allowed: open, resolve, dismiss.",
                    Status = StatusCodes.Status400BadRequest
                };
                return false;
        }
    }

    private static string AuditActionFor(DisputeResolveAction action) => action switch
    {
        DisputeResolveAction.Open => ActionOpen,
        DisputeResolveAction.Resolve => ActionResolve,
        DisputeResolveAction.Dismiss => ActionDismiss,
        _ => action.ToString()
    };

    private static IReadOnlyDictionary<string, object?> Snapshot(Dispute d) =>
        new Dictionary<string, object?>
        {
            ["state"] = d.State,
            ["category"] = d.Category,
            ["filed_by_user_id"] = d.FiledByUserId,
            ["delivery_id"] = d.DeliveryId,
            ["reviewed_at"] = d.ReviewedAt,
            ["resolver_admin_id"] = d.ResolverAdminId,
            ["resolution"] = d.Resolution,
            ["photo_count"] = d.PhotoUrls.Count
        };
}
