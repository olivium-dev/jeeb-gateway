using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using JeebGateway.Financials;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace JeebGateway.IntegrationTests.Financials;

public class CommissionDebitVerificationTests
{
    private static readonly Guid Holder = Guid.Parse("55555555-5555-4555-8555-555555555555");
    private static readonly Guid Source = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");
    private static readonly Guid Destination = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");
    private static readonly Guid Transaction = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly CommissionDebitLookup Expected =
        new("delivery:req-1", "accept:req-1", Holder, 0.10m, "platform-fee", "USD");

    [Fact]
    public async Task Exact_executed_ten_cent_debit_to_system_wallet_is_verified_using_only_GETs()
    {
        var handler = new LedgerHandler();
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().Be(Transaction);
        handler.Methods.Should().HaveCount(4).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("pending")]
    [InlineData("aborted")]
    [InlineData("unknown-status")]
    [InlineData("missing-status")]
    [InlineData("null-status")]
    [InlineData("wrong-reference")]
    [InlineData("wrong-key")]
    [InlineData("wrong-service")]
    [InlineData("wrong-tag")]
    [InlineData("wrong-amount")]
    [InlineData("wrong-source")]
    [InlineData("wrong-destination")]
    [InlineData("wrong-header-binding")]
    [InlineData("not-fee-leg")]
    [InlineData("missing-legs")]
    [InlineData("extra-leg")]
    [InlineData("missing-id")]
    public async Task A_header_ID_is_not_proof_of_a_matching_executed_debit(string failure)
    {
        var handler = new LedgerHandler();
        var transaction = handler.Transactions[0]!.AsObject();
        var header = transaction["transactionHeader"]!.AsObject();
        var leg = transaction["transactionDetails"]![0]!.AsObject();
        switch (failure)
        {
            case "pending": header["status"] = -1; break;
            case "aborted": header["status"] = -2; break;
            case "unknown-status": header["status"] = 7; break;
            case "missing-status": header.Remove("status"); break;
            case "null-status": header["status"] = null; break;
            case "wrong-reference": header["externalReference"] = "delivery:another"; break;
            case "wrong-key": header["idempotencyKey"] = "accept:another"; break;
            case "wrong-service": header["serviceName"] = "another-service"; break;
            case "wrong-tag": header["tag"] = "top-up"; break;
            case "wrong-amount": leg["amount"] = 0.11m; break;
            case "wrong-source": leg["sourceWalletId"] = Guid.NewGuid(); break;
            case "wrong-destination": leg["destinationWalletId"] = Guid.NewGuid(); break;
            case "wrong-header-binding": leg["txHeaderId"] = Guid.NewGuid(); break;
            case "not-fee-leg": leg["isAdditionalFees"] = false; break;
            case "missing-legs": transaction.Remove("transactionDetails"); break;
            case "extra-leg": transaction["transactionDetails"]!.AsArray().Add(leg.DeepClone()); break;
            case "missing-id": header.Remove("txId"); break;
        }
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().BeNull();
        handler.Methods.Should().OnlyContain(method => method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("source-owner")]
    [InlineData("source-currency")]
    [InlineData("source-cod")]
    [InlineData("destination-owner")]
    [InlineData("destination-currency")]
    [InlineData("system-name")]
    [InlineData("system-type")]
    [InlineData("non-reserved-system-holder")]
    [InlineData("missing-system-holder-id")]
    [InlineData("missing-owner")]
    public async Task Accounting_wallet_ownership_and_currency_are_verified(string failure)
    {
        var handler = new LedgerHandler();
        var source = handler.HolderWallets["wallets"]![0]!.AsObject();
        var destination = handler.SystemWallets["wallets"]![0]!.AsObject();
        var system = handler.SystemWallets["walletHolder"]!.AsObject();
        switch (failure)
        {
            case "source-owner": source["holderId"] = Guid.NewGuid(); break;
            case "source-currency": source["currencyID"] = 2; break;
            case "source-cod": source["type"] = "cod_float"; break;
            case "destination-owner": destination["holderId"] = Guid.NewGuid(); break;
            case "destination-currency": destination["currencyID"] = 2; break;
            case "system-name": system["holderName"] = "other"; break;
            case "system-type": system["holderType"] = "other"; break;
            case "non-reserved-system-holder":
                var impostor = Guid.NewGuid();
                system["holderId"] = impostor;
                destination["holderId"] = impostor;
                break;
            case "missing-system-holder-id": system.Remove("holderId"); break;
            case "missing-owner": source.Remove("holderId"); break;
        }
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("missing-mapping")]
    [InlineData("null-mapping")]
    [InlineData("empty-mapping")]
    [InlineData("missing-id")]
    [InlineData("null-id")]
    [InlineData("missing-code")]
    [InlineData("null-code")]
    [InlineData("wrong-code")]
    [InlineData("duplicate-id")]
    [InlineData("duplicate-identical-row")]
    [InlineData("duplicate-code")]
    [InlineData("duplicate-code-casing")]
    public async Task Missing_ambiguous_or_mismatched_owner_currency_mapping_cannot_prove_collection(string failure)
    {
        var handler = new LedgerHandler();
        var currency = handler.Currencies![0]!.AsObject();
        switch (failure)
        {
            case "missing-mapping": handler.CurrencyStatus = HttpStatusCode.NotFound; break;
            case "null-mapping": handler.Currencies = null; break;
            case "empty-mapping": handler.Currencies.Clear(); break;
            case "missing-id": currency.Remove("id"); break;
            case "null-id": currency["id"] = null; break;
            case "missing-code": currency.Remove("code"); break;
            case "null-code": currency["code"] = null; break;
            case "wrong-code": currency["code"] = "Credit"; break;
            case "duplicate-id": handler.Currencies.Add(Currency(1, "Credit")); break;
            case "duplicate-identical-row": handler.Currencies.Add(Currency(1, "USD")); break;
            case "duplicate-code": handler.Currencies.Add(Currency(2, "USD")); break;
            case "duplicate-code-casing": handler.Currencies.Add(Currency(2, "usd")); break;
        }

        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().BeNull();
        handler.Methods.Should().HaveCount(2).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("EUR")]
    [InlineData("Credit")]
    [InlineData("usd")]
    public async Task Unsupported_settlement_currency_is_not_relabelled_from_wallet_configuration(string? currency)
    {
        var handler = new LedgerHandler();
        (await Client(handler).FindExecutedDebitAsync(Expected with { Currency = currency! }, default))
            .Should().BeNull();
        handler.Methods.Should().BeEmpty();
    }

    [Fact]
    public async Task Configured_USD_ID_two_is_verified_even_when_credit_uses_ID_one()
    {
        var handler = new LedgerHandler { Currencies = new JsonArray(Currency(1, "Credit"), Currency(2, "USD")) };
        handler.HolderWallets["wallets"]![0]!["currencyID"] = 2;
        handler.SystemWallets["wallets"]![0]!["currencyID"] = 2;

        (await Client(handler, currencyId: 2).FindExecutedDebitAsync(Expected, default)).Should().Be(Transaction);
        handler.Methods.Should().HaveCount(4).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Fact]
    public async Task Configured_credit_ID_one_cannot_verify_a_USD_settlement()
    {
        var handler = new LedgerHandler { Currencies = new JsonArray(Currency(1, "Credit"), Currency(2, "USD")) };

        (await Client(handler, currencyId: 1).FindExecutedDebitAsync(Expected, default)).Should().BeNull();
        handler.Methods.Should().HaveCount(2).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Fact]
    public async Task Currency_owner_read_failure_is_not_replaced_by_an_assumed_USD_ID()
    {
        var handler = new LedgerHandler { CurrencyStatus = HttpStatusCode.ServiceUnavailable };
        var read = () => Client(handler).FindExecutedDebitAsync(Expected, default);

        await read.Should().ThrowAsync<WalletCommissionDebitException>();
        handler.Methods.Should().HaveCount(2).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Fact]
    public async Task New_debit_selectors_use_verified_USD_ID_two_and_ignore_credit_ID_one()
    {
        var handler = new LedgerHandler { Currencies = new JsonArray(Currency(1, "Credit"), Currency(2, "USD")) };
        var sourceWallets = handler.HolderWallets["wallets"]!.AsArray();
        var destinationWallets = handler.SystemWallets["wallets"]!.AsArray();
        sourceWallets[0]!["currencyID"] = 2;
        destinationWallets[0]!["currencyID"] = 2;
        var creditSource = sourceWallets[0]!.DeepClone();
        creditSource["walletId"] = Guid.NewGuid();
        creditSource["currencyID"] = 1;
        sourceWallets.Insert(0, creditSource);
        var creditDestination = destinationWallets[0]!.DeepClone();
        creditDestination["walletId"] = Guid.NewGuid();
        creditDestination["currencyID"] = 1;
        destinationWallets.Insert(0, creditDestination);
        var client = Client(handler, currencyId: 2);

        (await client.ResolveFeeWalletAsync(Holder, default)).Should().Be(Source);
        (await client.ResolveSystemWalletAsync(default)).Should().Be(Destination);
        handler.Methods.Should().HaveCount(4).And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Theory]
    [InlineData("wrong-mapping", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("missing-mapping", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("unavailable-mapping", CommissionCollectionOutcome.Failed)]
    [InlineData("ambiguous-mapping", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("source-envelope", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("missing-source-envelope", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("source-owner", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("source-inactive", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("source-ambiguous", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("source-duplicate", CommissionCollectionOutcome.NoFeeWallet)]
    [InlineData("system-impostor", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("system-name", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("system-type", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("missing-system-envelope", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("destination-owner", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("destination-inactive", CommissionCollectionOutcome.NoSystemWallet)]
    [InlineData("destination-ambiguous", CommissionCollectionOutcome.NoSystemWallet)]
    public async Task Unsafe_new_debit_selection_never_initiates_a_transaction(
        string failure, CommissionCollectionOutcome expectedOutcome)
    {
        var handler = new LedgerHandler();
        var sourceWallets = handler.HolderWallets["wallets"]!.AsArray();
        var destinationWallets = handler.SystemWallets["wallets"]!.AsArray();
        switch (failure)
        {
            case "wrong-mapping":
                handler.Currencies = new JsonArray(Currency(1, "Credit"), Currency(2, "USD"));
                break;
            case "missing-mapping": handler.CurrencyStatus = HttpStatusCode.NotFound; break;
            case "unavailable-mapping": handler.CurrencyStatus = HttpStatusCode.ServiceUnavailable; break;
            case "ambiguous-mapping": handler.Currencies!.Add(Currency(2, "USD")); break;
            case "source-envelope": handler.HolderWallets["walletHolder"]!["holderId"] = Guid.NewGuid(); break;
            case "missing-source-envelope": handler.HolderWallets.Remove("walletHolder"); break;
            case "source-owner": sourceWallets[0]!["holderId"] = Guid.NewGuid(); break;
            case "source-inactive": sourceWallets[0]!["isActive"] = false; break;
            case "source-ambiguous":
                var otherSource = sourceWallets[0]!.DeepClone();
                otherSource["walletId"] = Guid.NewGuid();
                sourceWallets.Add(otherSource);
                break;
            case "source-duplicate": sourceWallets.Add(sourceWallets[0]!.DeepClone()); break;
            case "system-impostor":
                var impostor = Guid.NewGuid();
                handler.SystemWallets["walletHolder"]!["holderId"] = impostor;
                destinationWallets[0]!["holderId"] = impostor;
                break;
            case "system-name": handler.SystemWallets["walletHolder"]!["holderName"] = "other"; break;
            case "system-type": handler.SystemWallets["walletHolder"]!["holderType"] = "other"; break;
            case "missing-system-envelope": handler.SystemWallets.Remove("walletHolder"); break;
            case "destination-owner": destinationWallets[0]!["holderId"] = Guid.NewGuid(); break;
            case "destination-inactive": destinationWallets[0]!["isActive"] = false; break;
            case "destination-ambiguous":
                var otherDestination = destinationWallets[0]!.DeepClone();
                otherDestination["walletId"] = Guid.NewGuid();
                destinationWallets.Add(otherDestination);
                break;
        }
        var collector = new WalletCommissionCollector(Client(handler), new FakeSettlementServiceClient(),
            Options.Create(new CommissionCollectionOptions { Enabled = true, CurrencyId = 1 }),
            NullLogger<WalletCommissionCollector>.Instance);

        var result = await collector.CollectOnAcceptAsync(
            new CommissionCollectionCommand("req-1", Holder.ToString("D"), 1m), default);

        result.Outcome.Should().Be(expectedOutcome);
        result.TransactionId.Should().BeEmpty();
        handler.Methods.Should().NotBeEmpty().And.OnlyContain(method => method == HttpMethod.Get);
    }

    [Fact]
    public async Task Newer_aborted_header_does_not_hide_the_unique_executed_entry()
    {
        var handler = new LedgerHandler();
        var aborted = TransactionRow(Guid.NewGuid());
        aborted["transactionHeader"]!["status"] = -2;
        handler.Transactions.Insert(0, aborted);
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().Be(Transaction);
    }

    [Fact]
    public async Task Multiple_executed_matches_are_ambiguous_not_newest_wins()
    {
        var handler = new LedgerHandler();
        handler.Transactions.Add(TransactionRow(Guid.NewGuid()));
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().BeNull();
    }

    [Fact]
    public async Task Historical_execution_remains_verifiable_after_wallet_deactivation()
    {
        var handler = new LedgerHandler();
        handler.HolderWallets["wallets"]![0]!["isActive"] = false;
        handler.SystemWallets["wallets"]![0]!["isActive"] = false;
        (await Client(handler).FindExecutedDebitAsync(Expected, default)).Should().Be(Transaction);
    }

    private static WalletCommissionDebitClient Client(LedgerHandler handler, int currencyId = 1) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://wallet.invalid/") }, currencyId);

    private static JsonObject Currency(int id, string code) => new() { ["id"] = id, ["code"] = code };

    private static JsonObject TransactionRow(Guid id) => new()
    {
        ["transactionHeader"] = new JsonObject
        {
            ["txId"] = id, ["status"] = 0, ["serviceName"] = "jeeb-gateway",
            ["tag"] = Expected.Tag, ["externalReference"] = Expected.ExternalReference,
            ["idempotencyKey"] = Expected.IdempotencyKey,
        },
        ["transactionDetails"] = new JsonArray(new JsonObject
        {
            ["txHeaderId"] = id, ["sourceWalletId"] = Source, ["destinationWalletId"] = Destination,
            ["amount"] = 0.10m, ["isAdditionalFees"] = true,
        }),
    };

    private static JsonObject Wallets(Guid holder, Guid wallet, string type) => new()
    {
        ["walletHolder"] = new JsonObject
        {
            ["holderId"] = holder, ["holderName"] = type, ["holderType"] = type,
        },
        ["wallets"] = new JsonArray(new JsonObject
        {
            ["walletId"] = wallet, ["holderId"] = holder, ["currencyID"] = 1,
            ["type"] = type == "__SYSTEM__" ? "system" : "jeeb", ["isActive"] = true,
        }),
    };

    private sealed class LedgerHandler : HttpMessageHandler
    {
        public JsonArray Transactions { get; } = new(TransactionRow(Transaction));
        public JsonObject HolderWallets { get; } = Wallets(Holder, Source, "jeeber");
        public JsonObject SystemWallets { get; } = Wallets(Guid.Empty, Destination, "__SYSTEM__");
        public JsonArray? Currencies { get; set; } = new(Currency(1, "USD"));
        public HttpStatusCode CurrencyStatus { get; set; } = HttpStatusCode.OK;
        public List<HttpMethod> Methods { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Methods.Add(request.Method);
            request.Method.Should().Be(HttpMethod.Get, "verification must never move money");
            var path = request.RequestUri!.AbsolutePath;
            JsonNode? body = path.StartsWith("/Transaction/by-external-reference/", StringComparison.Ordinal)
                ? Transactions
                : path == "/Fees/currencies" ? Currencies
                : path == "/system-wallet" ? SystemWallets
                : path == $"/Wallet/holder/{Holder:D}/wallets" ? HolderWallets
                : throw new InvalidOperationException("Unexpected wallet path " + path);
            return Task.FromResult(new HttpResponseMessage(path == "/Fees/currencies" ? CurrencyStatus : HttpStatusCode.OK)
            {
                Content = JsonContent.Create(body),
            });
        }
    }
}
