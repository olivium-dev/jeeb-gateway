#!/usr/bin/env bash
# WAL archive_command for point-in-time recovery.
#
# Wired in postgresql.conf:
#   wal_level = replica
#   archive_mode = on
#   archive_command = '/etc/postgresql/pg_wal_archive.sh %p %f'
#
# Args:
#   $1 = %p = path to the WAL segment Postgres wants archived (absolute)
#   $2 = %f = filename only
#
# Contract: must exit 0 only after the segment is durably stored. Returning
# non-zero makes Postgres retry, so transient failures are safe.

set -euo pipefail

src="${1:?missing %p argument from postgres}"
name="${2:?missing %f argument from postgres}"

: "${WAL_ARCHIVE_DIR:=/var/backups/jeeb/wal}"

mkdir -p "${WAL_ARCHIVE_DIR}"

# Refuse to overwrite — duplicate segment with the same name means something is
# wrong (e.g. two primaries archiving to the same location).
if [[ -e "${WAL_ARCHIVE_DIR}/${name}" ]]; then
    echo "WAL segment ${name} already archived; refusing to overwrite" >&2
    exit 1
fi

tmp="${WAL_ARCHIVE_DIR}/.${name}.partial"
cp "${src}" "${tmp}"
sync "${tmp}" 2>/dev/null || true
mv "${tmp}" "${WAL_ARCHIVE_DIR}/${name}"
