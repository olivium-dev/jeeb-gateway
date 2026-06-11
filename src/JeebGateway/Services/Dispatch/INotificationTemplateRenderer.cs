namespace JeebGateway.Services.Dispatch;

/// <summary>
/// Renders a notification template key into a localised title and body.
/// When the notification-service exposes a dedicated render endpoint this
/// interface can be backed by an HTTP call; for now a static catalog is used.
/// </summary>
public interface INotificationTemplateRenderer
{
    /// <summary>
    /// Returns <c>null</c> when the template key is unknown.
    /// </summary>
    RenderedNotification? Render(string templateKey, string locale, IReadOnlyDictionary<string, string> parameters);
}

public sealed record RenderedNotification(string Title, string Body);
