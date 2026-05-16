using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace JeebGateway.Push;

/// <summary>
/// Production FCM HTTP v1 transport. Sends notifications via the
/// https://fcm.googleapis.com/v1/projects/{project}/messages:send endpoint.
///
/// Auth: uses a pre-configured bearer token (service account OAuth2 token)
/// injected via <see cref="PushOptions.FcmBearerToken"/>. Production deployments
/// should rotate this via a short-lived token from Google's OAuth2 metadata
/// endpoint; for the MVP we accept a long-lived token from config/secrets.
///
/// The transport is cancellation-aware (linked CTS from the unified service)
/// and maps HTTP failures to <see cref="PushTransportException"/> so the retry
/// pipeline handles them uniformly.
/// </summary>
public sealed class FcmPushTransport : IPushTransport
{
    private readonly HttpClient _http;
    private readonly PushOptions _options;
    private readonly ILogger<FcmPushTransport> _log;

    public FcmPushTransport(
        HttpClient http,
        IOptions<PushOptions> options,
        ILogger<FcmPushTransport> log)
    {
        _http = http;
        _options = options.Value;
        _log = log;
    }

    public DevicePlatform Platform => DevicePlatform.Fcm;

    public async Task SendAsync(DeviceToken device, PushNotificationRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // T-backend-029 AC #6: surface the recipient's persisted language to
        // the device so the client app can route to the correct in-app screen
        // (and FCM can pick a localised notification channel where set up).
        var dataPayload = request.Data?.ToDictionary(kv => kv.Key, kv => kv.Value)
            ?? new Dictionary<string, string>();
        if (!string.IsNullOrEmpty(request.Language))
        {
            dataPayload["language"] = request.Language;
        }

        var payload = new FcmSendRequest
        {
            Message = new FcmMessage
            {
                Token = device.Token,
                Notification = new FcmNotification
                {
                    Title = request.Title,
                    Body = request.Body
                },
                Data = dataPayload.Count == 0 ? null : dataPayload,
                Android = new FcmAndroidConfig
                {
                    Priority = "high",
                    Notification = new FcmAndroidNotification
                    {
                        ClickAction = "FLUTTER_NOTIFICATION_CLICK",
                        ChannelId = ChannelIdFor(request.Trigger)
                    }
                }
            }
        };

        var url = $"https://fcm.googleapis.com/v1/projects/{_options.FcmProjectId}/messages:send";

        using var httpReq = new HttpRequestMessage(HttpMethod.Post, url);
        httpReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.FcmBearerToken);
        httpReq.Content = JsonContent.Create(payload, options: FcmJsonOptions);

        HttpResponseMessage? resp = null;
        try
        {
            resp = await _http.SendAsync(httpReq, ct);

            if (resp.IsSuccessStatusCode)
            {
                _log.LogDebug(
                    "FCM delivered to {Token} for user {UserId}, trigger {Trigger}",
                    device.Token, device.UserId, request.Trigger);
                return;
            }

            var errorBody = await resp.Content.ReadAsStringAsync(ct);
            throw new PushTransportException(
                $"FCM returned {(int)resp.StatusCode}: {errorBody}");
        }
        catch (PushTransportException) { throw; }
        catch (OperationCanceledException) { throw; }
        catch (HttpRequestException ex)
        {
            throw new PushTransportException($"FCM request failed: {ex.Message}", ex);
        }
        finally
        {
            resp?.Dispose();
        }
    }

    private static string ChannelIdFor(NotificationTrigger trigger) => trigger switch
    {
        NotificationTrigger.Chat => "chat_messages",
        NotificationTrigger.NewOffer or NotificationTrigger.OfferAccepted => "delivery_updates",
        NotificationTrigger.StatusChange => "delivery_updates",
        NotificationTrigger.Promotion => "promotions",
        NotificationTrigger.KycUpdate => "account_updates",
        NotificationTrigger.RatingReminder => "reminders",
        _ => "default"
    };

    private static readonly JsonSerializerOptions FcmJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

#region FCM payload DTOs

internal sealed class FcmSendRequest
{
    [JsonPropertyName("message")]
    public FcmMessage Message { get; init; } = null!;
}

internal sealed class FcmMessage
{
    [JsonPropertyName("token")]
    public string Token { get; init; } = string.Empty;

    [JsonPropertyName("notification")]
    public FcmNotification? Notification { get; init; }

    [JsonPropertyName("data")]
    public Dictionary<string, string>? Data { get; init; }

    [JsonPropertyName("android")]
    public FcmAndroidConfig? Android { get; init; }
}

internal sealed class FcmNotification
{
    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("body")]
    public string Body { get; init; } = string.Empty;
}

internal sealed class FcmAndroidConfig
{
    [JsonPropertyName("priority")]
    public string Priority { get; init; } = "high";

    [JsonPropertyName("notification")]
    public FcmAndroidNotification? Notification { get; init; }
}

internal sealed class FcmAndroidNotification
{
    [JsonPropertyName("click_action")]
    public string? ClickAction { get; init; }

    [JsonPropertyName("channel_id")]
    public string? ChannelId { get; init; }
}

#endregion
