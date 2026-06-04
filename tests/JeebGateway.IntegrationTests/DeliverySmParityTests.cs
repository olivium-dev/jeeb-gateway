using System.Text.RegularExpressions;
using FluentAssertions;
using JeebGateway.Requests;
using Xunit;

namespace JeebGateway.IntegrationTests;

/// <summary>
/// ADR-002 PR-4 (owner-approved 2026-06-04) gateway ↔ delivery-service parity
/// test. Asserts the gateway's <see cref="DeliverySm"/> table produces EXACTLY
/// the same <c>(from, trigger) → to</c> set the delivery-service Go table
/// (<c>internal/status/status.go::transitions</c>) does — the gateway and the
/// durable status holder must never drift (ADR-002 Confirmation §).
///
/// Two layers of assurance:
///
///   1. <see cref="Gateway_Table_Matches_Frozen_ADR002_Edge_Set"/> — the
///      gateway table is diffed against the frozen ADR-002 §2.3 edge set
///      encoded literally here. This is the AUTHORITATIVE, CI-stable check: it
///      runs in an isolated checkout where the sibling Go repo is absent.
///
///   2. <see cref="Gateway_Table_Matches_DeliveryService_StatusGo_When_Present"/>
///      — when the sibling <c>delivery-service</c> repo IS on disk (local dev /
///      monorepo CI), the Go source is parsed and diffed live, so a drift on the
///      Go side fails the build immediately. Skipped (not failed) when the path
///      is absent, so the isolated-checkout build stays green.
/// </summary>
public class DeliverySmParityTests
{
    /// <summary>
    /// The frozen 14-row table from ADR-002 §2.3 (13 canonical edges + the
    /// AtDoor escalate_either alias of edge 11). The entry edge [*]→Ordered is
    /// NOT included — it lives in the offer/auction context outside the table.
    /// </summary>
    private static readonly HashSet<string> FrozenEdgeSet = new(StringComparer.Ordinal)
    {
        Edge("Ordered", "jeeber_tap", "Picked"),
        Edge("Ordered", "client_cancel_no_fee", "Cancelled"),
        Edge("Ordered", "jeeber_cancel_strike", "Cancelled"),
        Edge("Ordered", "escalate_either", "FailedNeedsEscalation"),
        Edge("Picked", "jeeber_tap", "InTransit"),
        Edge("Picked", "jeeber_cancel_high_strike", "Cancelled"),
        Edge("Picked", "escalate_either", "FailedNeedsEscalation"),
        Edge("InTransit", "jeeber_tap", "AtDoor"),
        Edge("InTransit", "escalate_either", "FailedNeedsEscalation"),
        Edge("AtDoor", "otp_verified", "Done"),
        Edge("AtDoor", "otp_fail_or_jeeber_escalate", "FailedNeedsEscalation"),
        Edge("AtDoor", "escalate_either", "FailedNeedsEscalation"),
        Edge("FailedNeedsEscalation", "admin_resolve", "Done"),
        Edge("FailedNeedsEscalation", "admin_cancel", "Cancelled"),
    };

    private static string Edge(string from, string trigger, string to) => $"{from}|{trigger}|{to}";

    [Fact]
    public void Gateway_Table_Matches_Frozen_ADR002_Edge_Set()
    {
        var gatewayEdges = DeliverySm.AllValidTransitions()
            .Select(t => Edge(t.From, t.Trigger, t.To))
            .ToHashSet(StringComparer.Ordinal);

        // Exact set equality — neither side may have an edge the other lacks.
        gatewayEdges.Except(FrozenEdgeSet).Should().BeEmpty(
            "the gateway table must not contain an edge the frozen ADR-002 §2.3 table lacks");
        FrozenEdgeSet.Except(gatewayEdges).Should().BeEmpty(
            "the gateway table must contain every edge in the frozen ADR-002 §2.3 table");
        gatewayEdges.Should().HaveCount(14);
    }

    [Fact]
    public void Gateway_Table_Matches_DeliveryService_StatusGo_When_Present()
    {
        var statusGoPath = LocateStatusGo();
        if (statusGoPath is null)
        {
            // Isolated checkout (gateway repo alone) — the authoritative check
            // is Gateway_Table_Matches_Frozen_ADR002_Edge_Set. Skip the live
            // cross-check rather than fail.
            Assert.True(true, "delivery-service/internal/status/status.go not present; live cross-check skipped");
            return;
        }

        var goEdges = ParseGoTransitions(File.ReadAllText(statusGoPath));
        goEdges.Should().NotBeEmpty("status.go must contain a parseable transitions table");

        var gatewayEdges = DeliverySm.AllValidTransitions()
            .Select(t => Edge(t.From, t.Trigger, t.To))
            .ToHashSet(StringComparer.Ordinal);

        gatewayEdges.Except(goEdges).Should().BeEmpty(
            "the gateway table has an edge delivery-service status.go does not — they have DRIFTED");
        goEdges.Except(gatewayEdges).Should().BeEmpty(
            "delivery-service status.go has an edge the gateway lacks — they have DRIFTED");
    }

    /// <summary>
    /// Walks up from the test assembly looking for a sibling
    /// <c>delivery-service/internal/status/status.go</c>. Returns null if not
    /// found within a bounded number of parent hops.
    /// </summary>
    private static string? LocateStatusGo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
        {
            var candidate = Path.Combine(
                dir.FullName, "delivery-service", "internal", "status", "status.go");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    /// <summary>
    /// Parses the <c>var transitions = map[...]{ ... }</c> literal out of
    /// status.go by resolving the Go const identifiers to their string values
    /// and walking the nested <c>StatusX: { TriggerY: StatusZ, }</c> blocks.
    /// Deliberately tolerant: it keys off the const tables so it survives Go
    /// formatting changes (gofmt) as long as the identifiers and the literal
    /// structure are stable.
    /// </summary>
    private static HashSet<string> ParseGoTransitions(string src)
    {
        // Resolve `StatusOrdered DeliveryStatus = "Ordered"` and
        // `TriggerJeeberTap Trigger = "jeeber_tap"` const declarations.
        var consts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(
                     src, @"(?<ident>\w+)\s+(?:DeliveryStatus|Trigger)\s*=\s*""(?<val>[^""]+)"""))
        {
            consts[m.Groups["ident"].Value] = m.Groups["val"].Value;
        }

        // Isolate the transitions map body.
        var mapMatch = Regex.Match(
            src,
            @"var\s+transitions\s*=\s*map\[DeliveryStatus\]map\[Trigger\]DeliveryStatus\s*\{(?<body>.*?)\n\}",
            RegexOptions.Singleline);
        mapMatch.Success.Should().BeTrue("status.go must declare `var transitions = map[...]`");

        var edges = new HashSet<string>(StringComparer.Ordinal);

        // Each outer row: `StatusX: { ... },`
        foreach (Match row in Regex.Matches(
                     mapMatch.Groups["body"].Value,
                     @"(?<from>\w+):\s*\{(?<inner>[^}]*)\}",
                     RegexOptions.Singleline))
        {
            if (!consts.TryGetValue(row.Groups["from"].Value, out var from))
            {
                continue;
            }
            // Each inner entry: `TriggerY: StatusZ,` (ignore trailing line comments).
            foreach (Match entry in Regex.Matches(
                         row.Groups["inner"].Value,
                         @"(?<trig>\w+):\s*(?<to>\w+)\s*,"))
            {
                if (consts.TryGetValue(entry.Groups["trig"].Value, out var trig) &&
                    consts.TryGetValue(entry.Groups["to"].Value, out var to))
                {
                    edges.Add(Edge(from, trig, to));
                }
            }
        }

        return edges;
    }
}
