namespace JeebGateway.service.ServicePushNotification
{
    public partial class ServicePushNotificationClient
    {
        // Relay guards register/delete behind X-Api-Key when INTERNAL_API_KEY is set (relay #22);
        // attach the same configured key on every generated call, without duplicating the topic seam's.
        partial void PrepareRequest(System.Net.Http.HttpClient client, System.Net.Http.HttpRequestMessage request, System.Text.StringBuilder urlBuilder)
        {
            if (!string.IsNullOrWhiteSpace(InternalApiKey) && !request.Headers.Contains("X-Api-Key"))
            {
                request.Headers.TryAddWithoutValidation("X-Api-Key", InternalApiKey);
            }
        }
    }
}
