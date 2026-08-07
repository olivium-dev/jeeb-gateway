#!/usr/bin/env bash
# Hard CI gate for the production Jeeb gateway boundary.
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
SRC="$ROOT/src/JeebGateway"
PROJECT="$SRC/JeebGateway.csproj"
PROGRAM="$SRC/Program.cs"
ALLOWLIST="$ROOT/scripts/gateway-db-seam-allowlist.txt"
WORKFLOW="$ROOT/.github/workflows/deploy-to-jeeb.yml"
COMPOSE="$ROOT/docker-compose.yml"
fail=0
gate_tmp="$(mktemp -d)"
trap 'rm -rf "$gate_tmp"' EXIT

report_matches() {
  local message="$1"
  shift
  echo "FAIL: $message"
  "$@" || true
  fail=1
}

echo "== Stateless gateway hard gate =="

if grep -vE '^[[:space:]]*(#|$)' "$ALLOWLIST" | grep -q .; then
  report_matches "the database seam allowlist must remain empty" \
    grep -n -vE '^[[:space:]]*(#|$)' "$ALLOWLIST"
else
  echo "OK: database seam allowlist is absolute zero"
fi

db_source_pattern='^[[:space:]]*using[[:space:]]+(Npgsql|Microsoft\.EntityFrameworkCore)|Npgsql(Connection|Command|DataSource|Transaction)|Use(Npgsql|SqlServer|Sqlite)|:[[:space:]]*DbContext|DbContextOptions'
if grep -RInE "$db_source_pattern" "$SRC" --include='*.cs' > "$gate_tmp/db-source.matches"; then
  report_matches "database provider code exists in the gateway source" \
    sed -n '1,120p' "$gate_tmp/db-source.matches"
else
  echo "OK: source contains no database provider code"
fi

db_package_pattern='PackageReference[[:space:]]+Include="(Npgsql|Npgsql\.|Microsoft\.EntityFrameworkCore|Pomelo\.EntityFrameworkCore|MySqlConnector|Microsoft\.Data\.SqlClient)'
if grep -RInE "$db_package_pattern" "$ROOT/src" --include='*.csproj' > "$gate_tmp/db-package.matches"; then
  report_matches "database provider package is referenced by the gateway" \
    sed -n '1,120p' "$gate_tmp/db-package.matches"
else
  echo "OK: production project has no database provider package"
fi

if grep -nE 'builder\.Configuration\[[^]]*(GatewayPostgres|WalletPostgres|ConnectionStrings:Default|DATABASE_URL|JEEB_DATABASE_URL)' "$PROGRAM" > "$gate_tmp/db-config.matches"; then
  report_matches "Program.cs reads a gateway database credential" \
    sed -n '1,120p' "$gate_tmp/db-config.matches"
else
  echo "OK: composition root reads no database credential"
fi

if grep -nE 'db/apply\.sh|postgresql-client|JEEB_DATABASE_URL.*secrets|DATABASE_URL:.*secrets' "$WORKFLOW" > "$gate_tmp/db-deploy.matches"; then
  report_matches "deployment workflow still connects to or migrates a gateway database" \
    sed -n '1,120p' "$gate_tmp/db-deploy.matches"
else
  echo "OK: deployment applies no gateway database migration"
fi

if grep -nE 'postgres:|ConnectionStrings__Default|postgres-data|POSTGRES_(DB|USER|PASSWORD)' "$COMPOSE" > "$gate_tmp/db-compose.matches"; then
  report_matches "docker-compose still provisions or configures a gateway database" \
    sed -n '1,120p' "$gate_tmp/db-compose.matches"
else
  echo "OK: docker-compose contains no gateway database"
fi

if ! grep -q 'append_env Settlements__CodOwnerVerified true' "$WORKFLOW"; then
  echo "FAIL: deployment does not explicitly drive the COD owner-readiness gate"
  fail=1
else
  echo "OK: deployment drives the fail-closed COD owner-readiness gate"
fi

if ! grep -q 'StatelessGatewayGuard.EnsureStateless' "$PROGRAM"; then
  echo "FAIL: production startup does not enforce the runtime stateless guard"
  fail=1
else
  echo "OK: production startup enforces the runtime stateless guard"
fi

if [ "$fail" -ne 0 ]; then
  echo "Stateless gateway gate FAILED"
  exit 1
fi

echo "Stateless gateway gate PASSED"
