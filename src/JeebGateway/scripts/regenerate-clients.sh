#!/usr/bin/env bash
#
# regenerate-clients.sh — fetch upstream OpenAPI specs and regenerate NSwag clients.
#
# This is the canonical Olivium BFF pattern (see rahmah-gateway, salehly-gateway,
# cremat). The script:
#
#   1. Fetches each upstream service's OpenAPI document to contracts/<service>.openapi.json
#      using environment-overridable URLs (defaults target localhost dev ports).
#   2. Runs `nswag openapi2csclient` to regenerate Services/Generated/<Service>Client.cs
#      from each pinned spec.
#   3. Leaves changes uncommitted — the regeneration PR must commit both the spec
#      and the regenerated client together so a reviewer sees the contract diff.
#
# Idempotent: re-running with no upstream changes produces an empty diff.
# Required tools: bash, curl, dotnet, nswag (`dotnet tool install -g NSwag.Tool`).
#
# Exit codes:
#   0 — all configured specs fetched OK and all clients regenerated.
#   1 — nswag missing or other hard error.
#   2 — one or more upstream fetches failed (script continues for the rest;
#       missing specs fall back to placeholder so the build still passes).

set -euo pipefail

SCRIPT_DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"
PROJECT_DIR="$( cd "${SCRIPT_DIR}/.." && pwd )"
CONTRACTS_DIR="${PROJECT_DIR}/contracts"
GENERATED_DIR="${PROJECT_DIR}/Services/Generated"

mkdir -p "${CONTRACTS_DIR}" "${GENERATED_DIR}"

# ---------------------------------------------------------------------------
# Service registry
# ---------------------------------------------------------------------------
# Each row: <slug>|<ClientClassName>|<Namespace>|<default-openapi-url>|<env-var-override>
#
# Slug becomes <slug>.openapi.json and feeds the NSwag --classname/--namespace.
# Default URLs come from each upstream's Properties/launchSettings.json or
# FastAPI uvicorn run() port. Override via the listed env var for staging/prod.
#
# .NET services (Swashbuckle) expose at /swagger/v1/swagger.json.
# FastAPI services expose at /openapi.json.

SERVICES=(
  "auth-service|AuthServiceClient|JeebGateway.Services.Generated.AuthService|http://localhost:5000/swagger/v1/swagger.json|AUTH_SERVICE_OPENAPI_URL"
  "chat-service|ChatServiceClient|JeebGateway.Services.Generated.ChatService|http://localhost:5010/swagger/v1/swagger.json|CHAT_SERVICE_OPENAPI_URL"
  "user-management|UserManagementClient|JeebGateway.Services.Generated.UserManagement|http://localhost:5026/swagger/v1/swagger.json|USER_MANAGEMENT_OPENAPI_URL"
  "wallet-service|WalletServiceClient|JeebGateway.Services.Generated.WalletService|http://localhost:5026/swagger/v1/swagger.json|WALLET_SERVICE_OPENAPI_URL"
  "matching|MatchingServiceClient|JeebGateway.Services.Generated.Matching|http://localhost:8090/openapi.json|MATCHING_OPENAPI_URL"
  "notification-service|NotificationServiceClient|JeebGateway.Services.Generated.NotificationService|http://localhost:8000/openapi.json|NOTIFICATION_SERVICE_OPENAPI_URL"
  "geolocation-service|GeolocationServiceClient|JeebGateway.Services.Generated.GeolocationService|http://localhost:8085/openapi.json|GEOLOCATION_SERVICE_OPENAPI_URL"
  "push-notification|PushNotificationClient|JeebGateway.Services.Generated.PushNotification|http://localhost:8080/openapi.json|PUSH_NOTIFICATION_OPENAPI_URL"
  "delivery-service|DeliveryServiceClient|JeebGateway.Services.Generated.DeliveryService|http://localhost:8081/swagger/v1/swagger.json|DELIVERY_SERVICE_OPENAPI_URL"
)

# ---------------------------------------------------------------------------
# Toolchain check
# ---------------------------------------------------------------------------
if ! command -v nswag >/dev/null 2>&1; then
  echo "ERROR: nswag CLI is not installed."                                                  >&2
  echo "Install with: dotnet tool install -g NSwag.Tool --version 14.2.0"                     >&2
  exit 1
fi

if ! command -v curl >/dev/null 2>&1; then
  echo "ERROR: curl is required."                                                            >&2
  exit 1
fi

# ---------------------------------------------------------------------------
# Per-service fetch + regenerate
# ---------------------------------------------------------------------------
fetched_count=0
failed_count=0
generated_count=0

for row in "${SERVICES[@]}"; do
  IFS='|' read -r slug classname namespace default_url env_var <<<"${row}"

  # Allow per-service URL override via env var
  url="${!env_var:-${default_url}}"
  spec_path="${CONTRACTS_DIR}/${slug}.openapi.json"
  generated_path="${GENERATED_DIR}/${classname}.cs"

  echo ""
  echo "=== ${slug} ==="
  echo "    URL:  ${url}"
  echo "    Spec: ${spec_path}"

  # ---- Fetch spec (non-fatal: failure leaves any existing spec in place) ----
  tmp_spec="$( mktemp )"
  if curl --fail --silent --show-error --max-time 10 \
          --location --output "${tmp_spec}" "${url}"; then
    # Validate JSON before overwriting
    if jq empty "${tmp_spec}" >/dev/null 2>&1; then
      mv "${tmp_spec}" "${spec_path}"
      echo "    fetch: OK"
      fetched_count=$(( fetched_count + 1 ))
    else
      echo "    fetch: WARNING — response is not valid JSON; keeping existing spec" >&2
      rm -f "${tmp_spec}"
      failed_count=$(( failed_count + 1 ))
    fi
  else
    echo "    fetch: FAILED — keeping existing spec (or placeholder)" >&2
    rm -f "${tmp_spec}"
    failed_count=$(( failed_count + 1 ))
  fi

  # ---- Skip generation if no spec at all ----
  if [[ ! -f "${spec_path}" ]]; then
    echo "    generate: SKIPPED (no spec found and fetch failed)" >&2
    continue
  fi

  # ---- Generate client (always — picks up either fresh or pinned spec) ----
  if nswag openapi2csclient \
        "/input:${spec_path}" \
        "/classname:${classname}" \
        "/namespace:${namespace}" \
        "/output:${generated_path}" \
        /generateClientInterfaces:true \
        /injectHttpClient:true \
        /useHttpClientCreationMethod:false \
        /generateNullableReferenceTypes:true \
        /generateOptionalPropertiesAsNullable:true \
        /jsonLibrary:SystemTextJson \
        >/dev/null; then
    echo "    generate: OK → ${generated_path}"
    generated_count=$(( generated_count + 1 ))
  else
    echo "    generate: FAILED — see nswag output above" >&2
    failed_count=$(( failed_count + 1 ))
  fi
done

# ---------------------------------------------------------------------------
# Summary
# ---------------------------------------------------------------------------
echo ""
echo "============================================================"
echo "  Specs fetched:    ${fetched_count}"
echo "  Clients generated: ${generated_count}"
echo "  Failures:         ${failed_count}"
echo "============================================================"

if (( failed_count > 0 )); then
  echo "One or more services failed. The build will continue using pinned/placeholder specs." >&2
  exit 2
fi
