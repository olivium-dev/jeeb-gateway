#!/usr/bin/env bash
set -euo pipefail

repository_root=$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)
contract="$repository_root/scripts/staging-gateway-candidate-contract.jq"
test_root=$(mktemp -d)
trap 'rm -rf -- "$test_root"' EXIT
candidate="$test_root/candidate.json"
mutant="$test_root/mutant.json"
cutover="$test_root/cutover.json"
otp_cutover="$test_root/otp-cutover.json"
incumbent="$test_root/incumbent.json"
devtool_incumbent="$test_root/devtool-incumbent.json"
devtool_candidate="$test_root/devtool-candidate.json"
firebase_secret="$test_root/firebase-secret.json"
image=repo@sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
network_id=networkabc

cat > "$candidate" <<JSON
{
  "TaskTemplate": {
    "ContainerSpec": {
      "Image": "$image",
      "Env": [
        "Services__ServiceOTP__BaseUrl=http://jeeb-staging-one-time-password:8080",
        "ServiceOTPApi__BaseUrl=http://jeeb-staging-one-time-password:8080",
        "FeatureFlags__UseUpstream__Otp=true",
        "FeatureFlags__UseUpstream__Chat=false",
        "FeatureFlags__UseUpstream__Realtime=false",
        "Auth__Otp__ApplicationId=0d51afe1-499f-4a29-a55a-36d2dd223b05",
        "Auth__Otp__Phone__AllowedRegion=LB",
        "Auth__Otp__Phone__EnforceRegion=false",
        "Services__Realtime__BaseUrl=http://jeeb-staging-realtime-comunication-service:4000",
        "FeatureFlags__UseUpstream__Voice=false",
        "SuperLogin__OpenMode=true",
        "DemoUsers__Enabled=true",
        "Features__DevEndpoints__Enabled=true",
        "Features__Swagger__Enabled=true",
        "Jwt__SigningKeyFile=/run/secrets/jeeb_gateway_jwt",
        "ServiceNotificationClient__ServiceTokenFile=/run/secrets/notification_service_token",
        "Features__RealtimeWebSocketProxy__Enabled=false",
        "JeebFirebaseContract__SchemaVersion=1",
        "JeebFirebaseContract__ProjectId=jeeb-5a293",
        "JeebFirebaseContract__ProjectNumber=1051234312170",
        "JeebFirebaseContract__FirestoreDatabaseId=(default)",
        "JeebFirebaseContract__ChatEnabled=true",
        "JeebFirebaseContract__PushProducer=notification-service",
        "Firebase__Chat__ProjectId=jeeb-5a293",
        "Firebase__Chat__ServiceAccountKeyPath=/run/secrets/firebase_admin_json",
        "FeatureFlags__NotificationDurableWrite__Enabled=true",
        "FeatureFlags__NotificationOutboxMode=upstream-authority",
        "FeatureFlags__PushDispatchMode=local"
      ],
      "Secrets": [{
        "SecretID":"firebaseid",
        "SecretName":"jeeb_staging_fb_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
        "File":{"Name":"firebase_admin_json","UID":"65532","GID":"65532","Mode":256}
      }]
    },
    "Networks": [{"Target":"$network_id"}]
  },
  "EndpointSpec": {
    "Mode": "vip",
    "Ports": [{"PublishedPort":10000,"TargetPort":8080,"PublishMode":"ingress"}]
  },
  "Mode": {"Replicated":{"Replicas":1}},
  "UpdateConfig": {"Parallelism":1,"Monitor":20000000000,"Order":"start-first","FailureAction":"pause"},
  "RollbackConfig": {"Order":"start-first","FailureAction":"pause"}
}
JSON
jq '[.TaskTemplate.ContainerSpec.Secrets[] | select(.File.Name == "firebase_admin_json")]' \
  "$candidate" > "$firebase_secret"

validate() {
  local document=$1 mode=${2:-normal} incumbent_document=${3:-$candidate}
  jq -e --arg image "$image" --arg network_id "$network_id" \
    --argjson published 10000 --arg deployment_mode "$mode" \
    --slurpfile incumbent "$incumbent_document" \
    -f "$contract" "$document" >/dev/null
}

reject_mutant() {
  local description=$1 filter=$2
  jq "$filter" "$candidate" > "$mutant"
  if validate "$mutant"; then
    echo "candidate contract accepted unsafe mutant: $description" >&2
    exit 1
  fi
}

accept_mutant() {
  local description=$1 filter=$2
  jq "$filter" "$candidate" > "$mutant"
  if ! validate "$mutant"; then
    echo "candidate contract rejected safe mutant: $description" >&2
    exit 1
  fi
}

validate "$candidate"
validate "$candidate" devtool-reassert
jq '
  .UpdateConfig = {
    Parallelism:1,Monitor:20000000000,FailureAction:"pause",Order:"start-first"
  }
  | .RollbackConfig = {
    Parallelism:1,Monitor:20000000000,FailureAction:"pause",Order:"start-first"
  }
' "$candidate" > "$cutover"
validate "$cutover" security-cutover
validate "$candidate" security-cutover
validate "$cutover" normal
if validate "$cutover" invalid-mode; then
  echo 'deployment-mode validation is not fail-closed' >&2
  exit 1
fi
jq '
  .TaskTemplate.ContainerSpec.Env += [
    "ServiceAuth__Enabled=true",
    "ServiceAuth__Caller=jeeb-gateway",
    "ServiceAuth__SigningKeyFile=/run/secrets/jeeb_gateway_service_auth"
  ]
  | .TaskTemplate.ContainerSpec.Secrets += [{
      SecretID:"secretid",SecretName:"rotated-service-auth",
      File:{Name:"jeeb_gateway_service_auth",UID:"65532",GID:"65532",Mode:256}
    }]
' "$candidate" > "$otp_cutover"
validate "$otp_cutover" otp-cutover

# Security/OTP cutovers must preserve these two feature rows exactly, including
# accepted .NET boolean casing; they do not reassert the Dev Tool posture.
jq '(.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=FALSE"
  | (.TaskTemplate.ContainerSpec.Env[13]) = "Features__Swagger__Enabled=TrUe"' \
  "$candidate" > "$incumbent"
jq '(.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=FALSE"
  | (.TaskTemplate.ContainerSpec.Env[13]) = "Features__Swagger__Enabled=TrUe"' \
  "$cutover" > "$mutant"
validate "$mutant" security-cutover "$incumbent"
jq '(.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=FALSE"
  | (.TaskTemplate.ContainerSpec.Env[13]) = "Features__Swagger__Enabled=TrUe"' \
  "$otp_cutover" > "$mutant"
validate "$mutant" otp-cutover "$incumbent"
jq '(.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=true"' \
  "$mutant" > "$test_root/non-preserving.json"
if validate "$test_root/non-preserving.json" otp-cutover "$incumbent"; then
  echo 'otp-cutover accepted DevEndpoints drift from its incumbent' >&2
  exit 1
fi
jq 'del(.TaskTemplate.ContainerSpec.Env[13,12])' "$candidate" \
  > "$test_root/absent-incumbent.json"
jq 'del(.TaskTemplate.ContainerSpec.Env[13,12])' "$cutover" \
  > "$test_root/absent-security.json"
validate "$test_root/absent-security.json" security-cutover \
  "$test_root/absent-incumbent.json"
jq 'del(.TaskTemplate.ContainerSpec.Env[13,12])' "$otp_cutover" \
  > "$test_root/absent-otp.json"
validate "$test_root/absent-otp.json" otp-cutover \
  "$test_root/absent-incumbent.json"
for unsafe_filter in \
  'del(.TaskTemplate.ContainerSpec.Env[-1])' \
  '(.TaskTemplate.ContainerSpec.Env[-1]) = "ServiceAuth__SigningKey=inline-secret"' \
  'del(.TaskTemplate.ContainerSpec.Secrets)' \
  '.TaskTemplate.ContainerSpec.Secrets[0].File.Mode = 292'; do
  jq "$unsafe_filter" "$otp_cutover" > "$mutant"
  if validate "$mutant" otp-cutover; then
    echo "otp-cutover contract accepted unsafe mutant: $unsafe_filter" >&2
    exit 1
  fi
done
for unsafe_filter in \
  '.UpdateConfig.Parallelism = 2' \
  '.UpdateConfig.Monitor = 19999999999' \
  '.UpdateConfig.FailureAction = "rollback"' \
  '.UpdateConfig.Order = "stop-first"' \
  '.RollbackConfig.Order = "stop-first"'; do
  jq "$unsafe_filter" "$cutover" > "$mutant"
  if validate "$mutant" security-cutover; then
    echo "security-cutover contract accepted unsafe mutant: $unsafe_filter" >&2
    exit 1
  fi
done
accept_mutant 'unrelated upgrade key and hostname contain a non-token UPG substring' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__UpgradePolicy__BaseUrl=http://upgrade.internal"]'
accept_mutant 'unrelated backup-gateway word contains a non-token UPG substring' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__BackupGateway__BaseUrl=http://backupgateway.internal"]'
reject_mutant 'missing compatibility OTP alias' \
  'del(.TaskTemplate.ContainerSpec.Env[1])'
reject_mutant 'host-port OTP endpoint' \
  '(.TaskTemplate.ContainerSpec.Env[0]) = "Services__ServiceOTP__BaseUrl=http://192.168.2.20:10037"'
reject_mutant 'host-port realtime endpoint' \
  '(.TaskTemplate.ContainerSpec.Env[8]) = "Services__Realtime__BaseUrl=http://192.168.2.20:10069"'
reject_mutant 'Chat activated in A1' \
  '(.TaskTemplate.ContainerSpec.Env[3]) = "FeatureFlags__UseUpstream__Chat=true"'
reject_mutant 'Realtime activated in A1' \
  '(.TaskTemplate.ContainerSpec.Env[4]) = "FeatureFlags__UseUpstream__Realtime=true"'
reject_mutant 'WebSocket proxy activated in A1' \
  '(.TaskTemplate.ContainerSpec.Env[16]) = "Features__RealtimeWebSocketProxy__Enabled=true"'
reject_mutant 'wrong b05 application ID' \
  '(.TaskTemplate.ContainerSpec.Env[5]) = "Auth__Otp__ApplicationId=wrong"'
reject_mutant 'international eligibility disabled' \
  '(.TaskTemplate.ContainerSpec.Env[7]) = "Auth__Otp__Phone__EnforceRegion=true"'
reject_mutant 'Voice activated' \
  '(.TaskTemplate.ContainerSpec.Env[9]) = "FeatureFlags__UseUpstream__Voice=true"'
reject_mutant 'case-insensitive duplicate environment key' \
  '.TaskTemplate.ContainerSpec.Env += ["DEMousers__enabled=false"]'
reject_mutant 'missing Firebase credential path' \
  'del(.TaskTemplate.ContainerSpec.Env[] | select(startswith("Firebase__Chat__ServiceAccountKeyPath=")))'
reject_mutant 'missing Firebase secret target' \
  'del(.TaskTemplate.ContainerSpec.Secrets[] | select(.File.Name == "firebase_admin_json"))'
reject_mutant 'stale duplicate Firebase secret target' \
  '.TaskTemplate.ContainerSpec.Secrets += [{SecretID:"stale",SecretName:"jeeb_staging_fb_bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",File:{Name:"firebase_admin_json",UID:"65532",GID:"65532",Mode:256}}]'
reject_mutant 'run-addressed Firebase secret source' \
  '(.TaskTemplate.ContainerSpec.Secrets[] | select(.File.Name == "firebase_admin_json")).SecretName = "jeeb_staging_fb_123_1"'
reject_mutant 'legacy named Firestore selector' \
  '.TaskTemplate.ContainerSpec.Env += ["Firestore__DatabaseId=staging"]'
reject_mutant 'colon-delimited stale Chat activation' \
  '.TaskTemplate.ContainerSpec.Env += ["FeatureFlags:UseUpstream:Chat=true"]'
reject_mutant 'extra task network' \
  '.TaskTemplate.Networks += [{"Target":"othernetwork"}]'
reject_mutant 'staging trusts a forwarded proxy' \
  '.TaskTemplate.ContainerSpec.Env += ["ForwardedHeaders__KnownProxies__0=10.0.0.2"]'
reject_mutant 'staging trusts a forwarded network' \
  '.TaskTemplate.ContainerSpec.Env += ["forwardedheaders__KNOWNNETWORKS__0=10.0.0.0/24"]'
reject_mutant 'staging trusts a colon-delimited forwarded proxy' \
  '.TaskTemplate.ContainerSpec.Env += ["ForwardedHeaders:KnownProxies:0=10.0.0.2"]'
reject_mutant 'staging trusts a colon-delimited forwarded network' \
  '.TaskTemplate.ContainerSpec.Env += ["ForwardedHeaders:KnownNetworks:0=10.0.0.0/24"]'
# Owner ruling 2026-08-27: Super Login IS open on staging. The mutant keeps
# its MECHANISM — any drift from the pinned Spec must be rejected — and only
# flips polarity to match the new pinned value. Deleting it would have removed
# drift detection on this env slot entirely.
reject_mutant 'Super Login closed' \
  '(.TaskTemplate.ContainerSpec.Env[10]) = "SuperLogin__OpenMode=false"'
reject_mutant 'demo roster closed' \
  '(.TaskTemplate.ContainerSpec.Env[11]) = "DemoUsers__Enabled=false"'
reject_mutant 'Dev Tool endpoints closed' \
  '(.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=false"'
reject_mutant 'Swagger closed' \
  '(.TaskTemplate.ContainerSpec.Env[13]) = "Features__Swagger__Enabled=false"'
reject_mutant 'mixed-case unified payment gateway key' \
  '.TaskTemplate.ContainerSpec.Env += ["uNiFiEdPaYmEnTgAtEwAy__BaSeUrL=http://gateway.invalid"]'
reject_mutant 'mixed-case unified payment gateway destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://UNIFIED-PAYMENT_GATEWAY:8080"]'
reject_mutant 'exact legacy unified-payment service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__UnifiedPayment__BaseUrl=http://gateway.invalid"]'
reject_mutant 'mixed-case payment-gateway service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__PaYmEnTgAtEwAy__BaseUrl=http://gateway.invalid"]'
reject_mutant 'mixed-case payment-gateway destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://PaYmEnT-GaTeWaY:8080"]'
reject_mutant 'token-boundary UPG service key' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__uPg__BaseUrl=http://gateway.invalid"]'
reject_mutant 'token-boundary mixed-case UPG destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=HTTP://UpG.Internal:8080"]'
reject_mutant 'forbidden .50 destination' \
  '.TaskTemplate.ContainerSpec.Env += ["Services__Legacy__BaseUrl=http://192.168.2." + "50:10026"]'
reject_mutant 'mixed-case inline JWT signing key' \
  '.TaskTemplate.ContainerSpec.Env += ["jWt__sIgNiNgKeY=not-a-mounted-secret"]'
reject_mutant 'mixed-case inline password' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__PaSsWoRd=not-a-mounted-secret"]'
reject_mutant 'mixed-case connection-string password' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__ConnectionString=Host=db;PASSWORD=not-a-mounted-secret"]'
reject_mutant 'URL-embedded credential' \
  '.TaskTemplate.ContainerSpec.Env += ["Database__ConnectionString=postgres://user:password@db/jeeb"]'

jq '
  .TaskTemplate.ContainerSpec.Image = "repo@sha256:cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
  | (.TaskTemplate.ContainerSpec.Env[3]) = "FeatureFlags__UseUpstream__Chat=true"
  | (.TaskTemplate.ContainerSpec.Env[4]) = "FeatureFlags__UseUpstream__Realtime=true"
  | (.TaskTemplate.ContainerSpec.Env[16]) = "Features__RealtimeWebSocketProxy__Enabled=true"
  | (.TaskTemplate.ContainerSpec.Env[10]) = "SuperLogin__OpenMode=false"
  | (.TaskTemplate.ContainerSpec.Env[11]) = "DemoUsers__Enabled=false"
  | (.TaskTemplate.ContainerSpec.Env[12]) = "Features__DevEndpoints__Enabled=false"
  | del(.TaskTemplate.ContainerSpec.Env[13])
  | .TaskTemplate.ContainerSpec.Env += [
      "Security__TokenMint__Enabled=false",
      "ServiceAuth__Enabled=false",
      "ServiceAuth__Caller=incumbent-caller"
    ]
  | .TaskTemplate.ContainerSpec.Secrets = [{
      SecretID:"incumbent-secret",SecretName:"incumbent-service-auth",
      File:{Name:"jeeb_gateway_service_auth",UID:"65532",GID:"65532",Mode:256}
    }]
  | .TaskTemplate.Resources = {Limits:{NanoCPUs:123,MemoryBytes:456}}
  | .TaskTemplate.Placement = {Constraints:["node.role==worker"]}
  | .Labels = {"jeeb.contract":"preserve-me"}
  | .UpdateConfig = {Parallelism:3,Monitor:1,FailureAction:"pause",Order:"stop-first",MaxFailureRatio:0.25}
  | .RollbackConfig = {Parallelism:2,Monitor:2,FailureAction:"continue",Order:"stop-first",MaxFailureRatio:0.5}
' "$candidate" > "$devtool_incumbent"
jq --arg image "$image" --slurpfile firebase_secret "$firebase_secret" '
  def env_key: (split("=")[0] | ascii_downcase | gsub(":"; "__"));
  def target: env_key as $key | [
    "superlogin__openmode","demousers__enabled",
    "features__devendpoints__enabled","features__swagger__enabled",
    "jeebfirebasecontract__schemaversion","jeebfirebasecontract__projectid",
    "jeebfirebasecontract__projectnumber","jeebfirebasecontract__firestoredatabaseid",
    "jeebfirebasecontract__chatenabled","jeebfirebasecontract__pushproducer",
    "firebase__chat__projectid","firebase__chat__serviceaccountkeypath",
    "featureflags__notificationdurablewrite__enabled",
    "featureflags__notificationoutboxmode","featureflags__pushdispatchmode",
    "firestore__databaseid","firebase__firestoredatabaseid",
    "firebase__chat__firestoredatabaseid"
  ] | index($key) != null;
  .TaskTemplate.ContainerSpec.Image = $image
  | .TaskTemplate.ContainerSpec.Env = (
      (.TaskTemplate.ContainerSpec.Env | map(select(target | not))) + [
        "SuperLogin__OpenMode=true","DemoUsers__Enabled=true",
        "Features__DevEndpoints__Enabled=true","Features__Swagger__Enabled=true",
        "JeebFirebaseContract__SchemaVersion=1",
        "JeebFirebaseContract__ProjectId=jeeb-5a293",
        "JeebFirebaseContract__ProjectNumber=1051234312170",
        "JeebFirebaseContract__FirestoreDatabaseId=(default)",
        "JeebFirebaseContract__ChatEnabled=true",
        "JeebFirebaseContract__PushProducer=notification-service",
        "Firebase__Chat__ProjectId=jeeb-5a293",
        "Firebase__Chat__ServiceAccountKeyPath=/run/secrets/firebase_admin_json",
        "FeatureFlags__NotificationDurableWrite__Enabled=true",
        "FeatureFlags__NotificationOutboxMode=upstream-authority",
        "FeatureFlags__PushDispatchMode=local"
      ])
  | .TaskTemplate.ContainerSpec.Secrets = (
      (.TaskTemplate.ContainerSpec.Secrets
        | map(select(.File.Name != "firebase_admin_json")))
      + $firebase_secret[0]
    )
  | .UpdateConfig += {Parallelism:1,Monitor:20000000000,FailureAction:"pause",Order:"start-first"}
  | .RollbackConfig += {Parallelism:1,Monitor:20000000000,FailureAction:"pause",Order:"start-first"}
' "$devtool_incumbent" > "$devtool_candidate"
validate "$devtool_candidate" devtool-reassert "$devtool_incumbent"
jq -e --arg image "$image" --slurpfile incumbent "$devtool_incumbent" \
  --slurpfile firebase_secret "$firebase_secret" \
  -f "$repository_root/scripts/staging-gateway-devtool-reassert-candidate.jq" \
  "$devtool_candidate" >/dev/null

jq '.TaskTemplate.ContainerSpec.Env += ["ForwardedHeaders__KnownProxies__0=10.0.0.2"]' \
  "$devtool_incumbent" > "$test_root/devtool-unsafe-incumbent.json"
jq '.TaskTemplate.ContainerSpec.Env += ["ForwardedHeaders__KnownProxies__0=10.0.0.2"]' \
  "$devtool_candidate" > "$mutant"
if validate "$mutant" devtool-reassert "$test_root/devtool-unsafe-incumbent.json"; then
  echo 'devtool-reassert accepted preserved forwarded-proxy trust' >&2
  exit 1
fi

for unsafe_filter in \
  '.TaskTemplate.ContainerSpec.Env += ["Unrelated__Drift=true"]' \
  '(.TaskTemplate.ContainerSpec.Env[] | select(startswith("FeatureFlags__UseUpstream__Chat="))) = "FeatureFlags__UseUpstream__Chat=false"' \
  '(.TaskTemplate.ContainerSpec.Env[] | select(startswith("FeatureFlags__UseUpstream__Realtime="))) = "FeatureFlags__UseUpstream__Realtime=false"' \
  '(.TaskTemplate.ContainerSpec.Env[] | select(startswith("Features__RealtimeWebSocketProxy__Enabled="))) = "Features__RealtimeWebSocketProxy__Enabled=false"' \
  '(.TaskTemplate.ContainerSpec.Env[] | select(startswith("Security__TokenMint__Enabled="))) = "Security__TokenMint__Enabled=true"' \
  '(.TaskTemplate.ContainerSpec.Env[] | select(startswith("ServiceAuth__Enabled="))) = "ServiceAuth__Enabled=true"' \
  '.TaskTemplate.ContainerSpec.Secrets[0].SecretName = "rotated-service-auth"' \
  '.TaskTemplate.Networks += [{Target:"othernetwork"}]' \
  '.TaskTemplate.Resources.Limits.MemoryBytes = 789' \
  '.TaskTemplate.Placement.Constraints = []' \
  '.Labels["jeeb.contract"] = "drifted"' \
  '.EndpointSpec.Mode = "dnsrr"' \
  '.UpdateConfig.MaxFailureRatio = 0.75' \
  '.RollbackConfig.MaxFailureRatio = 0.75'; do
  jq "$unsafe_filter" "$devtool_candidate" > "$mutant"
  if jq -e --arg image "$image" --slurpfile incumbent "$devtool_incumbent" \
    --slurpfile firebase_secret "$firebase_secret" \
    -f "$repository_root/scripts/staging-gateway-devtool-reassert-candidate.jq" \
    "$mutant" >/dev/null; then
    echo "devtool-reassert accepted unrelated incumbent drift: $unsafe_filter" >&2
    exit 1
  fi
done

jq '.TaskTemplate.ContainerSpec.Env += ["SUPERLOGIN__OpenMode=true"]' \
  "$devtool_incumbent" > "$mutant"
if jq -e --arg image "$image" --slurpfile incumbent "$mutant" \
  --slurpfile firebase_secret "$firebase_secret" \
  -f "$repository_root/scripts/staging-gateway-devtool-reassert-candidate.jq" \
  "$devtool_candidate" >/dev/null; then
  echo 'devtool-reassert accepted duplicate target rows in the incumbent' >&2
  exit 1
fi

echo 'staging gateway candidate semantic contract tests: PASS (mode flags preserved; exact Dev Tool delta with 19 negative controls)'
