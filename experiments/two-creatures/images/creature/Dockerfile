# A creature's body: a Linux machine with a shell, git, Node, the creatura CLI, and the channel tools of its arm.
# The agent runs as the user `creature`, which may install what it needs (sudo without a password).
FROM node:22-bookworm-slim

RUN apt-get update \
 && apt-get install -y --no-install-recommends git openssh-client curl ca-certificates jq sudo procps libicu72 \
 && rm -rf /var/lib/apt/lists/* \
 && useradd -m -s /bin/bash creature \
 && echo 'creature ALL=(ALL) NOPASSWD:ALL' > /etc/sudoers.d/creature

COPY creatura/ /opt/creatura/
COPY tools/ /usr/local/bin/
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /opt/creatura/creatura /usr/local/bin/say /usr/local/bin/listen /usr/local/bin/cell /entrypoint.sh \
 && ln -s /opt/creatura/creatura /usr/local/bin/creatura

ENTRYPOINT ["/entrypoint.sh"]
