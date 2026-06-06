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

        try
        {
            var created = await _store.CreateAsync(new ProhibitedItemCreate
            {
                Name = body.Name!,
                Category = body.Category!,
                Description = body.Description
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

        try
        {
            var updated = await _store.UpdateAsync(id, new ProhibitedItemPatch
            {
                Name = body.Name,
                Category = body.Category,
                Description = body.Description,
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

    private static ProhibitedItemDto ToDto(ProhibitedItem i) => new()
    {
        Id = i.Id,
        Name = i.Name,
        Category = i.Category,
        Description = i.Description,
        Active = i.Active,
        CreatedAt = i.CreatedAt,
        UpdatedAt = i.UpdatedAt
    };
}
