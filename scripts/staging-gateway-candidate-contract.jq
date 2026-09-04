def env_pair: capture("^(?<key>[^=]+)=(?<value>.*)$");
def normalized_env_key: ascii_downcase | gsub(":"; "__");
def canonical_configuration_key: ascii_downcase | gsub("__"; ":");
def forwarded_trust_key:
  canonical_configuration_key
  | (. == "forwardedheaders:knownproxies"
    or startswith("forwardedheaders:knownproxies:")
    or . == "forwardedheaders:knownnetworks"
    or startswith("forwardedheaders:knownnetworks:"));
def setting_rows($document; $key):
  [
    ($document.TaskTemplate.ContainerSpec.Env // [])[]
    | env_pair
    | select((.key | normalized_env_key) == ($key | normalized_env_key))
    | {key:.key, value:.value}
  ];
def banned_legacy_host: "192.168.2." + "50";
def canonical_identifier: ascii_downcase | gsub("[^a-z0-9]"; "");
def raw_secret_config_key:
  canonical_identifier as $key
  | if (($key | endswith("file")) or ($key | endswith("path"))) then
      false
    else
      [
        "password",
        "secret",
        "token",
        "signingkey",
        "privatekey",
        "credential",
        "credentials",
        "apikey",
        "jwtkey"
      ]
      | any(. as $suffix | $key | endswith($suffix))
    end;
def embeds_inline_credential:
  ascii_downcase
  | (test("(^|[;?&])\\s*(password|pwd|user ?id|username|uid)\\s*=")
    or test("^[a-z][a-z0-9+.-]*://[^/@[:space:]]+:[^/@[:space:]]+@"));
def forbidden_payment_gateway_reference:
  ascii_downcase
  | test(
      "(^|[^a-z0-9])((unified[-_. ]*payment([-_. ]*gateway)?)|(payment[-_. ]*gateway)|upg)([^a-z0-9]|$)"
    );

# Every environment expectation lives HERE, once. The boolean gate and the --arg explain
# diagnostic are both derived from it, so a value cannot be pinned in one and not the other.
def expected_environment:
  (if $deployment_mode == "devtool-reassert" then
    # The exact incumbent-delta contract separately proves that all unrelated
    # values — including Chat, Realtime, WSS, OTP and ServiceAuth — are preserved.
    {
      "superlogin__openmode": "true",
      "demousers__enabled": "true",
      "features__devendpoints__enabled": "true",
      "features__swagger__enabled": "true"
    }
  else
    {
      "services__serviceotp__baseurl": "http://jeeb-staging-one-time-password:8080",
      "serviceotpapi__baseurl": "http://jeeb-staging-one-time-password:8080",
      "featureflags__useupstream__otp": "true",
      # Resolved per deploy, never pinned: a literal here rejected every activated
      # candidate and killed the deploy silently (run 33821087895).
      "featureflags__useupstream__chat": $chat_upstream_enabled,
      "featureflags__useupstream__realtime": "false",
      "features__realtimewebsocketproxy__enabled": "false",
      "auth__otp__applicationid": "0d51afe1-499f-4a29-a55a-36d2dd223b05",
      "auth__otp__phone__allowedregion": "LB",
      "auth__otp__phone__enforceregion": "false",
      "services__realtime__baseurl": "http://jeeb-staging-realtime-comunication-service:4000",
      "featureflags__useupstream__voice": "false",
      "superlogin__openmode": "true",
      "demousers__enabled": "true"
    }
    + (if $deployment_mode == "otp-cutover" then
        {
          "serviceauth__enabled": "true",
          "serviceauth__caller": "jeeb-gateway",
          "serviceauth__signingkeyfile": "/run/secrets/jeeb_gateway_service_auth"
        }
      else {} end)
  end)
  + {
    "jeebfirebasecontract__schemaversion": "1",
    "jeebfirebasecontract__projectid": "jeeb-5a293",
    "jeebfirebasecontract__projectnumber": "1051234312170",
    "jeebfirebasecontract__firestoredatabaseid": "(default)",
    "jeebfirebasecontract__chatenabled": "true",
    "jeebfirebasecontract__pushproducer": "notification-service",
    "firebase__chat__projectid": "jeeb-5a293",
    "firebase__chat__serviceaccountkeypath": "/run/secrets/firebase_admin_json",
    "featureflags__notificationdurablewrite__enabled": "true",
    "featureflags__notificationoutboxmode": "upstream-authority",
    "featureflags__pushdispatchmode": "local"
  };
def environment_mismatches($environment):
  expected_environment
  | to_entries
  | map(select(.value != $environment[.key])
        | "\(.key): expected \"\(.value)\", candidate has \"\($environment[.key] // "<unset>")\"");

(.TaskTemplate.ContainerSpec.Env // []) as $rows
| [$rows[] | env_pair] as $pairs
| ($pairs | map(.key | normalized_env_key)) as $normalized_keys
| ($pairs
    | map({key:(.key | normalized_env_key), value:.value})
    | from_entries) as $environment
| .EndpointSpec.Ports as $ports
| .TaskTemplate.Networks as $networks
| (tojson | ascii_downcase) as $candidate_text
# A malformed resolved state must fail loudly, never silently neuter the chat clause.
| (if ($chat_upstream_enabled | IN("true", "false")) then . else
     error("chat_upstream_enabled must be exactly true or false, got \"" + $chat_upstream_enabled + "\"")
   end)
| if $explain == "true" then
    environment_mismatches($environment)
    | if length == 0 then ["<no environment mismatch: a structural clause failed (image, network, ports, update/rollback policy, secrets, incumbent delta, or a banned-value scan)>"] else . end
    | .[]
  else
  ($normalized_keys | length) == ($normalized_keys | unique | length)
  and .TaskTemplate.ContainerSpec.Image == $image
  and ($networks == [{Target:$network_id}])
  and ($ports | length) == 1
  and $ports[0].PublishedPort == $published
  and $ports[0].TargetPort == 8080
  and $ports[0].PublishMode == "ingress"
  and .EndpointSpec.Mode == "vip"
  and .Mode.Replicated.Replicas == 1
  and (
    if ($deployment_mode == "normal"
      or $deployment_mode == "security-cutover"
      or $deployment_mode == "otp-cutover"
      or $deployment_mode == "devtool-reassert") then
      .UpdateConfig.Parallelism == 1
      and .UpdateConfig.Monitor == 20000000000
      and .UpdateConfig.Order == "start-first"
      and .UpdateConfig.FailureAction == "pause"
      and .RollbackConfig.Order == "start-first"
      and .RollbackConfig.FailureAction == "pause"
    else
      false
    end
  )
  and (environment_mismatches($environment) | length) == 0
  and (
    if $deployment_mode == "otp-cutover" then
      any(
        .TaskTemplate.ContainerSpec.Secrets[]?;
        .File.Name == "jeeb_gateway_service_auth"
        and .File.Mode == 256
      )
    else
      true
    end
  )
  and ([.TaskTemplate.ContainerSpec.Secrets[]?
    | select(.File.Name == "firebase_admin_json")] | length) == 1
  and any(
    .TaskTemplate.ContainerSpec.Secrets[]?;
    .File.Name == "firebase_admin_json"
    and .File.UID == "65532"
    and .File.GID == "65532"
    and .File.Mode == 256
    and (.SecretID | test("^[a-z0-9]+$"))
    and (.SecretName | test("^jeeb_staging_fb_[a-zA-Z0-9_-]{43}$"))
  )
  and $environment["featureflags__notificationdurablewrite__enabled"] == "true"
  and $environment["featureflags__notificationoutboxmode"] == "upstream-authority"
  and $environment["featureflags__pushdispatchmode"] == "local"
  and ($normalized_keys | index("firestore__databaseid")) == null
  and ($normalized_keys | index("firebase__firestoredatabaseid")) == null
  and ($normalized_keys | index("firebase__chat__firestoredatabaseid")) == null
  and ($pairs | all((.key | forwarded_trust_key) | not))
  and (
    if $deployment_mode == "devtool-reassert" then
      true
    else
      ($incumbent | length) == 1
      and setting_rows(.; "Features__DevEndpoints__Enabled")
        == setting_rows($incumbent[0]; "Features__DevEndpoints__Enabled")
      and setting_rows(.; "Features__Swagger__Enabled")
        == setting_rows($incumbent[0]; "Features__Swagger__Enabled")
    end
  )
  and ($candidate_text | contains(banned_legacy_host) | not)
  and ($pairs | all(
    . as $pair
    | ((($pair.key | forbidden_payment_gateway_reference)
      or ($pair.value | forbidden_payment_gateway_reference))
      | not)))
  and ($pairs | all((.key | raw_secret_config_key) | not))
  and ($pairs | all((.value | embeds_inline_credential) | not))
  and ($pairs | all(
    (.value | ascii_downcase) as $value
    | ((($value | contains("192.168.2.20:10037"))
      or ($value | contains("192.168.2.20:10069")))
      | not)))
  end
