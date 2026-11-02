#!/bin/sh
# The forced command of every key in git's authorized_keys: $1 is who holds the key (a, b or runner).
# Everyone may fetch both repositories; a creature may push only to its own; the runner, and a creature working
# alone (solo), to both.
who=$1
cmd=$SSH_ORIGINAL_COMMAND
verb=${cmd%% *}
repo=$(printf '%s' "${cmd#* }" | tr -d "'\"" | sed 's#^/##; s#^srv/git/##; s#\.git$##')
case "$repo" in
  repo-a|repo-b) ;;
  *) echo "no such repository: $repo (there are repo-a and repo-b)" >&2; exit 1 ;;
esac
case "$verb" in
  git-upload-pack|git-upload-archive) ;;
  git-receive-pack)
    if [ "$who" != runner ] && [ "$who" != solo ] && [ "$repo" != "repo-$who" ]; then
      echo "$repo is read-only for you: you push only to repo-$who" >&2
      exit 1
    fi ;;
  *) echo "only git fetch and git push here" >&2; exit 1 ;;
esac
exec "$verb" "/srv/git/$repo.git"
