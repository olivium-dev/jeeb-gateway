#!/usr/bin/env bash
# Shared plumbing for the end-to-end chat+push outcome canary.
# Pure helpers below the transport section are unit-tested by test-canary-lib.sh.

set -uo pipefail

CANARY_MODE="${CANARY_MODE:-plan}"
CANARY_EVIDENCE="${CANARY_EVIDENCE:-}"
CANARY_WORKDIR="${CANARY_WORKDIR:-}"
CANARY_LAST_CODE=""
CANARY_LAST_BODY_FILE=""

# --- Logging + evidence

canary_log() {
  printf '%s\n' "$*"
  [ -n "$CANARY_EVIDENCE" ] && printf '%s\n' "$*" >>"$CANARY_EVIDENCE"
  return 0
}

canary_note() {
  canary_log "    $*"
}

# Fails the run naming the leg that died, then dumps the evidence tail.
canary_fail() {
  local leg="$1"; shift
  printf '\n' >&2
  printf '::error::CANARY FAILED at leg [%s]: %s\n' "$leg" "$*" >&2
  printf 'CANARY FAILED at leg [%s]: %s\n' "$leg" "$*" >&2
  if [ -n "$CANARY_EVIDENCE" ] && [ -s "$CANARY_EVIDENCE" ]; then
    printf -- '--- last 20 lines of evidence ---\n' >&2
    tail -n 20 "$CANARY_EVIDENCE" >&2
    printf -- '--- end evidence ---\n' >&2
  fi
  if [ -n "${GITHUB_STEP_SUMMARY:-}" ]; then
    printf 'CANARY FAILED at leg **%s** — %s\n' "$leg" "$*" >>"$GITHUB_STEP_SUMMARY"
  fi
  exit 1
}

# A finding that must be SEEN but must not stop the run: annotated in Actions,
# kept in the evidence, exit status untouched.
canary_warn() {
  printf '::warning::%s\n' "$*" >&2
  canary_log "  WARN $*"
  return 0
}

canary_require_tools() {
  local missing=""
  for tool in curl jq; do
    command -v "$tool" >/dev/null 2>&1 || missing="$missing $tool"
  done
  [ -z "$missing" ] || canary_fail preflight "required tools missing:$missing"
}

# --- Secret-safe transport. Plan mode prints the exact call and executes nothing.

# canary_http METHOD URL [--bearer-var N|--header H|--header-var K:V|--json B|--json-var NAME|--out F|--no-preview]
# Sets CANARY_LAST_CODE / CANARY_LAST_BODY_FILE. Never asserts; callers do.
canary_http() {
  local method="$1" url="$2"; shift 2
  local out="" body="" preview=1 timeout="${CANARY_HTTP_TIMEOUT:-25}"
  local -a args=(-sS -m "$timeout" -X "$method")
  local -a shown=("curl" "-sS" "-m" "$timeout" "-X" "$method")

  while [ $# -gt 0 ]; do
    case "$1" in
      --bearer-var)
        args+=(-H "Authorization: Bearer ${!2:-}")
        shown+=("-H" "'Authorization: Bearer \$$2'")
        shift 2 ;;
      --header)
        args+=(-H "$2"); shown+=("-H" "'$2'"); shift 2 ;;
      --header-var)
        # 'Header-Name:ENV_VAR' — the value is read from the env var and never printed.
        local hname="${2%%:*}" hvar="${2#*:}"
        args+=(-H "$hname: ${!hvar:-}")
        shown+=("-H" "'$hname: \$$hvar'")
        shift 2 ;;
      --json)
        body="$2"
        args+=(-H "Content-Type: application/json" --data-binary "$2")
        shown+=("-H" "'Content-Type: application/json'" "--data-binary" "'$2'")
        shift 2 ;;
      --json-var)
        # Body read from an env var and rendered as $NAME: for payloads carrying
        # a credential, which must never reach a plan or a log.
        body="${!2:-}"
        args+=(-H "Content-Type: application/json" --data-binary "${!2:-}")
        shown+=("-H" "'Content-Type: application/json'" "--data-binary" "\$$2")
        shift 2 ;;
      --out)
        out="$2"; shift 2 ;;
      --no-preview)
        # For responses that carry a token: log the status only, never the body.
        preview=0; shift ;;
      *) canary_fail internal "canary_http: unknown flag $1" ;;
    esac
  done

  [ -n "$out" ] || out="$(canary_tmpfile response)"
  args+=(-o "$out" -w '%{http_code}')
  shown+=("-o" "$out" "-w" "'%{http_code}'" "'$url'")
  CANARY_LAST_BODY_FILE="$out"

  if [ "$CANARY_MODE" != "execute" ]; then
    canary_log "  PLAN ${shown[*]}"
    printf '{}' >"$out"
    CANARY_LAST_CODE="000"
    return 0
  fi

  canary_log "  CALL $method $url"
  local code
  code="$(curl "${args[@]}" "$url" 2>/dev/null)"
  # curl already writes 000 via -w on a transport failure AND exits non-zero;
  # anything that is not exactly three characters is that failure.
  [ "${#code}" -eq 3 ] || code="000"
  CANARY_LAST_CODE="$code"
  if [ "$preview" -eq 1 ]; then
    canary_log "    HTTP $CANARY_LAST_CODE ($(canary_body_preview "$out"))"
  else
    canary_log "    HTTP $CANARY_LAST_CODE (body withheld: carries a token)"
  fi
  return 0
}

canary_tmpfile() {
  local name="${1:-tmp}"
  [ -n "$CANARY_WORKDIR" ] || CANARY_WORKDIR="$(mktemp -d)"
  printf '%s/%s-%s.json' "$CANARY_WORKDIR" "$name" "$RANDOM"
}

# Every JWT starts with `eyJ`, so this redacts bearers and ID tokens while
# leaving GUIDs and status text readable.
canary_body_preview() {
  local file="$1"
  [ -s "$file" ] || { printf 'empty body'; return 0; }
  head -c 240 "$file" | tr -d '\n' | tr -s ' ' \
    | sed -E 's/eyJ[A-Za-z0-9_-]+(\.[A-Za-z0-9_.-]+)?/<jwt-redacted>/g'
}

# Accepts a status list as "200 201" or "200|201". A `|` produced by parameter
# expansion is a literal, so `case $code in $want)` silently never matches.
canary_status_accepted() {
  local code="$1" want="${2//|/ }"
  case " $want " in
    *" $code "*) return 0 ;;
  esac
  return 1
}

# Asserts the last call's status. Plan mode is a no-op so a plan never fails.
canary_expect() {
  local leg="$1" want="$2" what="$3"
  [ "$CANARY_MODE" = "execute" ] || return 0
  canary_status_accepted "$CANARY_LAST_CODE" "$want" && return 0
  if [ "$CANARY_LAST_CODE" = "000" ]; then
    canary_fail "$leg" "$what got NO HTTP response (transport failure, DNS, TLS or timeout)"
  fi
  canary_fail "$leg" "$what expected HTTP $want, got $CANARY_LAST_CODE — $(canary_body_preview "$CANARY_LAST_BODY_FILE")"
}

# ::add-mask:: is an Actions runner directive; outside Actions it would just print
# the token, so a local run gets nothing.
canary_mask() {
  [ "$CANARY_MODE" = "execute" ] || return 0
  [ -n "${GITHUB_ACTIONS:-}" ] || return 0
  [ -n "${1:-}" ] || return 0
  printf '::add-mask::%s\n' "$1"
}

# A per-leg budget, clamped to the whole-run cap so --timeout is really enforced.
canary_deadline() {
  local deadline=$(( $(date +%s) + $1 ))
  local cap="${CANARY_HARD_DEADLINE:-0}"
  if [ "$cap" -gt 0 ] && [ "$deadline" -gt "$cap" ]; then
    deadline="$cap"
  fi
  printf '%s' "$deadline"
}

# canary_poll LEG DEADLINE_EPOCH INTERVAL DESCRIPTION -- command...
# Runs the command until it exits 0 or the deadline passes.
canary_poll() {
  local leg="$1" deadline="$2" interval="$3" what="$4"; shift 4
  [ "$1" = "--" ] && shift
  if [ "$CANARY_MODE" != "execute" ]; then
    # Plan mode walks the probe once so its calls appear in the plan, then stops.
    canary_log "  PLAN poll every ${interval}s until: $what"
    "$@" || true
    return 0
  fi
  local attempt=0
  while :; do
    attempt=$((attempt + 1))
    if "$@"; then
      canary_note "$what satisfied after $attempt attempt(s)"
      return 0
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      canary_fail "$leg" "$what was still unsatisfied when the deadline expired after $attempt attempt(s)"
    fi
    sleep "$interval"
  done
}

# --- Pure helpers — no network, unit-tested by test-canary-lib.sh

# Flash tier id out of GET /tiers; empty when the catalog has no Flash row.
canary_flash_tier_id() {
  jq -r '
    (.items // .tiers // [])
    | map(select((.name // "") | ascii_downcase == "flash"))
    | (.[0].id // "")
  ' 2>/dev/null || printf ''
}

# accessToken out of POST /auth/tokens, tolerating both casings.
canary_access_token() {
  jq -r '(.accessToken // .access_token // "")' 2>/dev/null || printf ''
}

# conversation_id out of the Jeeb conversation envelope.
canary_conversation_id() {
  jq -r '(.conversation_id // .conversationId // "")' 2>/dev/null || printf ''
}

# message_id out of the append response.
canary_message_id() {
  jq -r '(.message_id // .messageId // "")' 2>/dev/null || printf ''
}

# 0 when the availability row echoes coordinates within ~11 m of the requested fix.
# The fan-out and the offer radius policy read THIS row, so `latitude: null` on a
# 200 is a vacuous pass, not a cosmetic gap — that is the shape this catches.
canary_presence_fix_landed() {
  local lat="$1" lng="$2"
  jq -e --argjson lat "$lat" --argjson lng "$lng" '
    def abs: if . < 0 then -. else . end;
    (.latitude // .lat) as $rlat | (.longitude // .lng) as $rlng
    | ($rlat != null) and ($rlng != null)
      and ((($rlat - $lat) | abs) < 0.0001)
      and ((($rlng - $lng) | abs) < 0.0001)
  ' >/dev/null 2>&1
}

# 0 when the viewer-scoped message page contains $id AND names $viewer as viewer.
canary_message_visible() {
  local id="$1" viewer="$2"
  jq -e --arg id "$id" --arg viewer "$viewer" '
    ((.viewer_id // .viewerId // "") == $viewer)
    and (((.messages // .items // []) | map(.message_id // .messageId // "")) | index($id) != null)
  ' >/dev/null 2>&1
}

# PartnerCredentialStore accepts ONE identifier shape and derives it from the
# holder itself; any other string 409s in ValidateRuntimeIdentity before the wallet.
canary_runtime_partner_identifier() {
  printf 'devtool-partner-%s' "$(printf '%s' "$1" | tr -d '-' | tr '[:upper:]' '[:lower:]')"
}

# A fresh password per run: the credential is provisioned, used, then deleted,
# so nothing durable is derived from it and nothing is committed.
canary_partner_password() {
  local raw
  raw="$(head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 24)"
  printf 'Jc!%s' "${raw:-CanaryFallback12345678}"
}

# 0 when GET /v1/jeeb/wallet reports at least $minimum available.
canary_wallet_sufficient() {
  local minimum="$1"
  jq -e --argjson min "$minimum" '
    ((.availableBalance // .available_balance // .balance // 0) | tonumber) >= $min
  ' >/dev/null 2>&1
}

# 0 when push-notification's ledger shows a terminal dispatch for $user. With
# $allow_reject, `failed` counts: FCM rejecting a bogus token still proves the chain.
canary_dispatch_terminal() {
  local user="$1" allow_reject="${2:-true}"
  jq -e --arg user "$user" --arg allow "$allow_reject" '
    def terminal: if $allow == "true" then ["succeeded", "failed"] else ["succeeded"] end;
    (.items // [])
    | map(select((.target_user_id // .targetUserId // "") == $user))
    | map(select((.state // "") | IN(terminal[])))
    | length > 0
  ' >/dev/null 2>&1
}

# 0 when the inbox carries a new_request row whose body preview names $tag. The
# projection drops the request id entirely, so the run tag rides the description.
canary_inbox_hit() {
  local tag="$1"
  jq -e --arg tag "$tag" '
    (.items // .notifications // [])
    | map(select(
        (((.type // .Type // "") | ascii_downcase) | test("new_request"))
        and (((.body // .Body // "") + " " + (.title // .Title // "")) | contains($tag))))
    | length > 0
  ' >/dev/null 2>&1
}

# 0 when a Firestore runQuery result carries a document whose id is $id.
# runQuery returns [{document:{name:".../Messages/<id>"}}, …] or [{}] when empty.
canary_firestore_hit() {
  local id="$1"
  jq -e --arg id "$id" '
    (if type == "array" then . else [.] end)
    | map(.document.name // "")
    | map(select(endswith("/Messages/" + $id)))
    | length > 0
  ' >/dev/null 2>&1
}

# The structured query mirroring the mobile listener, as a JSON body.
canary_firestore_query() {
  local uid="$1" limit="${2:-50}"
  jq -nc --arg uid "$uid" --argjson limit "$limit" '{
    structuredQuery: {
      from: [{collectionId: "Messages"}],
      where: {
        fieldFilter: {
          field: {fieldPath: "VisibleTo"},
          op: "ARRAY_CONTAINS",
          value: {stringValue: $uid}
        }
      },
      orderBy: [{field: {fieldPath: "CreatedAt"}, direction: "DESCENDING"}],
      limit: $limit
    }
  }'
}
