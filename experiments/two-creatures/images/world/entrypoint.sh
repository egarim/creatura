#!/bin/sh
# world's start: the Docker daemon for the published app, then git over ssh, the deployer, and, when the arm has
# them, the message board and the ledger hub. /srv/setup (from the runner, read-only) holds:
#   keys/{a,b,solo,runner}.pub              who may use git (solo: a creature working alone, both repositories)
#   owners.txt                              the experiment owner's public key, for the hub's --trust
#   world.env                               BOARD=on|off  HUB=on|off  WORLD_IP=<this machine's sandbox address>
# /srv/preload/*.tar (optional) are images loaded into the inner daemon, so the app's builds need not pull them.
set -eu
. /srv/setup/world.env

dockerd-entrypoint.sh dockerd --host=unix:///var/run/docker.sock >/var/log/dockerd.log 2>&1 &
i=0
until docker info >/dev/null 2>&1; do
  i=$((i + 1)); [ "$i" -gt 120 ] && { echo "world: dockerd did not start" >&2; tail -20 /var/log/dockerd.log >&2; exit 1; }
  sleep 0.5
done
for image in /srv/preload/*.tar; do
  if [ -f "$image" ]; then docker load -q -i "$image" >/dev/null; fi
done
docker network inspect pub >/dev/null 2>&1 || docker network create pub >/dev/null

# git: one bare repository per creature, a push-to-deploy hook, and keys bound to who holds them.
for repo in repo-a repo-b; do
  if [ ! -d "/srv/git/$repo.git" ]; then
    git init -q --bare -b main "/srv/git/$repo.git"
    cp /opt/world/post-receive "/srv/git/$repo.git/hooks/post-receive"
  fi
done
chown -R git:git /srv/git
mkdir -p /home/git/.ssh
: > /home/git/.ssh/authorized_keys
for who in a b solo runner; do
  key="/srv/setup/keys/$who.pub"
  if [ -f "$key" ]; then
    printf 'command="/opt/world/git-acl.sh %s",no-pty,no-port-forwarding,no-agent-forwarding,no-X11-forwarding %s\n' "$who" "$(cat "$key")" >> /home/git/.ssh/authorized_keys
  fi
done
chown -R git:git /home/git/.ssh && chmod 700 /home/git/.ssh && chmod 600 /home/git/.ssh/authorized_keys
[ -f /etc/ssh/ssh_host_ed25519_key ] || ssh-keygen -q -t ed25519 -N '' -f /etc/ssh/ssh_host_ed25519_key
/usr/sbin/sshd -E /var/log/sshd.log

node /opt/world/deployer.mjs >/var/log/deployer.log 2>&1 &
if [ "$BOARD" = on ]; then node /opt/world/board.mjs >/var/log/board.log 2>&1 & fi
if [ "$HUB" = on ]; then /opt/hub/creatura-hub --listen "$WORLD_IP:5181" --trust /srv/setup/owners.txt --data /srv/hub >/var/log/hub.log 2>&1 & fi

echo "world: ready (board $BOARD, hub $HUB)"
touch /run/world-ready
wait
