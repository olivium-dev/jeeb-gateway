using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using JeebGateway.Services.Clients;
using JeebGateway.StateService.Idempotency;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// JEBV4-344 regression pack — <see cref="StateServiceIdempotencyStore"/> must take the
/// <c>inserted</c> flag from the PUT's OWN response, never from a read-back.
///
/// <para>WHY THESE TESTS AND NOT THE EXISTING SUITE: every other idempotency test in this
/// project resolves <see cref="InMemoryIdempotencyStore"/> (or a hand-written fake) through
/// <c>WebApplicationFactory</c>, where <c>Inserted</c> works correctly. The bug lived
/// exclusively in the state-service-backed store's HTTP conversation, so it was invisible to
/// a green integration suite. These tests therefore drive the REAL store over a stub handler
/// that reproduces the live wire behaviour observed against jeeb-state-service on MSI:</para>
/// <code>
/// PUT  /state/idempotency       -> 201 {"inserted": true,  ...}   (first)
/// PUT  /state/idempotency       -> 200 {"inserted": false, ...}   (replay, ORIGINAL body)
/// GET  /state/idempotency/{key} -> 200 {"inserted": false, ...}   (ALWAYS false)
/// </code>
/// </summary>
public class StateServiceIdempotencyInsertedTests
{
    private const string BaseUrl = "http://jeeb-state-service.test/";

    private static StateServiceIdempotencyStore StoreOver(HttpMessageHandler handler) =>
        new(new JeebStateServiceClient(BaseUrl, new HttpClient(handler)),
            NullLogger<StateServiceIdempotencyStore>.Instance);

    // -----------------------------------------------------------------------
    // POSITIVE CONTROL — a FIRST reservation must report Inserted=true.
    // This is the assertion that fails on the pre-fix code.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task First_PutOrGet_Reports_Inserted_True()
    {
        var store = StoreOver(new FakeStateServiceHandler());

        var first = await store.PutOrGetAsync(
            "svc-callback:fresh-key", 202, """{"entryId":"e-1","status":"Queued"}""", 600, default);

        first.Inserted.Should().BeTrue(
            "the PUT answered 201 {\"inserted\":true} — that is the only authoritative source. "
            + "Reading it back with GET always says false, which pinned every reserve-before-act "
            + "caller to its already-exists branch (JEBV4-344).");
        first.StatusCode.Should().Be(202);
    }

    // -----------------------------------------------------------------------
    // NEGATIVE TEST — a REPLAYED key must report Inserted=false and hand back
    // the ORIGINAL stored status + body, not the one the replay tried to write.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Replayed_PutOrGet_Reports_Inserted_False_And_Original_Body()
    {
        var store = StoreOver(new FakeStateServiceHandler());
        const string key = "svc-callback:replayed-key";

        var first = await store.PutOrGetAsync(
            key, 202, """{"entryId":"first","status":"Queued"}""", 600, default);
        var replay = await store.PutOrGetAsync(
            key, 202, """{"entryId":"second","status":"Queued"}""", 600, default);

        first.Inserted.Should().BeTrue();
        replay.Inserted.Should().BeFalse("the second writer lost the insert-once race");
        replay.ResponseBodyJson.Should().Contain("first")
            .And.NotContain("second", "a replay must observe the ORIGINAL stored effect");
    }

    [Fact]
    public async Task Only_One_Of_Many_Concurrent_Writers_Sees_Inserted_True()
    {
        var handler = new FakeStateServiceHandler();
        var store = StoreOver(handler);

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i =>
            store.PutOrGetAsync("svc-callback:race", 202, $$"""{"n":{{i}}}""", 600, default)));

        results.Count(r => r.Inserted).Should().Be(1, "insert-once means exactly one winner");
    }

    // -----------------------------------------------------------------------
    // DISCRIMINATING TEST — proves the flag comes from the PUT, not the GET.
    // The stub lies in the read-back (GET says inserted:true) while the PUT
    // tells the truth (inserted:false). Only a store that reads the PUT passes.
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Inserted_Comes_From_The_Put_Not_The_Read_Back()
    {
        var handler = new FakeStateServiceHandler { GetLiesInsertedTrue = true };
        var store = StoreOver(handler);
        const string key = "svc-callback:put-is-authoritative";

        await store.PutOrGetAsync(key, 202, """{"entryId":"first"}""", 600, default);
        var replay = await store.PutOrGetAsync(key, 202, """{"entryId":"second"}""", 600, default);

        replay.Inserted.Should().BeFalse(
            "the read-back is not the authority — the PUT is. A store that consults GET would "
            + "read this stub's lying inserted:true and report a phantom insert.");
        handler.GetCount.Should().Be(0,
            "the PUT response already carries the authoritative record, so the normal path "
            + "must not issue a read-back at all (it was the read-back that caused JEBV4-344)");
    }

    // -----------------------------------------------------------------------
    // DEGRADATION — a bodyless 2xx must fall back to the documented status
    // semantics (201=created, 200=replay) plus a read-back for the body, and
    // must NEVER inherit the read-back's always-false flag on a 201.
    // -----------------------------------------------------------------------

    [Theory]
    [InlineData(HttpStatusCode.Created, true)]
    [InlineData(HttpStatusCode.OK, false)]
    public async Task Bodyless_Put_Falls_Back_To_Status_Semantics(HttpStatusCode status, bool expectInserted)
    {
        var store = StoreOver(new BodylessPutHandler(status));

        var outcome = await store.PutOrGetAsync("k", 202, """{"a":1}""", 600, default);

        outcome.Inserted.Should().Be(expectInserted,
            "with no body, the operation summary in jeeb-state-service.openapi.json is the "
            + "contract: 201=created, 200=replay returns original");
    }

    [Fact]
    public async Task Bodyless_204_Is_Unknown_And_Fails_Closed_To_Not_Inserted()
    {
        var store = StoreOver(new BodylessPutHandler(HttpStatusCode.NoContent));

        var outcome = await store.PutOrGetAsync("k", 202, """{"a":1}""", 600, default);

        outcome.Inserted.Should().BeFalse(
            "204 carries no insert information; unknown must fail towards not-inserted so a "
            + "reserve-before-act caller does not dispatch twice");
    }

    [Fact]
    public async Task Genuine_5xx_Still_Throws()
    {
        var store = StoreOver(new StatusOnlyHandler(HttpStatusCode.InternalServerError));

        var act = async () => await store.PutOrGetAsync("k", 202, "{}", 600, default);

        await act.Should().ThrowAsync<JeebStateServiceApiException>(
            "reading the PUT body must not swallow a real upstream failure — "
            + "ServiceCallbacksController fails CLOSED on this exception");
    }

    // ── stubs ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Reproduces jeeb-state-service's observed insert-once conversation, including the
    /// detail that matters: the PUT's response body is the AUTHORITATIVE record and the GET
    /// read-back always reports <c>inserted:false</c>.
    /// </summary>
    private sealed class FakeStateServiceHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, (int StatusCode, JsonElement Body)> _rows = new();
        private readonly object _gate = new();

        /// <summary>When true the GET read-back reports inserted:true (a lie, to prove the
        /// store does not consult it).</summary>
        public bool GetLiesInsertedTrue { get; init; }

        public int GetCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
            {
                var raw = await request.Content!.ReadAsStringAsync(cancellationToken);
                var put = JsonSerializer.Deserialize<JsonElement>(raw);
                var key = put.GetProperty("key").GetString()!;
                var statusCode = put.GetProperty("statusCode").GetInt32();
                var body = put.GetProperty("responseBody");

                bool inserted;
                (int StatusCode, JsonElement Body) row;
                lock (_gate)
                {
                    inserted = !_rows.ContainsKey(key);
                    if (inserted) _rows[key] = (statusCode, body.Clone());
                    row = _rows[key];
                }

                return Json(
                    inserted ? HttpStatusCode.Created : HttpStatusCode.OK,
                    Record(key, row, inserted));
            }

            if (request.Method == HttpMethod.Get)
            {
                lock (_gate) { GetCount++; }
                var key = Uri.UnescapeDataString(request.RequestUri!.Segments[^1]);
                (int StatusCode, JsonElement Body) row;
                lock (_gate)
                {
                    if (!_rows.TryGetValue(key, out row))
                        return new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request };
                }

                // The real service NEVER returns inserted:true on a read; GetLiesInsertedTrue
                // exists only so a store that wrongly consults the read-back is caught.
                return Json(HttpStatusCode.OK, Record(key, row, GetLiesInsertedTrue));
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed) { RequestMessage = request };
        }

        private static string Record(string key, (int StatusCode, JsonElement Body) row, bool inserted) =>
            $$"""
            {"key":{{JsonSerializer.Serialize(key)}},"responseBody":{{row.Body.GetRawText()}},
             "statusCode":{{row.StatusCode}},"createdAt":"2026-07-27T00:00:00+00:00",
             "expiresAt":"2026-07-27T01:00:00+00:00","inserted":{{(inserted ? "true" : "false")}}}
            """;

        private static HttpResponseMessage Json(HttpStatusCode status, string json) =>
            new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    }

    /// <summary>PUT answers 2xx with NO body; GET serves a stored row (inserted:false).</summary>
    private sealed class BodylessPutHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _putStatus;

        public BodylessPutHandler(HttpStatusCode putStatus) => _putStatus = putStatus;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Put)
                return Task.FromResult(new HttpResponseMessage(_putStatus) { RequestMessage = request });

            const string row = """
                {"key":"k","responseBody":{"a":1},"statusCode":202,
                 "createdAt":"2026-07-27T00:00:00+00:00","expiresAt":"2026-07-27T01:00:00+00:00",
                 "inserted":false}
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(row, Encoding.UTF8, "application/json"),
            });
        }
    }

    private sealed class StatusOnlyHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;

        public StatusOnlyHandler(HttpStatusCode status) => _status = status;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(_status) { RequestMessage = request });
    }
}
