#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
project="$repo_root/src/JeebGateway/JeebGateway.csproj"
output="${1:-$repo_root/artifacts/openapi/jeeb-gateway.v1.json}"
port="${JEEB_OPENAPI_PORT:-5199}"
scratch="$(mktemp -d)"
pid=""
export_signing_key="openapi-export-gateway-signing-key-at-least-32-bytes"

cleanup() {
  if [[ -n "$pid" ]]; then kill "$pid" 2>/dev/null || true; fi
  rm -rf "$scratch"
}
trap cleanup EXIT

(cd "$scratch" && dotnet build "$project" --nologo --verbosity:minimal)
(cd "$scratch" && OPENAPI_SIGNING_KEY="$export_signing_key" python3 - <<'PY' > "$scratch/bearer"
import base64, hashlib, hmac, json, os, time

def encoded(value):
    raw = json.dumps(value, separators=(",", ":")).encode()
    return base64.urlsafe_b64encode(raw).rstrip(b"=")

header = encoded({"alg": "HS256", "typ": "JWT"})
now = int(time.time())
payload = encoded({
    "iss": "jeeb-gateway",
    "aud": "jeeb-clients",
    "sub": "openapi-export",
    "roles": ["admin"],
    "iat": now,
    "nbf": now - 30,
    "exp": now + 300,
})
unsigned = header + b"." + payload
signature = hmac.new(os.environ["OPENAPI_SIGNING_KEY"].encode(), unsigned, hashlib.sha256).digest()
print((unsigned + b"." + base64.urlsafe_b64encode(signature).rstrip(b"=")).decode())
PY
)
(cd "$scratch" && \
  ASPNETCORE_ENVIRONMENT=Production \
  ASPNETCORE_URLS="http://127.0.0.1:$port" \
  Jwt__SigningKey="$export_signing_key" \
  AdminEvidence__TokenKey="openapi-export-admin-evidence-key-at-least-32-bytes" \
  Settlements__CodOwnerVerified=true \
  Features__Swagger__Enabled=true \
  Auth__Otp__ApplicationId="openapi-export-jeeb" \
  AdminOidc__Enabled=true \
  AdminOidc__Issuer="https://identity.example.test" \
  AdminOidc__AuthorizationEndpoint="https://identity.example.test/authorize" \
  AdminOidc__TokenEndpoint="https://identity.example.test/token" \
  AdminOidc__JwksUri="https://identity.example.test/jwks" \
  AdminOidc__ClientId="jeeb-admin-openapi-export" \
  AdminOidc__ClientSecret="openapi-export-client-secret" \
  AdminOidc__RedirectUri="https://admin.jeeb.example/gateway/admin/v1/auth/oidc/callback" \
  AdminOidc__StateProtectionKey="BwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwcHBwc=" \
  AdminOidc__RoleMappings__support__0="openapi-export-support" \
  dotnet run --no-build --no-launch-profile --project "$project" \
  >"$scratch/gateway.log" 2>&1) &
pid="$!"

for _ in {1..60}; do
  bearer="$(<"$scratch/bearer")"
  if curl -fsS -H "Authorization: Bearer $bearer" \
    "http://127.0.0.1:$port/swagger/v1/swagger.json" -o "$scratch/openapi.json"; then
    break
  fi
  if ! kill -0 "$pid" 2>/dev/null; then
    sed -n '1,200p' "$scratch/gateway.log" >&2
    exit 1
  fi
  sleep 0.5
done

jq -e '
  .openapi == "3.0.1" and
  (.info.license.name == "Proprietary") and
  (.servers == [{"url":"/gateway","description":"Same-origin Jeeb gateway"}]) and
  (.components.securitySchemes.Bearer.scheme == "bearer") and
  (.paths["/admin/v1/auth/refresh"].post | has("requestBody") | not) and
  (.paths | has("/admin/v1/auth/login") | not) and
  (.paths["/admin/v1/auth/refresh"].post.security == []) and
  (.paths["/admin/v1/auth/logout"].post.security == []) and
  (.paths["/admin/v1/auth/oidc/start"].get.security == []) and
  (.paths["/admin/v1/auth/oidc/callback"].get.security == []) and
  (.paths["/admin/v1/session"].get.security[0].Bearer == []) and
  ([.paths | keys[] | select(startswith("/api/User/"))] | length == 0) and
  (.components.schemas | has("AdminLoginRequest") | not) and
  ([.paths | to_entries[] | select(.key | startswith("/admin/")) | .value | to_entries[] | select(.key == "get" or .key == "post" or .key == "put" or .key == "patch" or .key == "delete") | .value.summary | select(type == "string" and length > 0)] | length) ==
    ([.paths | to_entries[] | select(.key | startswith("/admin/")) | .value | to_entries[] | select(.key == "get" or .key == "post" or .key == "put" or .key == "patch" or .key == "delete")] | length) and
  ([.paths | to_entries[] | .value | to_entries[] | select(.key == "get" or .key == "post" or .key == "put" or .key == "patch" or .key == "delete") | .value.operationId] | all(type == "string" and length > 0)) and
  (.components.schemas.GenericCaseEvidenceV1.properties.payload.type == "object") and
  (.components.schemas.GenericCaseEvidenceV1.properties.payload.nullable == true) and
  ([.paths["/admin/v1/deliveries"].get.parameters[].name] | sort == (["query", "status", "clientId", "jeeberId", "createdFrom", "createdTo", "sort", "limit", "cursor"] | sort)) and
  ([.paths["/admin/v1/deliveries/{deliveryId}/timeline"].get.parameters[].name] | sort == (["deliveryId", "limit", "cursor"] | sort)) and
  ([.paths["/admin/v1/settlements"].get.parameters[].name] | sort == (["query", "status", "providerId", "deliveryId", "from", "to", "sort", "limit", "cursor"] | sort)) and
  (.paths["/admin/v1/settlements/{settlementId}/dispute"].post.requestBody.content["application/json"].schema["$ref"] == "#/components/schemas/AdminDisputeSettlementRequest") and
  (.paths["/admin/v1/settlements/{settlementId}/resolve"].post.requestBody.content["application/json"].schema["$ref"] == "#/components/schemas/AdminResolveSettlementRequest") and
  (.paths["/admin/v1/deliveries/{deliveryId}"].get.responses["200"].content["application/json"].schema["$ref"] == "#/components/schemas/AdminDeliveryAggregateResponse") and
  (.paths["/admin/v1/cases/{id}"].get.responses["200"].content["application/json"].schema["$ref"] == "#/components/schemas/CaseDetailResponseV2")
' "$scratch/openapi.json" >/dev/null
mkdir -p "$(dirname "$output")"
cp "$scratch/openapi.json" "$output"
echo "$output"
