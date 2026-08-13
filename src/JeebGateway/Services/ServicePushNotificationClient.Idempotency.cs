// ---------------------------------------------------------------------------
// HAND-WRITTEN partial extension of the NSwag-generated
// ServicePushNotificationClient (Services/ServicePushNotificationClient.cs).
//
// W3-14 / §4.1 rider: a caller-supplied Idempotency-Key must reach
// push-notification UNCHANGED, because that key is what its PushDispatch
// durability registry stores and what api/v1/sent-payload/idempotency/{key}
// (IPushDispatchRecoveryClient) later reads back. The generated send methods
// take no header argument and generated files are never edited, so the key is
// carried through the PrepareRequest(…, string url) seam the generator leaves
// open — the same seam ServicePushNotificationClient.ApiKey.cs uses for
// X-Api-Key, on its other overload.
// ---------------------------------------------------------------------------

namespace JeebGateway.service.ServicePushNotification
{
    public partial class ServicePushNotificationClient
    {
        public const string IdempotencyHeader = "Idempotency-Key";

        // AsyncLocal, NOT an instance property: this client is a SCOPED singleton reused
        // across sends, so a settable key would leak onto the next, unrelated dispatch.
        private static readonly System.Threading.AsyncLocal<string?> AmbientIdempotencyKey = new();

        /// <summary>The key that would ride the next send on this async flow; null outside a scope.</summary>
        public static string? CurrentIdempotencyKey => AmbientIdempotencyKey.Value;

        /// <summary>
        /// Stamps <paramref name="key"/> on every request this client issues inside the
        /// <c>using</c> block. A null/blank key adds no header, which is today's behaviour.
        /// </summary>
        public static System.IDisposable UseIdempotencyKey(string? key) =>
            new IdempotencyKeyScope(key);

        partial void PrepareRequest(
            System.Net.Http.HttpClient client,
            System.Net.Http.HttpRequestMessage request,
            string url)
        {
            var key = AmbientIdempotencyKey.Value;
            if (!string.IsNullOrWhiteSpace(key) && !request.Headers.Contains(IdempotencyHeader))
            {
                // Verbatim: push-notification keys its dispatch registry on this exact string.
                request.Headers.TryAddWithoutValidation(IdempotencyHeader, key);
            }
        }

        private sealed class IdempotencyKeyScope : System.IDisposable
        {
            private readonly string? _previous;

            public IdempotencyKeyScope(string? key)
            {
                _previous = AmbientIdempotencyKey.Value;
                AmbientIdempotencyKey.Value = key;
            }

            public void Dispose() => AmbientIdempotencyKey.Value = _previous;
        }
    }
}
