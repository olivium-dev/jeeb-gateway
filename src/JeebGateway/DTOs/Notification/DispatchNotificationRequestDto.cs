using System.ComponentModel.DataAnnotations;

namespace JeebGateway.DTOs.Notification;

/// <summary>
/// Request body for <c>POST /api/notifications</c>.
/// </summary>
public sealed class DispatchNotificationRequestDto
{
    /// <summary>Template identifier, e.g. <c>jeeb.request.received</c>.</summary>
    [Required]
    public string TemplateKey { get; set; } = string.Empty;

    /// <summary>BCP-47 locale tag, e.g. <c>en</c> or <c>ar</c>. Defaults to <c>en</c>.</summary>
    public string Locale { get; set; } = "en";

    /// <summary>Template substitution parameters (e.g. <c>{"requestId": "abc-123"}</c>).</summary>
    public Dictionary<string, string> Parameters { get; set; } = new();

    /// <summary>Recipient user identifier.</summary>
    [Required]
    public Guid RecipientUserId { get; set; }
}
