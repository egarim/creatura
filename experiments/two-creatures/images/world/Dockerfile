# world: the sandbox's outside. A Docker host for the published app (Docker in Docker, so the app's containers run
# inside world exactly as they would on a world VM), a git server with push-to-deploy, the message board, and the
# Creatura ledger hub. Nothing on it belongs to either creature.
FROM docker:27-dind

RUN apk add --no-cache --upgrade openssh-server openssh-client git nodejs bash tar coreutils icu-libs \
 && adduser -D -h /home/git -s /bin/sh git \
 && sed -i 's/^git:!/git:*/' /etc/shadow \
 && mkdir -p /srv/git /srv/deploy/queue /srv/deploy/work /srv/deploy/logs /srv/board /srv/hub /opt/hub /opt/world \
 && chown -R git:git /srv/git /srv/deploy/queue

COPY sshd_config /etc/ssh/sshd_config
COPY git-acl.sh post-receive entrypoint.sh /opt/world/
COPY board.mjs deployer.mjs /opt/world/
COPY hub/ /opt/hub/
RUN chmod +x /opt/world/*.sh /opt/world/post-receive /opt/hub/creatura-hub

EXPOSE 22 5181 7000 7001 8080
ENTRYPOINT ["/opt/world/entrypoint.sh"]
