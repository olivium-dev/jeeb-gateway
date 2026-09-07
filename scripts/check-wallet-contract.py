#!/usr/bin/env python3
"""Reject empty or incomplete pinned wallet contracts before client generation."""

import json
import sys
from pathlib import Path

REQUIRED = {
    "/Wallet/holder/ensure": "put",
    "/Wallet/holder/{holderId}/wallets": "get",
    "/Transaction/initiate": "post",
    "/Transaction/validate": "post",
    "/Transaction/by-external-reference/{externalReference}": "get",
    "/Transaction/{transactionHeaderId}/abort": "post",
    "/Transaction/{transactionHeaderId}/execute": "post",
    "/Fees/currencies": "get",
}


def validate(document):
    paths = document.get("paths")
    if not isinstance(paths, dict) or len(paths) < 28:
        raise ValueError("Wallet contract must retain at least the reviewed 28 paths")
    for path, method in REQUIRED.items():
        operation = paths.get(path, {}).get(method)
        if not isinstance(operation, dict) or not operation.get("responses"):
            raise ValueError(f"Required wallet operation missing: {method} {path}")
    policy = document["components"]["schemas"]["TransactionRequest"]["properties"]["applyConfiguredFees"]
    if policy.get("type") != "boolean" or policy.get("default") is not True:
        raise ValueError("Existing fee-application default must remain true")


if __name__ == "__main__":
    path = Path(sys.argv[1]) if len(sys.argv) == 2 else Path(__file__).resolve().parents[1] / "src/JeebGateway/contracts/wallet-service.openapi.json"
    try:
        validate(json.loads(path.read_text(encoding="utf-8")))
    except (OSError, ValueError, KeyError, TypeError, AttributeError) as error:
        print(f"Wallet contract rejected: {error}", file=sys.stderr)
        sys.exit(1)
    print("Wallet contract retains reviewed operations and fee default")
