#!/usr/bin/env bash
# Hard production boundary for jeeb-gateway. Database ownership and retired UPG
# wiring have no allowance. Transitional local owners and hosted workers are
# accepted only when named exactly in the reviewed ownership roster; arbitrary
# matches and count-only headroom are not accepted.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"

source_root=src/JeebGateway
program=$source_root/Program.cs
project=$source_root/JeebGateway.csproj
allowlist=scripts/gateway-db-seam-allowlist.txt
ownership_roster=scripts/stateless-gateway-ownership-roster.txt
fail=0

report_matches() {
  local title=$1
  shift
  local matches
  matches=$("$@" || true)
  if [ -n "$matches" ]; then
    echo "FAIL: $title"
    printf '%s\n' "$matches"
    fail=1
  fi
}

echo '== stateless gateway hard gate =='

for tool in rg perl; do
  if ! command -v "$tool" >/dev/null 2>&1; then
    echo "FAIL: required stateless-gate tool not found: $tool"
    exit 1
  fi
done

non_comment_allowlist=$(rg -v '^[[:space:]]*(#|$)' "$allowlist" || true)
if [ -n "$non_comment_allowlist" ]; then
  echo 'FAIL: gateway DB allowlist must be empty'
  printf '%s\n' "$non_comment_allowlist"
  fail=1
fi

report_matches 'database provider code remains in the gateway source' \
  rg -n -g '*.cs' '^[[:space:]]*using[[:space:]]+Npgsql|UseNpgsql|UseSqlServer|UseSqlite|DbContextOptions|:[[:space:]]*DbContext' "$source_root"

report_matches 'gateway project references a database provider' \
  rg -n 'PackageReference[^>]+(Npgsql|EntityFrameworkCore)|Testcontainers\.PostgreSql' "$project"

report_matches 'retired gateway Postgres provider file remains' \
  bash -c "rg --files '$source_root' | rg '/(Postgres|Npgsql)[^/]*\\.cs$'"

if [ ! -f "$ownership_roster" ]; then
  echo "FAIL: reviewed ownership roster not found at $ownership_roster"
  exit 1
fi

roster_rows=$(grep -vE '^[[:space:]]*(#|$)' "$ownership_roster" \
  | sed 's/[[:space:]]*#.*$//' | awk '{$1=$1; print}' | sort -u)
roster_errors=$(printf '%s\n' "$roster_rows" | awk '
  NF != 2 { print "  malformed row (want <category> <type>): " $0; next }
  $1 != "hosted-service" && $1 != "local-owner" {
    print "  unknown category " $1 ": " $0
  }
  $2 !~ /^[A-Za-z_][A-Za-z0-9_]*$/ { print "  invalid type: " $0 }
')
roster_dupes=$(grep -vE '^[[:space:]]*(#|$)' "$ownership_roster" \
  | sed 's/[[:space:]]*#.*$//' | awk '{$1=$1; print}' | sort | uniq -d || true)
if [ -n "${roster_errors//[[:space:]]/}" ] || [ -n "$roster_dupes" ]; then
  echo 'FAIL: stateless ownership roster is malformed'
  printf '%s\n' "$roster_errors"
  [ -z "$roster_dupes" ] || printf '  duplicate row: %s\n' $roster_dupes
  exit 1
fi

# Canonical symbol inventory instead of source line numbers or a numeric budget:
# a new owner/worker cannot hide behind an existing count, and deleting one
# requires removing its exact row in the same PR so the ratchet shrinks.
local_owner_types=$(LC_ALL=C perl -0777 -ne '
  while (/\.Add(?:Singleton|Scoped|Transient)\b(.*?);/gs) {
    $statement = $1;
    while ($statement =~ /\b(InMemory[A-Za-z0-9_]+|Postgres[A-Za-z0-9_]+|DurableRequestsStore|InProcessCod[A-Za-z0-9_]+|NewRequestFanoutQueue|CourierPositionQueue)\b/g) {
      $type = $1;
      next if $type eq "InMemorySynonymRegistry" || $type eq "InMemoryGeoIndex";
      print "$type\n";
    }
  }
' $(rg --files "$source_root" -g '*.cs') | sort -u)

hosted_types=$(LC_ALL=C perl -0777 -ne '
  while (/AddHostedService(?:\s*<\s*([A-Za-z0-9_.]+)\s*>\s*\(\s*\)|\s*\(\s*sp\s*=>\s*sp\.GetRequiredService\s*<\s*([A-Za-z0-9_.]+)\s*>\s*\(\s*\)\s*\)\s*)/g) {
    $type = $1 || $2;
    $type =~ s/.*\.//;
    print "$type\n";
  }
' $(rg --files "$source_root" -g '*.cs') | sort -u)

current_roster=$({
  printf '%s\n' "$local_owner_types" | sed '/^$/d; s/^/local-owner /'
  printf '%s\n' "$hosted_types" | sed '/^$/d; s/^/hosted-service /'
} | sort -u)

unapproved=$(comm -23 <(printf '%s\n' "$current_roster") <(printf '%s\n' "$roster_rows") || true)
stale=$(comm -13 <(printf '%s\n' "$current_roster") <(printf '%s\n' "$roster_rows") || true)
if [ -n "$unapproved" ]; then
  echo 'FAIL: production DI contains unreviewed local owner/hosted-service types'
  printf '%s\n' "$unapproved" | sed 's/^/  add only after ownership review: /'
  fail=1
fi
if [ -n "$stale" ]; then
  echo 'FAIL: ownership roster contains stale types; shrink it in the same PR'
  printf '%s\n' "$stale" | sed 's/^/  remove: /'
  fail=1
fi

# A dormant worker implementation is still gateway-owned execution. It must be
# represented by an approved hosted-service row even before someone registers it.
worker_types=$(rg -o -g '*.cs' \
    'class[[:space:]]+[A-Za-z_][A-Za-z0-9_]*[[:space:]]*:[[:space:]]*(BackgroundService|IHostedService)\b' \
    "$source_root" | sed -E 's/.*class[[:space:]]+([A-Za-z_][A-Za-z0-9_]*).*/\1/' | sort -u || true)
approved_hosted=$(printf '%s\n' "$roster_rows" | awk '$1 == "hosted-service" { print $2 }' | sort -u)
unapproved_workers=$(comm -23 <(printf '%s\n' "$worker_types") <(printf '%s\n' "$approved_hosted") || true)
if [ -n "$unapproved_workers" ]; then
  echo 'FAIL: background/IHostedService implementation is not in the reviewed roster'
  printf '  %s\n' $unapproved_workers
  fail=1
fi

if [ -z "$unapproved" ] && [ -z "$stale" ] && [ -z "$unapproved_workers" ]; then
  echo "OK: reviewed transitional inventory matches (local owners=$(printf '%s\n' "$local_owner_types" | grep -c .), hosted services=$(printf '%s\n' "$hosted_types" | grep -c .))."
fi

report_matches 'retired database/UPG selector remains in committed gateway configuration' \
  rg -n '"(GatewayPostgres|WalletPostgres|UnifiedPaymentGateway|UPG|DATABASE_URL|JEEB_DATABASE_URL)"[[:space:]]*:' "$source_root"/appsettings*.json

report_matches 'production deploy still migrates or configures a gateway database/UPG' \
  rg -n 'GatewayPostgres|WalletPostgres|JEEB_DATABASE_URL|DATABASE_URL|db/apply\.sh|psql|UnifiedPaymentGateway|UPG' .github/workflows/deploy-to-jeeb.yml

gateway_staging_case=$(awk '
  /^[[:space:]]*jeeb-gateway\)/ { capture=1 }
  capture { print }
  capture && /^[[:space:]]*;;[[:space:]]*$/ { exit }
' .github/workflows/jeeb-staging-deploy.yml)
if printf '%s\n' "$gateway_staging_case" \
    | rg -n 'GatewayPostgres|WalletPostgres|JEEB_DATABASE_URL|DATABASE_URL|UnifiedPaymentGateway|UPG' >/dev/null; then
  echo 'FAIL: staging jeeb-gateway service case carries a database/UPG setting'
  printf '%s\n' "$gateway_staging_case" \
    | rg -n 'GatewayPostgres|WalletPostgres|JEEB_DATABASE_URL|DATABASE_URL|UnifiedPaymentGateway|UPG'
  fail=1
fi

for required in \
  JeebStateService__ServiceTokenFile \
  DELIVERY_SERVICE_TOKEN_FILE \
  ServiceNotificationClient__ServiceTokenFile; do
  if ! rg -q "$required" .github/workflows/deploy-to-jeeb.yml .github/workflows/jeeb-staging-deploy.yml; then
    echo "FAIL: deployment does not mount/configure $required"
    fail=1
  fi
done

if ! rg -q 'GatewayDirectPushDispatchGuardHandler' "$program"; then
  echo 'FAIL: generated push client is missing the fail-closed direct-dispatch guard'
  fail=1
fi

report_matches 'retired/vulnerable package version remains in gateway/test projects or locks' \
  rg -n 'OpenTelemetry[^"\n]*Version="1\.9\.0|"Npgsql"|Testcontainers\.PostgreSql' \
    "$project" tests/JeebGateway.IntegrationTests/JeebGateway.IntegrationTests.csproj \
    "$source_root/packages.lock.json" tests/JeebGateway.IntegrationTests/packages.lock.json

if [ "$fail" -ne 0 ]; then
  echo 'stateless gateway hard gate FAILED'
  exit 1
fi

echo 'stateless gateway hard gate PASSED'
