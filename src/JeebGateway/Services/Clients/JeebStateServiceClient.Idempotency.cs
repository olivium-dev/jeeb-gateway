#nullable enable

namespace JeebGateway.Services.Clients
{
    // Hand-authored partial extensions for the NSwag-generated IJeebStateServiceClient /
    // JeebStateServiceClient. Add new state-service endpoints here rather than editing
    // the auto-generated file (which will be overwritten on the next NSwag regeneration).
    public partial interface IJeebStateServiceClient
    {
        /// <summary>
        /// List non-expired idempotency keys whose key starts with <paramref name="prefix"/>,
        /// newest-first. Used by owner-scoped list operations (e.g. support tickets
        /// keyed as "support-ticket-owner:{userId}:"). 400 when prefix is empty.
        /// </summary>
        System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<IdempotencyRecord>>
            FindIdempotencyKeysByPrefixAsync(string prefix, System.Threading.CancellationToken cancellationToken);
    }

    public partial class JeebStateServiceClient
    {
        public virtual async System.Threading.Tasks.Task<System.Collections.Generic.IReadOnlyList<IdempotencyRecord>>
            FindIdempotencyKeysByPrefixAsync(string prefix, System.Threading.CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(prefix))
                throw new System.ArgumentNullException(nameof(prefix));

            var client_ = _httpClient;
            using var request_ = new System.Net.Http.HttpRequestMessage();
            request_.Method = new System.Net.Http.HttpMethod("GET");
            request_.Headers.Accept.Add(
                System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

            var urlBuilder_ = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(_baseUrl)) urlBuilder_.Append(_baseUrl);
            urlBuilder_.Append("v1/state/idempotency/by-prefix?prefix=");
            urlBuilder_.Append(System.Uri.EscapeDataString(prefix));

            PrepareRequest(client_, request_, urlBuilder_);
            request_.RequestUri = new System.Uri(urlBuilder_.ToString(), System.UriKind.RelativeOrAbsolute);
            PrepareRequest(client_, request_, urlBuilder_.ToString());

            var response_ = await client_
                .SendAsync(request_, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            try
            {
                var headers_ = new System.Collections.Generic.Dictionary<
                    string, System.Collections.Generic.IEnumerable<string>>();
                foreach (var item_ in response_.Headers) headers_[item_.Key] = item_.Value;
                if (response_.Content?.Headers != null)
                    foreach (var item_ in response_.Content.Headers) headers_[item_.Key] = item_.Value;

                ProcessResponse(client_, response_);
                var status_ = (int)response_.StatusCode;
                if (status_ == 200)
                {
                    var obj_ = await ReadObjectResponseAsync<System.Collections.Generic.IReadOnlyList<IdempotencyRecord>>(
                        response_, headers_, cancellationToken).ConfigureAwait(false);
                    return obj_.Object
                        ?? System.Array.Empty<IdempotencyRecord>();
                }

                var body_ = response_.Content == null
                    ? string.Empty
                    : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                throw new JeebStateServiceApiException(
                    $"The HTTP status code of the response was not expected ({status_}).",
                    status_, body_, headers_, null);
            }
            finally
            {
                response_.Dispose();
            }
        }
    }
}
