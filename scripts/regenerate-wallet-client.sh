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
