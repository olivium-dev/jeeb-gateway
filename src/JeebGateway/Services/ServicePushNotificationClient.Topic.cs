// ---------------------------------------------------------------------------
// HAND-WRITTEN partial extension of the NSwag-generated
// ServicePushNotificationClient (Services/ServicePushNotificationClient.cs,
// namespace JeebGateway.service.ServicePushNotification).
//
// WHY THIS IS HAND-WRITTEN AND NOT REGENERATED:
// The committed push-notification OpenAPI contract this gateway generated its
// client from is a PLACEHOLDER that does NOT expose the topic route
// (POST api/v1/sent-payload/topic/{topic_name}). The deployed push service at
// :10040 DOES host that route (it is the same FCM relay that already backs
// send-to-user / send-to-device / broadcast — all present in the generated
// client). Until the OpenAPI spec is refreshed to include the topic operation
// and the client is regenerated, this seam lives here as a hand-written partial
// so it is NOT clobbered by a future `nswag run` (per nswag-client-generation:
// "never modify generated files; use partial class extensions").
//
// It deliberately mirrors the generated Send_notification_to_userAsync idiom
// byte-for-byte (StringContent + Newtonsoft serialization, PrepareRequest /
// ProcessResponse hooks, ReadObjectResponseAsync<T>, the 201/404/422/else
// status ladder) so that when the spec is regenerated the two are drop-in
// interchangeable and this file can simply be deleted.
//
// BUILD-NEWREQ-PUSH — added for the "finding jeebers" topic broadcast
// (NewRequestPushNotifier fans a new-request push to the jeeb_jeebers topic).
// ---------------------------------------------------------------------------

namespace JeebGateway.service.ServicePushNotification
{
    using System = global::System;

    public partial class ServicePushNotificationClient
    {
        /// <summary>
        /// Optional internal API key forwarded as the <c>X-Api-Key</c> header on the
        /// topic send. Sourced from config <c>PushNotificationServiceApi:InternalApiKey</c>
        /// at DI-registration time (see Program.cs). When null/blank the header is
        /// omitted — the LAN-local relay accepts unauthenticated calls today, so this
        /// stays optional and additive.
        /// </summary>
        public string? InternalApiKey { get; set; }

        /// <summary>
        /// Send a notification to an FCM topic (product audience group). Mirrors the
        /// generated <c>Send_notification_to_userAsync</c> shape.
        /// </summary>
        public virtual System.Threading.Tasks.Task<SentPayloadResponse> Send_notification_to_topicAsync(string topicName, SentPayloadToTopicRequest body)
        {
            return Send_notification_to_topicAsync(topicName, body, System.Threading.CancellationToken.None);
        }

        /// <param name="cancellationToken">A cancellation token that can be used by other objects or threads to receive notice of cancellation.</param>
        /// <summary>
        /// Sent To Specific Topic
        /// </summary>
        /// <returns>Successful Response</returns>
        /// <exception cref="ApiException">A server side error occurred.</exception>
        public virtual async System.Threading.Tasks.Task<SentPayloadResponse> Send_notification_to_topicAsync(string topicName, SentPayloadToTopicRequest body, System.Threading.CancellationToken cancellationToken)
        {
            if (topicName == null)
                throw new System.ArgumentNullException("topicName");

            if (body == null)
                throw new System.ArgumentNullException("body");

            var client_ = _httpClient;
            var disposeClient_ = false;
            try
            {
                using (var request_ = new System.Net.Http.HttpRequestMessage())
                {
                    var json_ = Newtonsoft.Json.JsonConvert.SerializeObject(body, JsonSerializerSettings);
                    var content_ = new System.Net.Http.StringContent(json_);
                    content_.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
                    request_.Content = content_;
                    request_.Method = new System.Net.Http.HttpMethod("POST");
                    request_.Headers.Accept.Add(System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

                    // Optional internal auth — only when a key is configured.
                    if (!string.IsNullOrWhiteSpace(InternalApiKey))
                    {
                        request_.Headers.TryAddWithoutValidation("X-Api-Key", InternalApiKey);
                    }

                    var urlBuilder_ = new System.Text.StringBuilder();
                    if (!string.IsNullOrEmpty(_baseUrl)) urlBuilder_.Append(_baseUrl);
                    // Operation Path: "api/v1/sent-payload/topic/{topic_name}"
                    urlBuilder_.Append("api/v1/sent-payload/topic/");
                    urlBuilder_.Append(System.Uri.EscapeDataString(ConvertToString(topicName, System.Globalization.CultureInfo.InvariantCulture)));

                    PrepareRequest(client_, request_, urlBuilder_);

                    var url_ = urlBuilder_.ToString();
                    request_.RequestUri = new System.Uri(url_, System.UriKind.RelativeOrAbsolute);

                    PrepareRequest(client_, request_, url_);

                    var response_ = await client_.SendAsync(request_, System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                    var disposeResponse_ = true;
                    try
                    {
                        var headers_ = new System.Collections.Generic.Dictionary<string, System.Collections.Generic.IEnumerable<string>>();
                        foreach (var item_ in response_.Headers)
                            headers_[item_.Key] = item_.Value;
                        if (response_.Content != null && response_.Content.Headers != null)
                        {
                            foreach (var item_ in response_.Content.Headers)
                                headers_[item_.Key] = item_.Value;
                        }

                        ProcessResponse(client_, response_);

                        var status_ = (int)response_.StatusCode;
                        if (status_ == 201)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<SentPayloadResponse>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            return objectResponse_.Object;
                        }
                        else
                        if (status_ == 404)
                        {
                            string responseText_ = ( response_.Content == null ) ? string.Empty : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new ApiException("Not found", status_, responseText_, headers_, null);
                        }
                        else
                        if (status_ == 422)
                        {
                            var objectResponse_ = await ReadObjectResponseAsync<HTTPValidationError>(response_, headers_, cancellationToken).ConfigureAwait(false);
                            if (objectResponse_.Object == null)
                            {
                                throw new ApiException("Response was null which was not expected.", status_, objectResponse_.Text, headers_, null);
                            }
                            throw new ApiException<HTTPValidationError>("Validation Error", status_, objectResponse_.Text, headers_, objectResponse_.Object, null);
                        }
                        else
                        {
                            var responseData_ = response_.Content == null ? null : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                            throw new ApiException("The HTTP status code of the response was not expected (" + status_ + ").", status_, responseData_, headers_, null);
                        }
                    }
                    finally
                    {
                        if (disposeResponse_)
                            response_.Dispose();
                    }
                }
            }
            finally
            {
                if (disposeClient_)
                    client_.Dispose();
            }
        }
    }

    /// <summary>
    /// HAND-WRITTEN topic-send request DTO — mirrors the generated
    /// <see cref="SentPayloadToUserRequest"/> (single Required.Always <c>payload</c>
    /// object), which is the shape the :10040 relay expects on every send route.
    /// Lives here for the same placeholder-spec reason as the method above.
    /// </summary>
    public partial class SentPayloadToTopicRequest
    {
        /// <summary>
        /// Payload sent from notification
        /// </summary>
        [Newtonsoft.Json.JsonProperty("payload", Required = Newtonsoft.Json.Required.Always)]
        [System.ComponentModel.DataAnnotations.Required]
        public object Payload { get; set; } = new object();

        private System.Collections.Generic.IDictionary<string, object>? _additionalProperties;

        [Newtonsoft.Json.JsonExtensionData]
        public System.Collections.Generic.IDictionary<string, object> AdditionalProperties
        {
            get { return _additionalProperties ?? (_additionalProperties = new System.Collections.Generic.Dictionary<string, object>()); }
            set { _additionalProperties = value; }
        }
    }
}
