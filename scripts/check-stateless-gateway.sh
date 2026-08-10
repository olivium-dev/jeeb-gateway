#!/usr/bin/env bash
# Hard production boundary for jeeb-gateway. There is no allowlist and no
# warning-only mode: any gateway database, local owner fallback, durable worker,
# or retired UPG wiring fails CI.
set -euo pipefail

root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
cd "$root"

source_root=src/JeebGateway
program=$source_root/Program.cs
project=$source_root/JeebGateway.csproj
allowlist=scripts/gateway-db-seam-allowlist.txt
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

active_local=$(rg -n '^[[:space:]]*builder\.Services\.Add(Singleton|Scoped|Transient).*?(InMemory|Postgres|DurableRequestsStore|InProcessCod|NewRequestFanoutQueue|CourierPositionQueue)' "$program" \
  | rg -v 'InMemorySynonymRegistry|InMemoryGeoIndex' || true)
if [ -n "$active_local" ]; then
  echo 'FAIL: production DI registers a local owner/state implementation'
  printf '%s\n' "$active_local"
  fail=1
fi

hosted=$(rg -n 'AddHostedService' "$source_root" -g '*.cs' \
  | rg -v '^[^:]+:[0-9]+:[[:space:]]*//' || true)
hosted_count=$(printf '%s\n' "$hosted" | rg -c '.' || true)
if [ "$hosted_count" -ne 2 ]; then
  echo "FAIL: expected only the BFF startup validator and capability-coverage guard; found $hosted_count hosted registrations"
  printf '%s\n' "$hosted"
  fail=1
fi

hosted_whitelist=$(printf '%s\n' "$hosted" \
  | rg -v 'Program\.cs:.*AddHostedService|BffServiceCollectionExtensions\.cs:.*AddHostedService' || true)
if [ -n "$hosted_whitelist" ]; then
  echo 'FAIL: hosted registration is not an approved startup/coverage validator'
  printf '%s\n' "$hosted_whitelist"
  fail=1
fi

report_matches 'background-service implementation remains in the gateway artifact source' \
  rg -n -g '*.cs' ':[[:space:]]*BackgroundService\b' "$source_root"

unexpected_hosted_type=$(rg -n -g '*.cs' 'class[[:space:]]+[^:]+:[[:space:]]*IHostedService\b' "$source_root" \
  | rg -v 'BffStartupValidator|CapabilityCoverageGuard' || true)
if [ -n "$unexpected_hosted_type" ]; then
  echo 'FAIL: non-validator IHostedService implementation remains in gateway source'
  printf '%s\n' "$unexpected_hosted_type"
  fail=1
fi

report_matches 'gateway-owned state worker or queue is registered' \
  rg -n 'Add(HostedService|Singleton|Scoped|Transient).*?(SettlementLedgerReconciler|WeeklySettlementBatch|RequestExpiry|RequestNudge|ScheduledDeliveryActivator|PushRetryQueueProcessor|NewRequestFanout|CourierPosition|DataExportProcessor|DataExportWorker|AccountDeletionPurgeWorker|AcceptChatSettleReconciler|RatingRevealJob|LowRatingAutoFlag|AutoOfflineSweeper|OtpHandoverSweeper)' \
    "$program" "$source_root/Extensions"

report_matches 'retired local owner/worker implementation remains in gateway source' \
  rg -n -g '*.cs' 'class[[:space:]]+(InProcessCodSettlementLedger|DurableRequestsStore|SettlementLedgerReconciler|WeeklySettlementBatch|RequestExpirySweeper|RequestNudgeSweeper|RequestExpiryObserver|ScheduledDeliveryActivator|PushRetryQueueProcessor|NewRequestFanoutProcessor|CourierPositionPublisher|DataExportProcessor|DataExportWorker|AccountDeletionPurgeWorker|AcceptChatSettleReconciler|RatingRevealJob|LowRatingAutoFlag|AutoOfflineSweeper|OtpHandoverSweeper|JeebNotificationCatalogSeeder|NotificationDurableWriteStartupAlarm|DefaultLexiconSeeder)\b' \
    "$source_root"

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
