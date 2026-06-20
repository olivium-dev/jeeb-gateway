#!/bin/bash
# =============================================================================
# test-super-login.sh — smoke-test for super-login and super-login+
#
# Super-login (basic)  : POST /api/User/user-id-login
#   - Proxied by the gateway to UM POST /api/User/user-id-login
#   - UM validates superAdminPassCode against SuperAdmin:PassCode config
#   - Default UM fallback passcode (when SuperAdmin__PassCode env var is unset):
#     123768  (computed as 123000 + 768 in UM Program.cs PostConfigure)
#   - Returns SocialLoginResponse {userId, authToken, refreshToken, recentlyCreated}
#   - The authToken is UM-signed and carries the 'admin' role claim
#
# Super-login+ (enhanced) : POST /auth/tokens
#   - Mints a GATEWAY-signed JWT for any userId, no UM round-trip
#   - Gate: Security:TokenMint:Enabled (SecurityOptions.cs)
#       false (local dev) => gate OPEN, no header required
#       true  (prod/staging) => requires X-Service-Auth-Key: <configured-key>
#   - Request body: {"userId": "<id>", "roles": ["admin"]}  (roles is optional)
#   - Returns TokenPairResponse {accessToken, refreshToken, tokenType, ...}
#
# Usage:
#   ./scripts/test-super-login.sh [GW_BASE_URL] [PASSCODE] [USER_ID] [MINT_KEY]
#
#   PASSCODE may also be supplied via the SUPER_LOGIN_PASSCODE env var.
#   If neither arg 2 nor SUPER_LOGIN_PASSCODE is set the script exits with an error.
#
# Examples:
#   # Local dev (TokenMint.Enabled=false, gate open -- passcode via env var):
#   SUPER_LOGIN_PASSCODE=<passcode> ./scripts/test-super-login.sh http://localhost:10090 '' <userId>
#   # or pass the passcode directly as arg 2:
#   ./scripts/test-super-login.sh http://localhost:10090 <passcode> <userId>
#
#   # Live/staging (TokenMint.Enabled=true, key required):
#   SUPER_LOGIN_PASSCODE=<passcode> ./scripts/test-super-login.sh http://192.168.2.7:10090 '' <userId> <mint-key>
#
# Prerequisites: curl, python3
# =============================================================================

set -euo pipefail

GW="${1:-http://localhost:10090}"
PASSCODE="${2:-${SUPER_LOGIN_PASSCODE:-}}"
if [[ -z "$PASSCODE" ]]; then
  echo "Error: Passcode required. Set SUPER_LOGIN_PASSCODE env var or pass as arg 2." >&2
  exit 1
fi
USER_ID="${3:-}"
MINT_KEY="${4:-}"

RED='\033[0;31m'; GREEN='\033[0;32m'; YELLOW='\033[1;33m'; NC='\033[0m'

pass() { echo -e "${GREEN}[PASS]${NC} $*"; }
fail() { echo -e "${RED}[FAIL]${NC} $*"; }
info() { echo -e "${YELLOW}[INFO]${NC} $*"; }

if [[ -z "$USER_ID" ]]; then
  info "No USER_ID provided. Attempting to seed a dev user via POST /dev/seed/user ..."
  info "(Requires Features__DevEndpoints__Enabled=true on the gateway)"
  SEED_RESP=$(curl -s --max-time 10 -X POST "$GW/dev/seed/user" \
    -H "Content-Type: application/json" \
    -d '{"username":"superlogin-smoke","role":"user"}' 2>/dev/null)
  USER_ID=$(echo "$SEED_RESP" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('userId',''))" 2>/dev/null || true)
  if [[ -z "$USER_ID" ]]; then
    fail "Could not seed a user and no USER_ID was provided."
    echo "  Seed response: $SEED_RESP"
    echo "  Re-run with: $0 $GW $PASSCODE <userId>"
    exit 1
  fi
  pass "Seeded dev user userId=$USER_ID"
fi

echo ""
echo "========================================================"
echo " Gateway : $GW"
echo " UserId  : $USER_ID"
echo " Passcode: $PASSCODE"
echo "========================================================"

# ─── Super-login (basic) ─────────────────────────────────────────────────────
echo ""
echo "=== [1] Super-login (basic) — POST /api/User/user-id-login ==="
SL_RESP=$(curl -s --max-time 10 -X POST "$GW/api/User/user-id-login" \
  -H "Content-Type: application/json" \
  -d "{\"userId\":\"$USER_ID\",\"superAdminPassCode\":\"$PASSCODE\"}" 2>/dev/null)

echo "Raw response: $SL_RESP" | head -c 300
echo ""

SL_TOKEN=$(echo "$SL_RESP" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('authToken',''))" 2>/dev/null || true)
if [[ -z "$SL_TOKEN" || "$SL_TOKEN" == "None" ]]; then
  fail "super-login did not return authToken"
  echo "  Full response: $SL_RESP"
else
  pass "super-login returned authToken"
  echo "  Token (first 60 chars): ${SL_TOKEN:0:60}..."

  echo ""
  echo "  JWT payload:"
  PAYLOAD=$(echo "$SL_TOKEN" | cut -d. -f2 | tr '_-' '/+')
  # Pad to multiple of 4
  PAD=$(( 4 - ${#PAYLOAD} % 4 ))
  [[ $PAD -lt 4 ]] && PAYLOAD="${PAYLOAD}$(printf '=%.0s' $(seq 1 $PAD))"
  echo "$PAYLOAD" | base64 -d 2>/dev/null | python3 -c "import json,sys; print(json.dumps(json.load(sys.stdin), indent=4))" 2>/dev/null || echo "  (could not decode payload)"
fi

# ─── Super-login+ (enhanced) ─────────────────────────────────────────────────
echo ""
echo "=== [2] Super-login+ (enhanced) — POST /auth/tokens ==="
if [[ -n "$MINT_KEY" ]]; then
  info "Using X-Service-Auth-Key header (TokenMint.Enabled=true mode)"
  SLP_RESP=$(curl -s --max-time 10 -X POST "$GW/auth/tokens" \
    -H "Content-Type: application/json" \
    -H "X-Service-Auth-Key: $MINT_KEY" \
    -d "{\"userId\":\"$USER_ID\",\"roles\":[\"admin\"]}" 2>/dev/null)
else
  info "No MINT_KEY — sending without X-Service-Auth-Key (requires TokenMint.Enabled=false)"
  SLP_RESP=$(curl -s --max-time 10 -X POST "$GW/auth/tokens" \
    -H "Content-Type: application/json" \
    -d "{\"userId\":\"$USER_ID\",\"roles\":[\"admin\"]}" 2>/dev/null)
fi

echo "Raw response: $SLP_RESP" | head -c 300
echo ""

SLP_TOKEN=$(echo "$SLP_RESP" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('accessToken',''))" 2>/dev/null || true)
if [[ -z "$SLP_TOKEN" || "$SLP_TOKEN" == "None" ]]; then
  fail "super-login+ did not return accessToken"
  echo "  Full response: $SLP_RESP"
  HTTP_STATUS=$(echo "$SLP_RESP" | python3 -c "import json,sys; d=json.load(sys.stdin); print(d.get('status',''))" 2>/dev/null || true)
  if [[ "$HTTP_STATUS" == "401" ]]; then
    info "Got 401 — the live gateway likely has TokenMint.Enabled=true."
    info "Provide the mint key as argument 4: $0 $GW $PASSCODE $USER_ID <mint-key>"
  fi
else
  pass "super-login+ returned gateway accessToken"
  echo "  Token (first 60 chars): ${SLP_TOKEN:0:60}..."

  echo ""
  echo "  JWT payload:"
  PAYLOAD2=$(echo "$SLP_TOKEN" | cut -d. -f2 | tr '_-' '/+')
  PAD2=$(( 4 - ${#PAYLOAD2} % 4 ))
  [[ $PAD2 -lt 4 ]] && PAYLOAD2="${PAYLOAD2}$(printf '=%.0s' $(seq 1 $PAD2))"
  echo "$PAYLOAD2" | base64 -d 2>/dev/null | python3 -c "import json,sys; print(json.dumps(json.load(sys.stdin), indent=4))" 2>/dev/null || echo "  (could not decode payload)"

  # ─── Verify elevated access ───────────────────────────────────────────────
  echo ""
  echo "=== [3] Verify elevated access — GET /v1/users/me ==="
  ME_RESP=$(curl -s --max-time 10 "$GW/v1/users/me" \
    -H "Authorization: Bearer $SLP_TOKEN" 2>/dev/null)
  echo "  /v1/users/me response:"
  echo "$ME_RESP" | python3 -c "import json,sys; print(json.dumps(json.load(sys.stdin), indent=4))" 2>/dev/null || echo "$ME_RESP"
fi

echo ""
echo "========================================================"
echo " Done."
echo "========================================================"
