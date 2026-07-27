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

        /// <summary>
        /// JEBV4-344 — response-reading form of <c>PUT /v1/state/idempotency</c>.
        ///
        /// <para>The generated <see cref="JeebStateServiceClient.UpsertIdempotencyKeyAsync(IdempotencyPutRequest, System.Threading.CancellationToken)"/>
        /// is typed <c>void</c> because the OpenAPI document declares 200/201/204 with NO
        /// response body. The running service does return one, and it is the ONLY place the
        /// authoritative <c>inserted</c> flag exists — a subsequent
        /// <c>GET /v1/state/idempotency/{key}</c> always reports <c>inserted:false</c>, because
        /// a read is never an insert. Discarding the PUT body therefore pins every
        /// reserve-before-act caller to its "already exists" branch forever.</para>
        ///
        /// <para>Returns the PUT's own status code plus the parsed record when the service sent
        /// one. Callers must NOT assume "first call wins" — see
        /// <see cref="IdempotencyUpsertResult.ResolveInserted"/> for the precedence rules.</para>
        /// </summary>
        System.Threading.Tasks.Task<IdempotencyUpsertResult>
            UpsertIdempotencyKeyWithResultAsync(
                IdempotencyPutRequest body, System.Threading.CancellationToken cancellationToken);
    }

    /// <summary>
    /// Outcome of a response-reading <c>PUT /v1/state/idempotency</c> (JEBV4-344).
    /// </summary>
    public sealed class IdempotencyUpsertResult
    {
        /// <summary>The PUT's own HTTP status: 201 = created, 200 = replay, 204 = no content.</summary>
        public required int HttpStatusCode { get; init; }

        /// <summary>
        /// The record the service returned in the PUT response, or <c>null</c> when the
        /// response carried no parseable body (older builds, or a genuine 204).
        /// </summary>
        public required IdempotencyRecord? Record { get; init; }

        /// <summary>
        /// The authoritative insert flag, or <c>null</c> when it cannot be established.
        ///
        /// <para>Precedence: the body's own <c>inserted</c> field first (the service's own
        /// answer); failing that, the documented status semantics — <c>201 = created</c>,
        /// <c>200 = replay returns original</c>, per the operation summary in
        /// <c>jeeb-state-service.openapi.json</c>. A bodyless 204 is genuinely ambiguous and
        /// yields <c>null</c> so the caller can decide how to degrade; it is never guessed.</para>
        /// </summary>
        public bool? ResolveInserted() => Record?.Inserted ?? HttpStatusCode switch
        {
            201 => true,
            200 => false,
            _ => (bool?)null,
        };
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
                if (status_ >= 200 && status_ < 300)
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

        /// <inheritdoc cref="IJeebStateServiceClient.UpsertIdempotencyKeyWithResultAsync"/>
        public virtual async System.Threading.Tasks.Task<IdempotencyUpsertResult>
            UpsertIdempotencyKeyWithResultAsync(
                IdempotencyPutRequest body, System.Threading.CancellationToken cancellationToken)
        {
            if (body == null)
                throw new System.ArgumentNullException(nameof(body));

            var client_ = _httpClient;
            using var request_ = new System.Net.Http.HttpRequestMessage();
            var json_ = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(body, JsonSerializerSettings);
            var content_ = new System.Net.Http.ByteArrayContent(json_);
            content_.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse("application/json");
            request_.Content = content_;
            request_.Method = new System.Net.Http.HttpMethod("PUT");
            request_.Headers.Accept.Add(
                System.Net.Http.Headers.MediaTypeWithQualityHeaderValue.Parse("application/json"));

            var urlBuilder_ = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(_baseUrl)) urlBuilder_.Append(_baseUrl);
            urlBuilder_.Append("v1/state/idempotency");

            PrepareRequest(client_, request_, urlBuilder_);
            var url_ = urlBuilder_.ToString();
            request_.RequestUri = new System.Uri(url_, System.UriKind.RelativeOrAbsolute);
            PrepareRequest(client_, request_, url_);

            var response_ = await client_
                .SendAsync(request_, System.Net.Http.HttpCompletionOption.ResponseContentRead, cancellationToken)
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

                // D1b pin (Systemic2xxUpstreamClientTests): every 2xx is a success here,
                // exactly as the void-typed generated overload treats 200/201/204.
                if (status_ < 200 || status_ >= 300)
                {
                    var errorBody_ = response_.Content == null
                        ? string.Empty
                        : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new JeebStateServiceApiException(
                        "The HTTP status code of the response was not expected (" + status_ + ").",
                        status_, errorBody_, headers_, null);
                }

                var text_ = response_.Content == null
                    ? string.Empty
                    : await response_.Content.ReadAsStringAsync().ConfigureAwait(false);

                IdempotencyRecord? record_ = null;
                if (!string.IsNullOrWhiteSpace(text_))
                {
                    try
                    {
                        record_ = System.Text.Json.JsonSerializer
                            .Deserialize<IdempotencyRecord>(text_, JsonSerializerSettings);
                    }
                    catch (System.Text.Json.JsonException)
                    {
                        // A 2xx with an unparseable body is a contract surprise, not a failure of
                        // the write. Leave Record null; the caller degrades via the status code
                        // and (where it needs the stored body) a read-back.
                        record_ = null;
                    }
                }

                return new IdempotencyUpsertResult { HttpStatusCode = status_, Record = record_ };
            }
            finally
            {
                response_.Dispose();
            }
        }
    }
}
