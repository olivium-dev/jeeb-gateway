# Sidecar that runs pg_backup.sh on a 6h cron.
# Image stays small: alpine + postgresql-client + busybox crond + curl.
FROM postgres:16-alpine

RUN apk add --no-cache bash curl tini

WORKDIR /opt/jeeb-backup

COPY pg_backup.sh   /opt/jeeb-backup/pg_backup.sh
COPY pg_restore.sh  /opt/jeeb-backup/pg_restore.sh
COPY verify_backup.sh /opt/jeeb-backup/verify_backup.sh
COPY cron/crontab   /etc/crontabs/root
COPY cron/entrypoint.sh /opt/jeeb-backup/entrypoint.sh

RUN chmod +x /opt/jeeb-backup/*.sh

ENTRYPOINT ["/sbin/tini", "--", "/opt/jeeb-backup/entrypoint.sh"]
