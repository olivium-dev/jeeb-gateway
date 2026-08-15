#nullable enable

namespace JeebGateway.Services.Clients
{
    // W6-03 — the generated operations still carry the legacy "v1/" segment because it is baked
    // into the vendored OpenAPI contract; strip it here so the wire uses the unversioned twin.
    public partial class JeebStateServiceClient
    {
        // Same NSwag seam ServicePushNotificationClient.ApiKey.cs uses, so the auto-generated
        // file is never edited and a regeneration cannot silently re-version the wire.
        partial void PrepareRequest(
            System.Net.Http.HttpClient client,
            System.Net.Http.HttpRequestMessage request,
            System.Text.StringBuilder urlBuilder)
        {
            var start = string.IsNullOrEmpty(_baseUrl) ? 0 : _baseUrl.Length;
            if (urlBuilder.Length >= start + 3
                && urlBuilder[start] == 'v'
                && urlBuilder[start + 1] == '1'
                && urlBuilder[start + 2] == '/')
            {
                urlBuilder.Remove(start, 3);
            }
        }
    }
}
