#!/usr/bin/env python3
"""
GW1 test pack, W1.8 leg — the migration and the client must agree, COLUMN BY COLUMN.

The failure this exists to catch is silent by construction. `SettlementService`
treats the ledger post as best-effort and swallows the exception, so a column typo
(`42703 undefined_column`) or a missing table (`42P01`) produces:

    - no 500, no failed request, no alert;
    - `settlements.ledger_entry_id` stays NULL;
    - the 60 s `SettlementLedgerReconciler` retries it forever, quietly.

A DI-resolution test cannot see any of that: the type resolves fine, the SQL is a
string, and it is only ever parsed by Postgres. Nothing in the host suite ever
executes it (Testcontainers needs Docker, which is banned in this environment). So
the strongest available host-side evidence is a source-to-source contract check of
the two artefacts that must agree.

WHAT THIS DOES NOT PROVE — stated plainly, because a green here is easy to
over-read: it does not prove the table exists in any database, and it does not
prove a ledger entry survives a restart. Both are `service`-class claims that need
a live MSI gateway plus `systemctl restart` (GW1 V-2). See run-pack.sh's
NOT-PROVEN section.

Usage:  schema-contract.py [--verbose]
Exit:   0 = the two artefacts agree, 1 = they do not.
"""
from __future__ import annotations

import re
import sys

MIGRATION = "db/migrations/0044_init_settlement_ledger_entries.sql"
CLIENT = "src/JeebGateway/Financials/PostgresSettlementLedgerClient.cs"
TABLE = "settlement_ledger_entries"

failures: list[str] = []
notes: list[str] = []


def fail(msg: str) -> None:
    failures.append(msg)


def read(path: str) -> str:
    try:
        with open(path, "r", encoding="utf-8") as fh:
            return fh.read()
    except FileNotFoundError:
        fail(f"missing file: {path}")
        return ""


def strip_sql_comments(sql: str) -> str:
    return re.sub(r"^\s*--[^\n]*$", "", sql, flags=re.M)


def split_top_level(text: str, sep: str = ",") -> list[str]:
    """Split on `sep` only at paren-depth 0 — NUMERIC(20,4) must stay one token."""
    out, depth, cur = [], 0, []
    for ch in text:
        if ch == "(":
            depth += 1
        elif ch == ")":
            depth -= 1
        if ch == sep and depth == 0:
            out.append("".join(cur))
            cur = []
        else:
            cur.append(ch)
    out.append("".join(cur))
    return out


def parse_migration(sql: str):
    """-> (columns, pk_column, not_null_without_default, idempotent_create)"""
    sql = strip_sql_comments(sql)
    m = re.search(
        rf"CREATE\s+TABLE\s+(IF\s+NOT\s+EXISTS\s+)?{TABLE}\s*\((.*?)\n\s*\);",
        sql, re.S | re.I)
    if not m:
        fail(f"{MIGRATION}: no CREATE TABLE for {TABLE}")
        return [], None, [], False
    idempotent = m.group(1) is not None
    cols, pk, required = [], None, []
    for raw in split_top_level(m.group(2)):
        line = raw.strip()
        if not line:
            continue
        name = line.split()[0]
        if name.upper() in ("PRIMARY", "CONSTRAINT", "UNIQUE", "CHECK", "FOREIGN"):
            continue
        cols.append(name)
        upper = line.upper()
        if "PRIMARY KEY" in upper:
            pk = name
        if "NOT NULL" in upper and "DEFAULT" not in upper:
            required.append(name)
    return cols, pk, required, idempotent


def parse_client(cs: str):
    ins = re.search(r'insertSql\s*=\s*"""(.*?)"""', cs, re.S)
    sel = re.search(r'selectSql\s*=\s*"""(.*?)"""', cs, re.S)
    if not ins:
        fail(f"{CLIENT}: insertSql not found")
        return None
    if not sel:
        fail(f"{CLIENT}: selectSql not found")
        return None
    insert_sql, select_sql = ins.group(1), sel.group(1)

    cols_m = re.search(rf"INSERT\s+INTO\s+{TABLE}\s*\((.*?)\)\s*VALUES", insert_sql, re.S | re.I)
    vals_m = re.search(r"VALUES\s*\((.*?)\)\s*ON\s+CONFLICT", insert_sql, re.S | re.I)
    if not cols_m or not vals_m:
        fail(f"{CLIENT}: could not parse the INSERT column/VALUES lists")
        return None

    insert_cols = [c.strip() for c in split_top_level(cols_m.group(1)) if c.strip()]
    value_exprs = [v.strip() for v in split_top_level(vals_m.group(1)) if v.strip()]
    sql_params = [p[1:] for p in value_exprs if p.startswith("@")]
    literals = [p for p in value_exprs if not p.startswith("@")]

    # Every @param mentioned anywhere in either statement, and every parameter the
    # code actually binds. Compared as SETS: the idempotency key legitimately appears
    # in both statements, so a positional/multiset comparison would false-red.
    all_sql_params = set(re.findall(r"@(\w+)", insert_sql + select_sql))
    add_params = sorted(set(re.findall(r'AddWithValue\(\s*"([^"]+)"', cs)))
    conflict = re.search(r"ON\s+CONFLICT\s*\(\s*(\w+)\s*\)\s*DO\s+NOTHING", insert_sql, re.I)
    returning = re.search(r"RETURNING\s+([\w,\s]+)", insert_sql, re.I)
    sel_cols_m = re.search(r"SELECT\s+(.*?)\s+FROM", select_sql, re.S | re.I)
    sel_where_m = re.search(r"WHERE\s+(\w+)\s*=", select_sql, re.I)

    return {
        "insert_cols": insert_cols,
        "value_exprs": value_exprs,
        "sql_params": sql_params,
        "all_sql_params": sorted(all_sql_params),
        "literals": literals,
        "add_params": add_params,
        "conflict_col": conflict.group(1) if conflict else None,
        "returning": [c.strip() for c in returning.group(1).split(",")] if returning else [],
        "select_cols": [c.strip() for c in sel_cols_m.group(1).split(",")] if sel_cols_m else [],
        "select_where": sel_where_m.group(1) if sel_where_m else None,
        "select_targets_table": bool(re.search(rf"FROM\s+{TABLE}", select_sql, re.I)),
    }


def main() -> int:
    verbose = "--verbose" in sys.argv

    cols, pk, required, idempotent = parse_migration(read(MIGRATION))
    client = parse_client(read(CLIENT))
    if not cols or client is None:
        for f in failures:
            print(f"FAIL {f}")
        return 1

    notes.append(f"migration columns ({len(cols)}): {cols}")
    notes.append(f"migration PRIMARY KEY: {pk}")
    notes.append(f"INSERT columns ({len(client['insert_cols'])}): {client['insert_cols']}")
    notes.append(f"SQL @params ({len(client['sql_params'])}) / AddWithValue ({len(client['add_params'])})")

    # POSITIVE CONTROL — both parsers must have found real structure. A zero-length
    # parse would otherwise satisfy every subset check below vacuously.
    if len(cols) < 5:
        fail(f"positive control: only {len(cols)} columns parsed from {MIGRATION}")
    if len(client["insert_cols"]) < 5:
        fail(f"positive control: only {len(client['insert_cols'])} INSERT columns parsed from {CLIENT}")

    # 1. every column the client writes must exist in the table
    unknown = [c for c in client["insert_cols"] if c not in cols]
    if unknown:
        fail(f"INSERT writes column(s) absent from {MIGRATION}: {unknown} "
             f"(runtime 42703, swallowed as best-effort — ledger_entry_id stays NULL forever)")

    # 2. every NOT NULL column without a DEFAULT must be written
    missing = [c for c in required if c not in client["insert_cols"]]
    if missing:
        fail(f"NOT NULL column(s) never written by the INSERT: {missing} (runtime 23502)")

    # 3. the SQL placeholders and the Npgsql bindings must be the same set. An @param
    #    with no AddWithValue is a runtime 42P02; an AddWithValue with no @param is a
    #    value silently dropped on a money row.
    sql_set, code_set = set(client["all_sql_params"]), set(client["add_params"])
    if sql_set != code_set:
        fail(f"@param / AddWithValue mismatch: "
             f"in SQL only {sorted(sql_set - code_set)}, "
             f"in code only {sorted(code_set - sql_set)}")

    # 4. columns and value expressions must be positionally 1:1
    if len(client["insert_cols"]) != len(client["value_exprs"]):
        fail(f"INSERT arity mismatch: {len(client['insert_cols'])} columns vs "
             f"{len(client['value_exprs'])} value expressions")

    # 5. THE MONEY INVARIANT — the conflict target must be the table's PRIMARY KEY.
    #    This is what makes the replay return the ORIGINAL entry instead of minting a
    #    second ledger id for one hand-to-hand cash collection.
    if client["conflict_col"] is None:
        fail("INSERT has no `ON CONFLICT (<pk>) DO NOTHING` — a replay would mint a SECOND "
             "ledger entry id for one cash collection")
    elif client["conflict_col"] != pk:
        fail(f"ON CONFLICT targets '{client['conflict_col']}' but the PRIMARY KEY is '{pk}' "
             "— the conflict clause would not fire (runtime 42P10)")

    # 6. the read-back path must query the same table on the same key
    if not client["select_targets_table"]:
        fail(f"the conflict read-back SELECT does not target {TABLE}")
    if client["select_where"] != pk:
        fail(f"the conflict read-back filters on '{client['select_where']}', not the PK '{pk}' "
             "— a replay could read back the WRONG entry")
    bad_sel = [c for c in client["select_cols"] if c not in cols]
    if bad_sel:
        fail(f"read-back SELECT names column(s) absent from {MIGRATION}: {bad_sel}")

    # 7. RETURNING must yield what the client reads by ordinal (GetString(0)/GetFieldValue(1))
    if client["returning"] != ["ledger_entry_id", "posted_at"]:
        fail(f"RETURNING is {client['returning']}, but the client reads ordinal 0 as the "
             "ledger entry id and ordinal 1 as posted_at")
    if client["select_cols"] != client["returning"]:
        fail(f"the INSERT…RETURNING projection {client['returning']} and the read-back "
             f"projection {client['select_cols']} differ — the two paths would return "
             "different shapes by ordinal")

    # 8. the migration must be re-runnable and self-registering
    if not idempotent:
        fail(f"{MIGRATION}: CREATE TABLE is not `IF NOT EXISTS` — not safe to re-run")
    if "schema_migrations" not in read(MIGRATION):
        fail(f"{MIGRATION}: does not register itself in schema_migrations")

    if verbose or failures:
        for n in notes:
            print(f"  . {n}")
    for f in failures:
        print(f"FAIL {f}")
    if not failures:
        print(f"OK  {MIGRATION} <-> {CLIENT} agree: {len(cols)} columns, "
              f"PK '{pk}', ON CONFLICT ({client['conflict_col']}) DO NOTHING, "
              f"RETURNING {client['returning']}")
    return 1 if failures else 0


if __name__ == "__main__":
    sys.exit(main())
