using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using JeebGateway.Financials;
using JeebGateway.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace JeebGateway.IntegrationTests.Gw1Pack;

/// <summary>
/// GW1 TEST PACK — member item <b>W1.8 + W3.5(b)</b>: the cash-settlement ledger is
/// durably backed and is a <c>Critical</c> store under <see cref="StoreDurabilityGuard"/>
/// (owner ruling 2026-07-31 "PROMOTE").
///
/// <para>Run this item alone:
/// <c>dotnet test --no-build --filter "FullyQualifiedName~Gw1Pack.W18_"</c>.</para>
///
/// <para><b>READ THIS BEFORE SCORING THE ITEM ON A GREEN HERE.</b> The claim that
/// matters — <i>a ledger entry survives a process restart</i> — is <b>NOT PROVEN by
/// anything in this file, and cannot be</b>. It is a <c>service</c>-class claim about
/// a real database: create a settlement, <c>msi.sh --sudo systemctl restart
/// jeeb-gateway</c>, read the same ledger entry id back. Testcontainers is the only
/// host-side way to execute the SQL and it needs Docker, which is banned in this
/// environment. No placeholder stands in for that leg here: an
/// <c>Assert.True(true, "verified elsewhere")</c> is a green that means nothing, and
/// this programme has already shipped a 473-test green that was worth exactly zero.
/// <c>run-pack.sh</c> prints the missing leg as NOT-PROVEN and never as PASS.</para>
///
/// <para><b>What IS proven here, and why each leg is not circular.</b></para>
/// <list type="bullet">
///   <item><b>B1/B2</b> (in <c>tests/gw1-pack/lib/critical-parse.py</c>, not in this
///   file) — the <c>Critical</c> set measured by parsing the C# at two git refs, so the
///   base→head delta is computed rather than asserted. An in-suite
///   <c>HaveCount(26)</c> is a tripwire after the owner-service and GDPR state-work cutovers.</item>
///   <item><b>B3/B4</b> — a REAL host boot in <c>Production</c> with the gate ARMED.
///   B3 is the red (no <c>GatewayPostgres</c> ⇒ the violation list names the ledger);
///   B4 is its discriminating control (with <c>GatewayPostgres</c> set the ledger
///   DISAPPEARS from the same list). B4 is what separates "the promotion works" from
///   "the guard refuses everything" — a refuse-everything guard passes B3 alone.</item>
///   <item><b>B5</b> — the hazard the promotion closes, demonstrated rather than
///   asserted: a second <see cref="InMemorySettlementLedgerClient"/> instance (what a
///   restart produces) mints a SECOND ledger entry id for the SAME idempotency key.</item>
///   <item><b>B6</b> — the migration and the client agree column-by-column. The
///   failure mode is silent: <c>SettlementService</c> swallows the ledger post, so a
///   column typo yields no error, a permanently NULL <c>ledger_entry_id</c>, and a
///   60 s reconciler retrying forever.</item>
/// </list>
/// </summary>
public class W18_SettlementLedgerDurableTests
{
    private const string FakeCs =
        "Host=127.0.0.1;Port=1;Database=jeeb_test;Username=jeeb;Password=jeeb;Timeout=1";

    // A real (>=32 byte) key so the Production boot gets past JwtSigningKeyGuard and
    // actually reaches StoreDurabilityGuard — otherwise B3's throw would be the JWT
    // guard's and would prove nothing about the ledger.
    private const string ProdJwtKey = "gw1-pack-production-boot-signing-key-32+";

    // ── B3 / B4 — a REAL Production host boot, gate ARMED ──────────────────

    private static Exception? BootProduction(string? gatewayPostgresCs)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Jwt:SigningKey", ProdJwtKey);
            b.UseSetting("BffServices:RequiredInProduction", "false");
            // StoreDurability:FailClosedDisabled is deliberately NOT set: the gate must
            // be ARMED. Setting it here would disarm the very thing under test.
            if (gatewayPostgresCs is not null)
                b.UseSetting("GatewayPostgres:ConnectionString", gatewayPostgresCs);
        });

        try
        {
            _ = factory.Services.GetService<ISettlementLedgerClient>(); // forces the host build
            return null;
        }
        catch (Exception ex)
        {
            return ex;
        }
        finally
        {
            factory.Dispose();
        }
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (Exception? e = ex; e is not null; e = e.InnerException) parts.Add(e.Message);
        if (ex is AggregateException agg)
            parts.AddRange(agg.Flatten().InnerExceptions.Select(e => e.Message));
        return string.Join("\n", parts);
    }

    [Fact]
    public void B3_Production_Boot_Without_GatewayPostgres_Does_Not_Downgrade_The_Settlement_Ledger()
    {
        var ex = BootProduction(gatewayPostgresCs: null);

        ex.Should().NotBeNull("other gateway-owned stores still correctly require durable selectors");
        var message = Flatten(ex!);
        message.Should().Contain("FAIL-CLOSED");
        message.Should().NotContain("ISettlementLedgerClient",
            "wallet-service authority is independent of the legacy gateway database selector");
    }

    [Fact]
    public void B4_Production_Boot_With_GatewayPostgres_No_Longer_Names_The_Settlement_Ledger()
    {
        // THE DISCRIMINATING CONTROL. B3 alone cannot distinguish "the ledger promotion
        // is live" from "this guard refuses every prod-like boot for unrelated reasons".
        // Flipping ONLY the GatewayPostgres selector must remove ISettlementLedgerClient
        // from the violation list. Other critical stores (state-service, Redis, upstream
        // flags) are still unprovisioned in a host test, so the boot legitimately still
        // fails — the assertion is about WHICH store is named, not about booting.
        var ex = BootProduction(gatewayPostgresCs: FakeCs);

        // No silent-pass path. `if (ex is null) return;` would let this test go green
        // without ever evaluating the guard, which is the shape of green this programme
        // keeps mistaking for evidence. A host test cannot provision the state-service /
        // Redis / upstream-flag stores, so the boot MUST still fail — the claim is about
        // WHICH store is named, not about booting successfully.
        ex.Should().NotBeNull(
            "the Production boot must still reach StoreDurabilityGuard and refuse for the OTHER " +
            "unprovisioned critical stores; if it now boots clean, this control has stopped " +
            "discriminating and must be re-authored rather than left as a green");

        var message = Flatten(ex!);
        message.Should().NotContain("ISettlementLedgerClient",
            "the settlement ledger always resolves to wallet-service, independent of this selector");

        // POSITIVE CONTROL that the boot really did evaluate the guard (and this is not
        // a NotContain passing because the message is about something else entirely).
        message.Should().Contain("FAIL-CLOSED",
            "the run must still have reached StoreDurabilityGuard for the NotContain above to mean anything");
    }

    [Fact]
    public void B4b_DI_Selects_Wallet_With_Or_Without_A_Gateway_Connection_String()
    {
        using (var withCs = new WebApplicationFactory<Program>()
                   .WithWebHostBuilder(b => b.UseSetting("GatewayPostgres:ConnectionString", FakeCs)))
        {
            withCs.Services.GetRequiredService<ISettlementLedgerClient>()
                .Should().BeOfType<WalletSettlementLedgerClient>();
        }

        // POSITIVE CONTROL for the line above: a resolution that ALWAYS returned the
        // Postgres type would satisfy it without the selector working at all.
        using var withoutCs = new WebApplicationFactory<Program>();
        withoutCs.Services.GetRequiredService<ISettlementLedgerClient>()
            .Should().BeOfType<WalletSettlementLedgerClient>(
                "there is no local financial writer fallback");
    }

    // ── B5 — the hazard the promotion closes, demonstrated ─────────────────

    [Fact]
    public async Task B5_A_Restart_Of_The_InMemory_Ledger_Mints_A_Second_Id_For_One_Cash_Collection()
    {
        static LedgerEntryRequest Post(string key) => new()
        {
            DeliveryId = "delivery-1", JeeberId = "jeeber-1", ClientId = "client-1",
            EntryType = "cod_settlement", GoodsCost = 100m, Commission = 10m,
            Insurance = 0m, Total = 110m, Currency = "USD", PaymentMethod = "cash",
            IdempotencyKey = key,
        };

        var beforeRestart = new InMemorySettlementLedgerClient(TimeProvider.System);
        var first = await beforeRestart.PostLedgerEntryAsync(Post("settlement-42"), CancellationToken.None);
        var replayInSameProcess = await beforeRestart.PostLedgerEntryAsync(Post("settlement-42"), CancellationToken.None);

        // Within one process the ConcurrentDictionary memo works — this is the POSITIVE
        // CONTROL, and it is exactly why the old rationale looked sound.
        replayInSameProcess.LedgerEntryId.Should().Be(first.LedgerEntryId);

        // A `systemctl restart` is a new instance and an empty dictionary. The 60 s
        // SettlementLedgerReconciler then replays every settlement row whose
        // ledger_entry_id is NULL under the SAME key.
        var afterRestart = new InMemorySettlementLedgerClient(TimeProvider.System);
        var replayAfterRestart = await afterRestart.PostLedgerEntryAsync(Post("settlement-42"), CancellationToken.None);

        replayAfterRestart.LedgerEntryId.Should().NotBe(first.LedgerEntryId,
            "this is the defect W1.8 exists to close: one hand-to-hand cash collection, two ledger " +
            "entry ids, and the second silently overwrites the first on settlements.ledger_entry_id");

        // …and the fix is that the durable client is not this class.
        StoreDurabilityGuard.Critical
            .Single(c => c.Iface == typeof(ISettlementLedgerClient)).DurableImpls
            .Should().NotContain(typeof(InMemorySettlementLedgerClient))
            .And.NotContain(typeof(PostgresSettlementLedgerClient))
            .And.Contain(typeof(WalletSettlementLedgerClient));
    }

    // ── B6 — migration <-> client, the silent-failure class ────────────────

    /// <summary>
    /// Strip <c>//</c> and <c>/* */</c> comments. LOAD-BEARING, and the pack learned it the
    /// hard way: the first cut of B6 matched its <c>RETURNING</c> regex against the class's
    /// own XML doc ("…<c>RETURNING</c>: the FIRST post inserts and <i>yields the row</i>")
    /// and reported the projection as <c>{"yields the row"}</c> — a red on correct code.
    /// A source-text assertion that does not first remove prose is measuring the prose.
    /// </summary>
    private static string StripCsComments(string src)
    {
        src = Regex.Replace(src, @"/\*.*?\*/", "", RegexOptions.Singleline);
        return Regex.Replace(src, @"//[^\n]*", "");
    }

    private static string StripSqlComments(string sql)
        => Regex.Replace(sql, @"^\s*--[^\n]*$", "", RegexOptions.Multiline);

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "db", "migrations")))
            dir = dir.Parent;
        dir.Should().NotBeNull("the pack must be able to locate the repo root from the test binary");
        return dir!.FullName;
    }

    [Fact]
    public void B6_Migration_0044_And_The_Client_Agree_On_The_Table_The_Key_And_The_Projection()
    {
        var root = RepoRoot();
        var migrationPath = Path.Combine(root, "db", "migrations", "0044_init_settlement_ledger_entries.sql");
        var clientPath = Path.Combine(root, "src", "JeebGateway", "Financials", "PostgresSettlementLedgerClient.cs");

        File.Exists(migrationPath).Should().BeTrue($"missing {migrationPath}");
        File.Exists(clientPath).Should().BeTrue($"missing {clientPath}");

        var migration = StripSqlComments(File.ReadAllText(migrationPath));
        var client = StripCsComments(File.ReadAllText(clientPath));

        // POSITIVE CONTROL — both files parsed to real structure. Without this a regex
        // that matched nothing would satisfy every assertion below vacuously.
        var createTable = Regex.Match(migration,
            @"CREATE\s+TABLE\s+IF\s+NOT\s+EXISTS\s+settlement_ledger_entries\s*\((.*?)\n\s*\);",
            RegexOptions.Singleline);
        createTable.Success.Should().BeTrue("the migration must create settlement_ledger_entries idempotently");

        var pk = Regex.Match(createTable.Groups[1].Value, @"^\s*(\w+)[^,]*PRIMARY\s+KEY",
            RegexOptions.Multiline | RegexOptions.IgnoreCase);
        pk.Success.Should().BeTrue("the table must declare a PRIMARY KEY");
        pk.Groups[1].Value.Should().Be("idempotency_key",
            "the idempotency memo is the thing being moved out of process memory; " +
            "if it is not the key, a restart still permits a second entry");

        // THE MONEY INVARIANT: the conflict target must be that same PK, or the
        // ON CONFLICT clause never fires and every replay inserts a new row.
        var conflict = Regex.Match(client, @"ON\s+CONFLICT\s*\(\s*(\w+)\s*\)\s*DO\s+NOTHING",
            RegexOptions.IgnoreCase);
        conflict.Success.Should().BeTrue("the durable client must post with ON CONFLICT … DO NOTHING");
        conflict.Groups[1].Value.Should().Be(pk.Groups[1].Value,
            "ON CONFLICT must target the PRIMARY KEY (a mismatch is a runtime 42P10 on a money path)");

        // Both result paths are read by ORDINAL (GetString(0) / GetFieldValue(1)), so
        // the two projections must be identical or a replay returns a different shape.
        var returning = Regex.Match(client, @"RETURNING\s+([\w,\s]+)", RegexOptions.IgnoreCase);
        returning.Success.Should().BeTrue();
        var returningCols = returning.Groups[1].Value.Split(',').Select(c => c.Trim()).ToArray();
        returningCols.Should().Equal("ledger_entry_id", "posted_at");

        var selectCols = Regex.Match(client, @"SELECT\s+([\w,\s]+?)\s+FROM\s+settlement_ledger_entries",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        selectCols.Success.Should().BeTrue("the conflict read-back must query the same table");
        selectCols.Groups[1].Value.Split(',').Select(c => c.Trim()).Should().Equal(returningCols,
            "the insert projection and the read-back projection are consumed by the same ordinals");

        // The client must never invent an id when the read-back finds nothing.
        client.Should().Contain("throw new InvalidOperationException",
            "on an unreadable conflict the client must throw, never fabricate a ledger entry id for money");
    }

    [Fact]
    public void B6b_Migration_0044_Is_Uniquely_Numbered_And_Registers_Itself()
    {
        var dir = Path.Combine(RepoRoot(), "db", "migrations");
        var files = Directory.GetFiles(dir, "*.sql").Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal).ToArray();

        files.Should().NotBeEmpty();
        files.Should().Contain("0044_init_settlement_ledger_entries.sql",
            "W1.8's durable backing must not be renamed or renumbered out from under the ledger client");
        files.Count(n => n!.StartsWith("0044", StringComparison.Ordinal)).Should().Be(1,
            "two migrations sharing a number is a deploy hazard");

        // Head-position is not asserted: 0044 was the head when this landed, but any later
        // wave legitimately appends (W0-08 added 0045-0048). Prefix uniqueness is the invariant.
        var prefixes = files.Select(n => n!.Split('_')[0]).ToArray();
        prefixes.Should().OnlyHaveUniqueItems("two migrations sharing a number is a deploy hazard");

        // G-18: everything landing after 0044 must write its own schema_migrations row,
        // or db/apply.sh re-runs stop being idempotent.
        foreach (var later in files.Where(n => string.CompareOrdinal(n!.Split('_')[0], "0044") > 0))
        {
            var laterSql = StripSqlComments(File.ReadAllText(Path.Combine(dir, later!)));
            laterSql.Should().Contain("INSERT INTO schema_migrations",
                $"{later} must register itself (G-18) or apply.sh re-runs it every deploy");
        }

        var raw = File.ReadAllText(Path.Combine(dir, "0044_init_settlement_ledger_entries.sql"));
        var sql = StripSqlComments(raw);
        sql.Should().Contain("INSERT INTO schema_migrations",
            "an unregistered migration re-runs on every deploy");
        sql.Should().Contain("'0044_init_settlement_ledger_entries'",
            "it must register itself under its own version string");

        // Cash-only (locked owner directive). Asserted against the EXECUTABLE SQL only:
        // the header comment says "There is no unified_payment_gateway dependency here and
        // none may be added", so a raw-text NotContain reds on a file that is stating the
        // policy it obeys. Same trap as the `_comment_Fcm_REMOVED` tombstone in W0.6.
        sql.Should().NotContain("unified_payment_gateway",
            "Jeeb is cash-on-delivery only; the executable migration must not acquire a " +
            "payments-gateway dependency");
        raw.Should().Contain("cash-on-delivery only",
            "positive control: the comment stripper must not have emptied the file");
    }
}
