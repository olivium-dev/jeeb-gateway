#!/usr/bin/env bash
set -euo pipefail

repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
spec="$repo_root/src/JeebGateway/contracts/wallet-service.openapi.json"
output="${1:-$repo_root/src/JeebGateway/Services/ServiceWalletClient.cs}"
nswag_bin="${NSWAG_BIN:-nswag}"

"$nswag_bin" openapi2csclient \
  "/input:$spec" \
  /classname:ServiceWalletClient \
  /namespace:JeebGateway.service.ServiceWallet \
  "/output:$output" \
  /injectHttpClient:true \
  /useHttpClientCreationMethod:false \
  /jsonLibrary:NewtonsoftJson

# NSwag generates a lone `status_ == 200` branch when an operation documents
# only 200. Jeeb upstream clients deliberately accept the complete successful
# HTTP range; otherwise a provider changing a successful write to 201/202/204
# is surfaced as a gateway failure. Keep this deterministic post-generation
# transform beside the authoritative generator rather than hand-editing the
# generated client.
perl -0pi -e 's/if \(status_ == 200\)/if (status_ >= 200 \&\& status_ < 300)/g' "$output"
