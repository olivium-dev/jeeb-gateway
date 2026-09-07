#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 || ! "$1" =~ ^[0-9a-fA-F]{40}$ || "$1" == 0000000000000000000000000000000000000000 ]]; then
  echo "An exact, available base commit is required; missing history is not a pass." >&2
  exit 64
fi
base=$1
repo_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repo_root"
git cat-file -e "$base^{commit}"
python3 -B scripts/check-wallet-contract.py
for spec in artifacts/openapi/jeeb-gateway.v1.json src/JeebGateway/contracts/wallet-service.openapi.json; do
  git cat-file -e "$base:$spec"
  bash scripts/check-openapi-path-method-compatibility.sh <(git show "$base:$spec") "$spec"
done
