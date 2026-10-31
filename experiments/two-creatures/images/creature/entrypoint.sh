#!/bin/sh
# A creature's start. /setup (from the runner, read-only) holds id_ed25519 (its git key) and creature.env:
#   NAME=a  PEER=b  CHANNEL=L|LM|M  WORLD=world
# The machine gets its own machine-id, which is what its vessel id is derived from (hashed, never stored raw).
set -eu
[ -s /etc/machine-id ] || cat /proc/sys/kernel/random/uuid | tr -d - > /etc/machine-id

home=/home/creature
mkdir -p "$home/.ssh"
cp /setup/id_ed25519 "$home/.ssh/id_ed25519"
cat > "$home/.ssh/config" <<EOF
Host world
  User git
  IdentityFile ~/.ssh/id_ed25519
  StrictHostKeyChecking accept-new
EOF
cp /setup/creature.env "$home/.creature.env"
chown -R creature:creature "$home"
chmod 700 "$home/.ssh" && chmod 600 "$home/.ssh/id_ed25519"

echo "creature: ready"
touch /run/creature-ready
exec sleep infinity
