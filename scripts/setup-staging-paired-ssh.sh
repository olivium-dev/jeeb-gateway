#!/usr/bin/env bash
# Same pinned strict SSH bootstrap as the reviewed delivery preparation route.
set -euo pipefail
: "${SSH_HOST:?}" "${SSH_USER:?}" "${SSH_PRIVATE_KEY:?}" "${SSH_KNOWN_HOSTS:?}"
[ "$SSH_HOST" = jeeb-staging-ssh.fds-1.com ]
[[ "$SSH_USER" =~ ^[a-z_][a-z0-9_-]*$ ]]
curl -fsSL --retry 3 -o /tmp/cloudflared \
  https://github.com/cloudflare/cloudflared/releases/download/2026.8.2/cloudflared-linux-amd64
printf '%s  %s\n' fcfb02b575a52ca1af2e3267af4e1517bcdeb30ac48c834c69abaed3c0576ad2 /tmp/cloudflared \
  | sha256sum --check --strict
install -d "$HOME/.local/bin"
install -m 0755 /tmp/cloudflared "$HOME/.local/bin/cloudflared"
echo "$HOME/.local/bin" >> "$GITHUB_PATH"
install -d -m 700 "$HOME/.ssh"
if printf '%s' "$SSH_PRIVATE_KEY" | grep -q '^-----BEGIN'; then
  printf '%s\n' "$SSH_PRIVATE_KEY" > "$HOME/.ssh/id_ed25519"
else
  printf '%s' "$SSH_PRIVATE_KEY" | base64 --decode > "$HOME/.ssh/id_ed25519"
fi
chmod 600 "$HOME/.ssh/id_ed25519"
printf '%s\n' "$SSH_KNOWN_HOSTS" > "$HOME/.ssh/known_hosts"
chmod 600 "$HOME/.ssh/known_hosts"
ssh-keygen -F "$SSH_HOST" -f "$HOME/.ssh/known_hosts" >/dev/null
{
  echo 'Host jeeb-staging'
  echo "  HostName $SSH_HOST"
  echo "  User $SSH_USER"
  echo '  IdentityFile ~/.ssh/id_ed25519'
  echo '  IdentitiesOnly yes'
  echo '  ProxyCommand cloudflared access ssh --hostname %h'
  echo '  StrictHostKeyChecking yes'
  echo '  BatchMode yes'
  echo '  ConnectTimeout 15'
  echo '  ServerAliveInterval 15'
  echo '  ServerAliveCountMax 2'
  echo '  UserKnownHostsFile ~/.ssh/known_hosts'
} > "$HOME/.ssh/config"
chmod 600 "$HOME/.ssh/config"
