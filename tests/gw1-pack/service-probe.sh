#!/usr/bin/env bash
# =============================================================================
# GW1 TEST PACK — the `service`-class leg. READ-ONLY against MSI (192.168.2.39).
#
# run-pack.sh is `static`/`build`/`suite` class and says so. Per GATE.md §3 none of
# those can prove that a live process resolved a durable store, so this file exists to
# give V-2 an instrument with controls instead of a bare curl.
#
# ---------------------------------------------------------------------------
# THIS SCRIPT WRITES NOTHING. NOT TO MSI, NOT TO THE DATABASE, NOT TO THE REPO.
# Every leg is a read: an HTTP GET, a file read over ssh, a journal read. It does not
# restart the unit — a restart mid-delivery strands settlement, and the restart leg is
# V-2's to schedule against the freeze calendar, not the test author's to fire.
#
# IT ALSO NEVER TOUCHES 192.168.2.20. The host is active Jeeb staging as of 2026-08-18,
# but this read-only MSI probe is not an approved staging deployment or database tool.
# That scope is why the row-level half of W1.8's claim is reported below as NOT PROVEN
# rather than quietly checked with a SELECT. See the NOT-PROVEN block at the end.
#
# AND IT NEVER TOUCHES 192.168.2.50. Nothing here resolves, dials or names it as a
# target; P5 asserts the live env file is clean of it.
# ---------------------------------------------------------------------------
#
# Usage:
#   tests/gw1-pack/service-probe.sh --self-test              # offline reader controls only
#   tests/gw1-pack/service-probe.sh --sha <full-sha>         # full live probe
#   tests/gw1-pack/service-probe.sh --sha <sha> --neg        # + the live NEG controls
#
# Exit code = number of FAILING legs (0 = all green). A leg that could not be measured
# is reported NOT-MEASURED and counted as a failure: per GATE.md §4 an unmeasurable
# check is not a check that passed.
# =============================================================================
set -uo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
LIB="$ROOT/tests/gw1-pack/lib"
MSI="${MSI_SH:-$(cd "$ROOT/../.." 2>/dev/null && pwd)/docs/agents/scripts/msi.sh}"
GW_URL="${GW_URL:-http://127.0.0.1:10090}"
ENVFILE="${GW_ENVFILE:-/home/ec2-user/iter5-native/env/gateway.env}"
PUBLISH="${GW_PUBLISH:-/home/ec2-user/iter5-native/publish}"
EXPECT_CRITICAL="${GW1_EXPECT_CRITICAL:-33}"

SHA=""; SELFTEST=0; NEG=0
while [ $# -gt 0 ]; do
  case "$1" in
    --sha) SHA="$2"; shift 2 ;;
    --self-test) SELFTEST=1; shift ;;
    --neg) NEG=1; shift ;;
    *) echo "unknown arg: $1" >&2; exit 2 ;;
  esac
done

FAILS=0
ok()  { echo "PASS $1  $2"; }
bad() { echo "FAIL $1  $2"; FAILS=$((FAILS+1)); }
nm()  { echo "NOT-MEASURED $1  $2  (counted as FAIL: GATE.md §4)"; FAILS=$((FAILS+1)); }

# ── R0 — the reader's own controls, ALWAYS first ──────────────────────────────
# A live green from an instrument never shown to reject is not evidence. If R0 does not
# behave, the script refuses to interpret live data at all.
echo "===== R0 — reader controls (offline) ====="
if python3 "$LIB/health-parse.py" --self-test; then
  ok R0 "the /health/ready reader accepts the post-GW1 payload and rejects 7 look-alikes"
else
  bad R0 "the reader could not be shown to behave — every live leg below is uninterpretable"
  echo "RESULT: refusing to probe live state with an unvalidated reader"
  exit 1
fi

if [ "$SELFTEST" -eq 1 ]; then
  echo; echo "RESULT: self-test only, no live legs run"; exit 0
fi

[ -n "$SHA" ] || { echo "--sha <full-sha> is required for the live probe" >&2; exit 2; }
[ -x "$MSI" ] || { echo "msi.sh not executable at $MSI (set MSI_SH=)" >&2; exit 2; }
SHORT="${SHA:0:7}"

echo
echo "host    : MSI 192.168.2.39 (the only server; owner rule)"
echo "expected: $SHA"
echo

# Two ssh round trips (one plain, one --sudo for the journal) collect everything, so
# every later leg reads ONE snapshot rather than a drifting live host.
#
# `; echo` after every value is LOAD-BEARING and this script's first run proved it:
# `curl` emits no trailing newline, so the next `echo '@@MARKER'` landed on the SAME
# line as the JSON body. The reader then saw `…"failing":[]}@@ENV_50`, rejected it as
# "Extra data", and the field AFTER it came back EMPTY — a red on P2 and a blank on P5
# that were entirely artefacts of the harness. `grep -c … || echo 0` is likewise gone:
# grep -c already prints `0` and then exits 1, so the `||` printed a SECOND zero and the
# field read `00`, which compares equal to nothing.
SNAP="$(mktemp)"; trap 'rm -f "$SNAP" "$SNAP".*' EXIT
"$MSI" "
  echo '@@DEPLOYED_SHA'; cat $PUBLISH/.deployed-sha 2>/dev/null; echo
  echo '@@CWD';          p=\$(systemctl show -p MainPID --value jeeb-gateway); readlink /proc/\$p/cwd 2>/dev/null; echo
  echo '@@ACTIVE';       systemctl is-active jeeb-gateway 2>/dev/null; echo
  echo '@@READY';        curl -s -m 15 $GW_URL/health/ready; echo
  echo '@@ENV_50_LIVE';  grep -F '192.168.2.50' $ENVFILE 2>/dev/null | grep -v -E '^[[:space:]]*#' | wc -l; echo
  echo '@@ENV_50_ALL';   grep -F '192.168.2.50' $ENVFILE 2>/dev/null | wc -l; echo
  echo '@@ENV_50_LINES'; grep -n -F '192.168.2.50' $ENVFILE 2>/dev/null; echo
  echo '@@ENV_LOOPBACK'; grep -c -F '127.0.0.1' $ENVFILE 2>/dev/null; echo
  echo '@@ENV_UPG_LIVE'; grep -F 'unified_payment_gateway' $ENVFILE 2>/dev/null | grep -v -E '^[[:space:]]*#' | wc -l; echo
  echo '@@ENV_UPG_ALL';  grep -F 'unified_payment_gateway' $ENVFILE 2>/dev/null | wc -l; echo
  echo '@@ENV_PG';       grep -c -F 'GatewayPostgres__ConnectionString' $ENVFILE 2>/dev/null; echo
  echo '@@ENV_HATCH';    grep -c -i -F 'StoreDurability__FailClosedDisabled' $ENVFILE 2>/dev/null; echo
  echo '@@ENV_ASPNET';   sed -n 's/^export ASPNETCORE_ENVIRONMENT=//p' $ENVFILE 2>/dev/null | tail -1; echo
  echo '@@END'
" > "$SNAP" 2>/dev/null

# The journal needs root, and msi.sh pipes the sudo password only in --sudo mode; a bare
# `sudo journalctl` in the plain call silently produced ZERO lines, which P7 would have
# read as "no boot line" instead of "no permission". Separate call, separate snapshot.
"$MSI" --sudo "
  echo '@@JOURNAL_LINES'; journalctl -u jeeb-gateway --since '-12h' --no-pager 2>/dev/null | wc -l; echo
  echo '@@JOURNAL_DUR';   journalctl -u jeeb-gateway --since '-12h' --no-pager 2>/dev/null | grep -c 'critical stores resolved to durable implementations'; echo
  echo '@@JOURNAL_POST';  journalctl -u jeeb-gateway --since '-12h' --no-pager 2>/dev/null | grep -c 'Settlement ledger entry posted idempotencyKey='; echo
  echo '@@JOURNAL_SWALLOW'; journalctl -u jeeb-gateway --since '-12h' --no-pager 2>/dev/null | grep -c 'Settlement ledger post failed for settlement'; echo
  echo '@@END'
" >> "$SNAP" 2>/dev/null

field() { awk -v k="@@$1" 'f && /^@@/ {exit} f {print} $0==k {f=1}' "$SNAP" | sed '/^$/d'; }

# ── P1 — deploy lineage: is the running binary the SHA under gate? ────────────
# "Health proves it RUNS; only identity/ancestry proves it is not a regression."
D_SHA="$(field DEPLOYED_SHA | tr -d ' \r\n')"
D_CWD="$(field CWD          | tr -d ' \r\n')"
D_ACT="$(field ACTIVE       | tr -d ' \r\n')"
if [ -z "$D_SHA" ] && [ -z "$D_CWD" ]; then
  nm P1 "no answer from MSI — the whole live half is unmeasured, not clean"
else
  [ "$D_SHA" = "$SHA" ] && ok P1a "deployed-sha == the SHA under gate  [$D_SHA]" \
                        || bad P1a "deployed-sha is '$D_SHA', wanted '$SHA'"
  case "$D_CWD" in
    *"$SHORT"*) ok P1b "the RUNNING process's cwd names that release  [$D_CWD]" ;;
    *)          bad P1b "MainPID cwd '$D_CWD' does not contain '$SHORT' — the stamp file and the live process disagree" ;;
  esac
  [ "$D_ACT" = "active" ] && ok P1c "unit is active  [$D_ACT]" || bad P1c "unit is '$D_ACT'"
fi

# ── P2 — THE row: the live process resolved every Critical store durably ─────
field READY > "$SNAP.ready"
if [ ! -s "$SNAP.ready" ]; then
  nm P2 "/health/ready returned nothing"
else
  if OUT="$(python3 "$LIB/health-parse.py" --expect-critical "$EXPECT_CRITICAL" "$SNAP.ready")"; then
    ok P2 "store-durability live verdict"
    echo "       | $OUT"
  else
    bad P2 "store-durability live verdict"
    echo "       | $OUT"
  fi
  # P3 — the durable-mirror leg, and it IS only a config implication (GW1.md V-2).
  if grep -q '"name":"gateway-postgres","status":"Healthy"' "$SNAP.ready"; then
    ok P3 "gateway-postgres Healthy — CONFIG IMPLICATION only: GatewayPostgres:ConnectionString is set and the DB answers. It does NOT read any resolved type"
  else
    bad P3 "gateway-postgres is not Healthy in the live payload"
  fi
fi

# ── P4 — is the BOOT gate genuinely armed on this host? ──────────────────────
# The readiness probe has no escape hatch (proven by the suite leg
# W18_LiveProbeInstrumentTests.S6), but the BOOT gate does. A live
# StoreDurability__FailClosedDisabled=true would mean a mis-provisioned deploy could
# have started at all, so it is asserted here rather than assumed.
E_HATCH="$(field ENV_HATCH | tr -d ' \r\n')"; E_PG="$(field ENV_PG | tr -d ' \r\n')"
E_ASPNET="$(field ENV_ASPNET | tr -d ' \r\n')"
if [ -z "${E_PG:-}" ]; then
  nm P4 "could not read $ENVFILE"
else
  [ "$E_HATCH" = "0" ] && ok P4a "no StoreDurability__FailClosedDisabled in the live env — the boot gate is armed  [0 hits]" \
                       || bad P4a "the fail-closed escape hatch appears $E_HATCH time(s) in the live env"
  # POSITIVE CONTROL — the same grep, same file, must find a key that IS there, or the
  # zero above is a zero because the file was unreadable.
  [ "${E_PG:-0}" -ge 1 ] && ok P4pos "POS control: the same grep finds GatewayPostgres__ConnectionString  [$E_PG]" \
                         || bad P4pos "POS control failed — the env grep found nothing at all, so P4a/P5/P6 zeros prove nothing"
  case "$E_ASPNET" in
    *Production*|*Staging*) ok P4b "ASPNETCORE_ENVIRONMENT is prod-like  [$E_ASPNET]" ;;
    "")                     echo "INFO P4b  ASPNETCORE_ENVIRONMENT not set in $ENVFILE; the live description string already proves non-exempt (a Development host would read 'exempt')" ;;
    *)                      bad P4b "ASPNETCORE_ENVIRONMENT='$E_ASPNET' — if this is Development the guard is a documented no-op and P2 is vacuous" ;;
  esac
fi

# ── P5 / P6 — the two locked policies, on the LIVE config ────────────────────
#
# CLASSIFIED, never a bare count, and the probe's own first run is the reason. A raw
# `grep -c 192.168.2.50` on the live env returns 1 and reds — on a `#` COMMENT at line
# 155 documenting the historical unreachable swarm. AGENTS.md counts 33 such inert
# mentions in the repo for the same reason: a gate has to be able to name what it
# forbids. A comment cannot dial. Only a non-comment line can, so only that is scored —
# and every hit is PRINTED, never suppressed.
E_50L="$(field ENV_50_LIVE | tr -d ' \r\n')"; E_50A="$(field ENV_50_ALL | tr -d ' \r\n')"
E_LO="$(field ENV_LOOPBACK | tr -d ' \r\n')"
E_UPGL="$(field ENV_UPG_LIVE | tr -d ' \r\n')"; E_UPGA="$(field ENV_UPG_ALL | tr -d ' \r\n')"
if [ "${E_50L:-x}" = "0" ]; then
  ok P5 "no LIVE 192.168.2.50 in the gateway env  [live=$E_50L of $E_50A total mention(s), the rest are comments]"
  [ "${E_50A:-0}" -gt 0 ] && field ENV_50_LINES | sed 's/^/       | [INERT] /'
else
  bad P5 "192.168.2.50 appears on $E_50L NON-COMMENT line(s) in the live gateway env — this can dial"
  field ENV_50_LINES | sed 's/^/       | /'
fi
[ "${E_LO:-0}" -ge 1 ] && ok P5pos "POS control: the same grep finds 127.0.0.1 overrides  [$E_LO]" || bad P5pos "POS control failed — the .50 zero is unfounded"
[ "${E_UPGL:-x}" = "0" ] && ok P6 "no LIVE unified_payment_gateway in the gateway env (cash-only)  [live=$E_UPGL of $E_UPGA total]" \
                         || bad P6 "unified_payment_gateway appears on $E_UPGL NON-COMMENT line(s) in the live gateway env"

# ── P7 — the boot line, from the process's own journal ───────────────────────
J_ALL="$(field JOURNAL_LINES | tr -d ' \r\n')"; J_DUR="$(field JOURNAL_DUR | tr -d ' \r\n')"
J_POST="$(field JOURNAL_POST | tr -d ' \r\n')"
if [ -z "${J_ALL:-}" ] || [ "${J_ALL:-0}" -eq 0 ]; then
  nm P7 "the journal read returned 0 lines — a 0 in P7/P8 would be a reading failure, not a finding"
else
  ok P7pos "POS control: the journal is readable  [$J_ALL line(s) in the last 12h]"
  [ "${J_DUR:-0}" -ge 1 ] && ok P7 "boot line 'critical stores resolved to durable implementations' present  [$J_DUR]" \
                          || bad P7 "no store-durability boot line in the last 12h"
  # P9 — the SILENT-FAILURE discriminator, and this one IS scored. If migration 0044 were
  # missing on this host, every ledger post would throw 42P01 and SettlementService would
  # SWALLOW it: no 5xx, no red health check, ledger_entry_id permanently NULL and the 60 s
  # reconciler retrying forever. The only externally visible trace is this warning. A
  # non-zero count is the one thing that would falsify the deployment silently.
  # P7 is P9's POSITIVE CONTROL: it finds a real pattern in this same journal window, so
  # a zero here is a measured zero and not a grep that matched nothing because it could
  # not read. Without P7 green, P9's zero would be indistinguishable from silence.
  J_SWALLOW="$(field JOURNAL_SWALLOW | tr -d ' \r\n')"
  [ "${J_SWALLOW:-x}" = "0" ] \
    && ok P9 "no swallowed 'Settlement ledger post failed for settlement' warning in the window  [0]" \
    || bad P9 "$J_SWALLOW swallowed ledger-post failure(s) in the window — the post is throwing and nothing else would show it (missing migration 0044? wrong schema?)"

  # P8 is REPORTED, never scored. See the NOT-PROVEN block.
  echo "INFO P8  'Settlement ledger entry posted idempotencyKey=' lines in the last 12h: ${J_POST:-0}"
  echo "       | 0 is NOT a failure and NOT a pass: it means no COD settlement occurred in the window."
  echo "       | This line exists ONLY in PostgresSettlementLedgerClient and is written only AFTER the"
  echo "       | INSERT executed against the deployed schema and returned a row (suite leg"
  echo "       | W18_LiveProbeInstrumentTests.S5 proves the in-memory client cannot emit it and has no"
  echo "       | logger at all). So >=1 here is positive evidence that migration 0044 is applied and the"
  echo "       | client's SQL is valid; 0 is simply no observation."
fi

# ── LIVE NEGATIVE CONTROLS (opt-in, still read-only) ─────────────────────────
if [ "$NEG" -eq 1 ]; then
  echo
  echo "===== live NEG controls (read-only: wrong inputs, same instruments) ====="
  BOGUS="0000000000000000000000000000000000000000"
  if [ "$D_SHA" = "$BOGUS" ]; then echo "BAD  NEG1 unusable"; FAILS=$((FAILS+1))
  else echo "OK   NEG1  P1a compares by equality: deployed '$D_SHA' != bogus '$BOGUS', so a wrong SHA reds"; fi
  if python3 "$LIB/health-parse.py" --expect-critical 32 "$SNAP.ready" >/dev/null 2>&1; then
    echo "BAD  NEG2  the live payload was ACCEPTED against the pre-GW1 count 32 — P2 is not discriminating"
    FAILS=$((FAILS+1))
  else
    echo "OK   NEG2  the same live payload is REJECTED against --expect-critical 32 (the pre-GW1 baseline)"
    python3 "$LIB/health-parse.py" --expect-critical 32 "$SNAP.ready" 2>&1 | sed 's/^/        -> /'
  fi
fi

cat <<'NOTPROVEN'

===== NOT PROVEN BY THIS PROBE (printed every run) =====
  A LEDGER ROW ROUND-TRIPPING THROUGH POSTGRES. This probe proves the live process
  RESOLVED ISettlementLedgerClient to PostgresSettlementLedgerClient (P2 — a live
  concrete-type read, not an array length). It does NOT prove a row was written and
  read back. Executing SQL would require either Testcontainers (needs Docker, banned)
  or psql against the active staging datastore at 192.168.2.20, which is outside
  this read-only MSI probe's scope. No SELECT is run and none should be.

  THE SANCTIONED SUBSTITUTE, for whoever schedules the live round:
    1. drive ONE COD delivery to settled (real flow, no API-driving);
    2. journal must show `Settlement ledger entry posted idempotencyKey=<settlementId>
       … ledgerEntryId=<guid>` — that line is unique to the durable client and is
       written only after the INSERT succeeded against the deployed schema;
    3. it must NOT show SettlementService's swallowed ledger warning for that id;
    4. `msi.sh --sudo systemctl restart jeeb-gateway`, then re-read.

  AND A TRAP IN THE OBVIOUS VERSION OF STEP 4, worth more than the rest of this note:
  "POST /deliveries/{id}/settle again after the restart and check the ledger id is the
  same" is INVARIANT UNDER GW1 and proves nothing. SettlementService stamps
  settlements.ledger_entry_id (PostgresSettlementStore, migration 0015 — durable since
  long before this batch) and SettleAsync short-circuits an already-settled row, so the
  replay returns the stored stamp whichever ledger client is wired. The defect W1.8
  closes only bites when the stamp is NULL: SettlementLedgerReconciler then replays that
  settlement id every 60 s, and with the in-memory memo emptied by the restart it mints
  a SECOND ledger_entry_id for one cash collection. A round that cannot produce a
  NULL-stamped row has not exercised the claim. Say so rather than scoring it.

  ANY PUSH BEHAVIOUR (W0.6). `device` class. Nothing here observes a handset.
NOTPROVEN

echo
if [ "$FAILS" -eq 0 ]; then echo "RESULT: all live legs PASS"; else echo "RESULT: $FAILS leg(s) FAIL/NOT-MEASURED"; fi
exit "$FAILS"
