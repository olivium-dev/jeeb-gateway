using System.Security.Claims;
using JeebGateway.Push;
using Microsoft.AspNetCore.Mvc;

namespace JeebGateway.Controllers;

/// <summary>
/// Internal HTTP surface for the push pipeline (T-backend-022). Downstream
/// services that emit trigger events (offer-service, chat-service, KYC
/// admin panel, rating-reminder sweepers) hit this endpoint via the BFF
/// NSwag-generated client; mobile clients only ever talk to the device
/// registration endpoint exposed on this controller.
/// </summary>
[ApiController]
[Route("push")]
public class PushController : ControllerBase
{
    private readonly IPushNotificationService _push;
    private readonly IDeviceTokenStore _devices;
    private readonly IPushDeliveryTracker _tracker;

    public PushController(IPushNotificationService push, IDeviceTokenStore devices, IPushDeliveryTracker tracker)
    {
        _push = push;
        _devices = devices;
        _tracker = tracker;
    }

    [HttpPost("send")]
    [ProducesResponseType(typeof(SendPushResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> Send([FromBody] SendPushRequest body, CancellationToken ct)
    {
        if (body is null || string.IsNullOrWhiteSpace(body.UserId) || string.IsNullOrWhiteSpace(body.Trigger))
        {
            return Problem(
                title: "userId and trigger are required",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<NotificationTrigger>(body.Trigger, ignoreCase: true, out var trigger))
        {
            return Problem(
                title: $"Unknown trigger '{body.Trigger}'",
                detail: "Valid values: " + string.Join(", ", Enum.GetNames<NotificationTrigger>()),
                statusCode: StatusCodes.Status400BadRequest);
        }

        var request = new PushNotificationRequest(
            UserId: body.UserId,
            Trigger: trigger,
            Title: body.Title ?? string.Empty,
            Body: body.Body ?? string.Empty,
            Data: body.Data,
            IdempotencyKey: body.IdempotencyKey);

        var result = await _push.SendAsync(request, ct);

        return Accepted(new SendPushResponse
        {
            UserId = result.UserId,
            Trigger = result.Trigger.ToString(),
            Outcome = result.Outcome.ToString(),
            AttemptsMade = result.AttemptsMade,
            Reason = result.Reason
        });
    }

    [HttpPost("devices")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> RegisterDevice([FromBody] RegisterDeviceRequest body, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;

        if (body is null || string.IsNullOrWhiteSpace(body.Platform) || string.IsNullOrWhiteSpace(body.Token))
        {
            return Problem(
                title: "platform and token are required",
                statusCode: StatusCodes.Status400BadRequest);
        }

        if (!Enum.TryParse<DevicePlatform>(body.Platform, ignoreCase: true, out var platform))
        {
            return Problem(
                title: $"Unknown platform '{body.Platform}'",
                detail: "Valid values: fcm, apns",
                statusCode: StatusCodes.Status400BadRequest);
        }

        await _devices.RegisterAsync(new DeviceToken(userId, platform, body.Token), ct);
        return NoContent();
    }

    [HttpDelete("devices/{token}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UnregisterDevice(string token, CancellationToken ct)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;
        await _devices.UnregisterAsync(userId, token, ct);
        return NoContent();
    }

    [HttpGet("deliveries")]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryTrackingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetDeliveries([FromQuery] int limit = 50, CancellationToken ct = default)
    {
        if (!TryGetUserId(out var userId, out var problem)) return problem;

        var results = await _tracker.GetForUserAsync(userId, ct);
        var response = results
            .Take(limit)
            .Select(r => new DeliveryTrackingResponse
            {
                UserId = r.UserId,
                Trigger = r.Trigger.ToString(),
                Outcome = r.Outcome.ToString(),
                AttemptsMade = r.AttemptsMade,
                Reason = r.Reason
            })
            .ToArray();

        return Ok(response);
    }

    [HttpGet("deliveries/recent")]
    [ProducesResponseType(typeof(IReadOnlyList<DeliveryTrackingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetRecentDeliveries([FromQuery] int limit = 100, CancellationToken ct = default)
    {
        var results = await _tracker.GetRecentAsync(limit, ct);
        var response = results.Select(r => new DeliveryTrackingResponse
        {
            UserId = r.UserId,
            Trigger = r.Trigger.ToString(),
            Outcome = r.Outcome.ToString(),
            AttemptsMade = r.AttemptsMade,
            Reason = r.Reason
        }).ToArray();

        return Ok(response);
    }

    private bool TryGetUserId(out string userId, out IActionResult problem)
    {
        var fromClaim = User?.FindFirstValue(ClaimTypes.NameIdentifier)
                        ?? User?.FindFirstValue("sub");
        if (!string.IsNullOrWhiteSpace(fromClaim))
        {
            userId = fromClaim;
            problem = null!;
            return true;
        }

        if (Request.Headers.TryGetValue("X-User-Id", out var header) && !string.IsNullOrWhiteSpace(header))
        {
            userId = header.ToString();
            problem = null!;
            return true;
        }

        userId = string.Empty;
        problem = Unauthorized();
        return false;
    }
}
