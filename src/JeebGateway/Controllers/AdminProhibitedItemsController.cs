using System.Text.RegularExpressions;
using JeebGateway.Auth.Capabilities;
using JeebGateway.ProhibitedItems;
using JeebGateway.Users;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

[Obsolete("Migrating to BFF aggregation: see GATEWAY-REMEDIATION-PLAN.md. Do not add new endpoints; consume the NSwag-generated client from Services/Generated/ via the named HttpClient registered in Extensions/ServiceClientExtensions.cs.")]
[ApiController]
[Route("admin/prohibited-items")]
// ADR-005 L2: all CRUD on the admin prohibited-items catalog is one admin capability
// (prohibited.manage), declared class-level (replaces class [RequireRole(Roles.Admin)]).
[RequireCapability(Capabilities.ProhibitedManage)]
public class AdminProhibitedItemsController : ControllerBase
{
    private const int DefaultPageSize = 20;
    private const int MaxPageSize = 100;
    private const int MaxNameLength = 200;
    private const int MaxDescriptionLength = 2000;

    // Mirrors prohibited_items_category_format check in 0005.
    private static readonly Regex CategoryFormat =
        new("^[a-z][a-z0-9_]{1,47}$", RegexOptions.Compiled);

    private readonly IProhibitedItemsStore _store;

    public AdminProhibitedItemsController(IProhibitedItemsStore store)
    {
        _store = store;
    }

    [HttpGet]
    [ProducesResponseType(typeof(AdminProhibitedItemsListResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = DefaultPageSize,
        CancellationToken ct = default)
    {
        if (page < 1)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "page must be >= 1.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        if (pageSize < 1 || pageSize > MaxPageSize)
        {
            return BadRequest(new ProblemDetails
            {
                Title = $"pageSize must be between 1 and {MaxPageSize}.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var result = await _store.ListAllAsync(page, pageSize, ct);
        return Ok(new AdminProhibitedItemsListResponse
        {
            Items = result.Items.Select(ToDto).ToList(),
            Page = page,
            PageSize = pageSize,
            Total = result.Total
        });
    }

    [HttpGet("{id}")]
    [ProducesResponseType(typeof(ProhibitedItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string id, CancellationToken ct)
    {
        var item = await _store.GetAsync(id, ct);
        if (item is null) return NotFound();

        return Ok(ToDto(item));
    }

    [HttpPost]
    [ProducesResponseType(typeof(ProhibitedItemDto), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] ProhibitedItemCreateRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body is null) return BadRequestBody();

        if (ValidateName(body.Name, out var nameError) is false) return nameError!;
        if (ValidateCategory(body.Category, out var categoryError) is false) return categoryError!;
        if (ValidateDescription(body.Description, out var descError) is false) return descError!;
        if (TryParseSeverity(body.Severity, ProhibitedSeverity.Block, out var severity, out var sevError) is false) return sevError!;

        try
        {
            var created = await _store.CreateAsync(new ProhibitedItemCreate
            {
                Name = body.Name!,
                Category = body.Category!,
                Description = body.Description,
                Severity = severity
            }, adminId, ct);

            return CreatedAtAction(nameof(Get), new { id = created.Id }, ToDto(created));
        }
        catch (DuplicateProhibitedItemNameException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    [HttpPatch("{id}")]
    [ProducesResponseType(typeof(ProhibitedItemDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        string id,
        [FromBody] ProhibitedItemUpdateRequest body,
        CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body is null) return BadRequestBody();

        if (body.Name is not null && ValidateName(body.Name, out var nameError) is false) return nameError!;
        if (body.Category is not null && ValidateCategory(body.Category, out var categoryError) is false) return categoryError!;
        if (body.Description is not null && ValidateDescription(body.Description, out var descError) is false) return descError!;
        ProhibitedSeverity? severityPatch = null;
        if (body.Severity is not null)
        {
            if (TryParseSeverity(body.Severity, ProhibitedSeverity.Block, out var parsed, out var sevError) is false) return sevError!;
            severityPatch = parsed;
        }

        try
        {
            var updated = await _store.UpdateAsync(id, new ProhibitedItemPatch
            {
                Name = body.Name,
                Category = body.Category,
                Description = body.Description,
                Severity = severityPatch,
                Active = body.Active
            }, adminId, ct);

            if (updated is null) return NotFound();

            return Ok(ToDto(updated));
        }
        catch (DuplicateProhibitedItemNameException ex)
        {
            return Conflict(new ProblemDetails
            {
                Title = ex.Message,
                Status = StatusCodes.Status409Conflict
            });
        }
    }

    /// <summary>
    /// WS-06 (ADM-03): bulk-import prohibited items in one call. Each row is validated
    /// independently; a bad or duplicate row is reported as a per-row outcome and does NOT
    /// abort the batch (partial success is the expected admin ergonomics for a paste-in list).
    /// Reuses <see cref="ValidateName"/>/<see cref="ValidateCategory"/>/<see cref="TryParseSeverity"/>
    /// rules and the store's duplicate-name guard so semantics match single-item create exactly.
    /// </summary>
    [HttpPost("bulk-import")]
    [ProducesResponseType(typeof(ProhibitedItemBulkImportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> BulkImport([FromBody] ProhibitedItemBulkImportRequest body, CancellationToken ct)
    {
        if (!UserIdentity.TryGetUserId(HttpContext, out var adminId, out var problem)) return problem;

        if (body?.Items is null || body.Items.Count == 0)
        {
            return BadRequest(new ProblemDetails
            {
                Title = "items is required and must contain at least one row.",
                Status = StatusCodes.Status400BadRequest
            });
        }

        var results = new List<ProhibitedItemBulkImportRowResult>(body.Items.Count);
        var imported = 0;
        var skipped = 0;

        for (var i = 0; i < body.Items.Count; i++)
        {
            var row = body.Items[i];

            var rowError = ValidateBulkRow(row);
            if (rowError is not null)
            {
                skipped++;
                results.Add(new ProhibitedItemBulkImportRowResult
                {
                    Index = i,
                    Outcome = "invalid",
                    Name = row?.Name,
                    Error = rowError
                });
                continue;
            }

            // ValidateBulkRow guarantees Name/Category are non-null and severity parses.
            TryParseSeverity(row!.Severity, ProhibitedSeverity.Block, out var severity, out _);

            try
            {
                var created = await _store.CreateAsync(new ProhibitedItemCreate
                {
                    Name = row.Name!,
                    Category = row.Category!,
                    Description = row.Description,
                    Severity = severity
                }, adminId, ct);

                imported++;
                results.Add(new ProhibitedItemBulkImportRowResult
                {
                    Index = i,
                    Outcome = "created",
                    Id = created.Id,
                    Name = created.Name
                });
            }
            catch (DuplicateProhibitedItemNameException ex)
            {
                skipped++;
                results.Add(new ProhibitedItemBulkImportRowResult
                {
                    Index = i,
                    Outcome = "duplicate",
                    Name = row.Name,
                    Error = ex.Message
                });
            }
        }

        return Ok(new ProhibitedItemBulkImportResponse
        {
            Imported = imported,
            Skipped = skipped,
            Total = body.Items.Count,
            Results = results
        });
    }

    /// <summary>
    /// Validates a single bulk-import row reusing the same rules as single-item create.
    /// Returns null when valid, or a human-readable error string for the per-row outcome.
    /// </summary>
    private string? ValidateBulkRow(ProhibitedItemBulkImportRow? row)
    {
        if (row is null) return "row is null.";
        if (string.IsNullOrWhiteSpace(row.Name)) return "name is required and cannot be blank.";
        if (row.Name.Length > MaxNameLength) return $"name must be {MaxNameLength} characters or fewer.";
        if (string.IsNullOrWhiteSpace(row.Category)) return "category is required.";
        if (!CategoryFormat.IsMatch(row.Category)) return "category must match ^[a-z][a-z0-9_]{1,47}$ (lowercase slug, 2-48 chars).";
        if (row.Description is { Length: > MaxDescriptionLength }) return $"description must be {MaxDescriptionLength} characters or fewer.";
        if (!string.IsNullOrWhiteSpace(row.Severity))
        {
            var s = row.Severity.Trim().ToLowerInvariant();
            if (s is not ("warn" or "block")) return "severity must be 'warn' or 'block'.";
        }
        return null;
    }

    private bool ValidateName(string? name, out IActionResult? error)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "name is required and cannot be blank.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        if (name.Length > MaxNameLength)
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"name must be {MaxNameLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateCategory(string? category, out IActionResult? error)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "category is required.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        if (!CategoryFormat.IsMatch(category))
        {
            error = BadRequest(new ProblemDetails
            {
                Title = "category must match ^[a-z][a-z0-9_]{1,47}$ (lowercase slug, 2-48 chars).",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private bool ValidateDescription(string? description, out IActionResult? error)
    {
        if (description is { Length: > MaxDescriptionLength })
        {
            error = BadRequest(new ProblemDetails
            {
                Title = $"description must be {MaxDescriptionLength} characters or fewer.",
                Status = StatusCodes.Status400BadRequest
            });
            return false;
        }

        error = null;
        return true;
    }

    private IActionResult BadRequestBody() => BadRequest(new ProblemDetails
    {
        Title = "Request body is required.",
        Status = StatusCodes.Status400BadRequest
    });

    /// <summary>
    /// Parses the wire severity string ("warn"|"block", case-insensitive) for
    /// JEB-63. A null/blank value yields <paramref name="fallback"/>; any other
    /// unrecognised value is a 400. Additive validation — existing callers that
    /// omit severity hit the fallback and are unaffected.
    /// </summary>
    private bool TryParseSeverity(string? wire, ProhibitedSeverity fallback, out ProhibitedSeverity severity, out IActionResult? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(wire))
        {
            severity = fallback;
            return true;
        }

        switch (wire.Trim().ToLowerInvariant())
        {
            case "warn":
                severity = ProhibitedSeverity.Warn;
                return true;
            case "block":
                severity = ProhibitedSeverity.Block;
                return true;
            default:
                severity = fallback;
                error = BadRequest(new ProblemDetails
                {
                    Title = "severity must be 'warn' or 'block'.",
                    Detail = $"got '{wire}'",
                    Status = StatusCodes.Status400BadRequest
                });
                return false;
        }
    }

    private static ProhibitedItemDto ToDto(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Severity = i.Severity.ToString().ToLowerInvariant(),
        Active = i.Active,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt
    };
}
