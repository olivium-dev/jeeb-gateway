using System.Net;
using System.Text;
using FluentAssertions;
using JeebGateway.JeebWallet;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JeebGateway.IntegrationTests;

public sealed class WalletServiceLedgerReaderTests
{
    [Fact]
    public async Task List_UsesGenericHolderLedger_AndPreservesDecimalAndUtc()
    {
        Uri? requested = null;
        var detailId = Guid.NewGuid();
        var handler = new DelegateHandler(request =>
        {
            requested = request.RequestUri;
            return Json(HttpStatusCode.OK, $$"""
                {
                  "items": [{
                    "id": "{{detailId:D}}",
                    "transactionId": "{{Guid.NewGuid():D}}",
                    "type": "partner-topup",
                    "amount": 9007199254740993.25,
                    "sign": 1,
                    "reference": "receipt-1",
                    "serviceName": "partner",
                    "status": 0,
                    "isAdditionalFees": false,
                    "createdAt": "2026-08-10T11:12:13.0000000Z"
                  }],
                  "nextCursor": null
                }
                """);
        });
        var sut = NewReader(handler);

        var result = await sut.ReadLedgerAsync(
            Guid.Parse("11111111-1111-1111-1111-111111111111"),
            2,
            50,
            "partner-topup",
            new DateOnly(2026, 8, 1),
            new DateOnly(2026, 8, 10),
            CancellationToken.None);

        result.Should().ContainSingle();
        result[0].Amount.Should().Be(9007199254740993.25m);
        result[0].Id.Should().Be(detailId.ToString("D"));
        result[0].Ts.Should().Be("2026-08-10T11:12:13.0000000Z");
        var decoded = Uri.UnescapeDataString(requested!.Query);
        decoded.Should().Contain("page=2").And.Contain("pageSize=50");
        decoded.Should().Contain("type=partner-topup");
        decoded.Should().Contain("from=2026-08-01T00:00:00.0000000+00:00");
        decoded.Should().Contain("to=2026-08-11T00:00:00.0000000+00:00");
    }

    [Fact]
    public async Task UpstreamFailure_IsUnavailable_NotAnEmptyLedger()
    {
        var sut = NewReader(new DelegateHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));

        var act = () => sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        await act.Should().ThrowAsync<WalletLedgerUnavailableException>();
    }

    [Fact]
    public async Task Detail_MapsWalletEntry_AndPreservesNotFound()
    {
        var detailId = Guid.NewGuid();
        var call = 0;
        var sut = NewReader(new DelegateHandler(_ =>
        {
            call++;
            return call == 1
                ? Json(HttpStatusCode.OK, $$"""
                    {
                      "id": "{{detailId:D}}",
                      "type": "cod-settlement",
                      "amount": 12.34,
                      "sign": 1,
                      "reference": "delivery-1",
                      "createdAt": "2026-08-10T10:00:00Z"
                    }
                    """)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        }));

        var found = await sut.ReadEntryAsync(
            Guid.NewGuid(), detailId.ToString("D"), CancellationToken.None);
        var missing = await sut.ReadEntryAsync(
            Guid.NewGuid(), detailId.ToString("D"), CancellationToken.None);

        found.Should().NotBeNull();
        found!.Ref.Should().Be("delivery-1");
        missing.Should().BeNull();
    }

    /// <summary>
    /// The Authority flip must be invisible on the wire: both readers project the same instant
    /// into the SAME string, or every mobile client sees the format change the day it flips.
    /// </summary>
    [Fact]
    public async Task BothAuthorities_ProjectTheSameInstant_IntoTheSameWireFormat()
    {
        var sut = NewReader(new DelegateHandler(_ => Json(HttpStatusCode.OK, $$"""
            {
              "items": [{
                "id": "{{Guid.NewGuid():D}}", "type": "topup", "amount": 1,
                "sign": 1, "reference": "r", "createdAt": "2026-08-08T21:02:43.9544940Z"
              }],
              "nextCursor": null
            }
            """)));

        var viaWalletApi = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        var viaPostgres = PostgresJeebWalletLedgerReader.FormatUtcTimestamp(
            new DateTime(2026, 8, 8, 21, 2, 43, DateTimeKind.Unspecified).AddTicks(9_544_940));
        viaWalletApi[0].Ts.Should().Be(viaPostgres);
    }

    /// <summary>
    /// The shadow digest is the only evidence the flip is judged on, so it must hash the served
    /// bytes: an offset-form vs Z-form drift on the same instant has to read as a MISMATCH.
    /// </summary>
    [Fact]
    public void ShadowDigest_SeesAWireFormatDrift_OnTheSameInstant()
    {
        var zForm = new JeebWalletLedgerEntry
        {
            Id = "a", Type = "topup", Amount = 1m, Sign = 1, Ref = "r",
            Ts = "2026-08-08T21:02:43.9544940Z",
        };
        var offsetForm = new JeebWalletLedgerEntry
        {
            Id = "a", Type = "topup", Amount = 1m, Sign = 1, Ref = "r",
            Ts = "2026-08-08T21:02:43.9544940+00:00",
        };

        ShadowComparingJeebWalletLedgerReader.Digest(new[] { zForm })
            .Should().NotBe(ShadowComparingJeebWalletLedgerReader.Digest(new[] { offsetForm }));
    }

    [Fact]
    public async Task ShadowFailure_NeverReplacesPrimaryResponse()
    {
        var primary = NewReader(new DelegateHandler(_ => Json(HttpStatusCode.OK, """
            { "items": [], "nextCursor": null }
            """)));
        var sut = new ShadowComparingJeebWalletLedgerReader(
            primary,
            new ThrowingShadow(),
            NullLogger<ShadowComparingJeebWalletLedgerReader>.Instance);

        var result = await sut.ReadLedgerAsync(
            Guid.NewGuid(), 1, 20, null, null, null, CancellationToken.None);

        result.Should().BeEmpty();
    }

    private static WalletServiceJeebWalletLedgerReader NewReader(HttpMessageHandler handler) =>
        new(
            new StubHttpClientFactory(new HttpClient(handler)
            {
                BaseAddress = new Uri("http://wallet-service/"),
            }),
            NullLogger<WalletServiceJeebWalletLedgerReader>.Instance);

    private static HttpResponseMessage Json(HttpStatusCode status, string body) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> send)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(send(request));
    }

    private sealed class ThrowingShadow : IJeebWalletLedgerReader
    {
        public Task<IReadOnlyList<JeebWalletLedgerEntry>> ReadLedgerAsync(
            Guid holderId, int page, int pageSize, string? type, DateOnly? from, DateOnly? to,
            CancellationToken ct) =>
            throw new IOException("shadow unavailable");

        public Task<JeebWalletLedgerEntry?> ReadEntryAsync(
            Guid holderId, string detailId, CancellationToken ct) =>
            throw new IOException("shadow unavailable");
    }
}
