#!/usr/bin/env bash
# Build the three images for the Docker host the runner talks to: tc-world, tc-creature, tc-judge.
# The creatura CLI (glibc, for the creatures) and the ledger hub (musl, for world's Alpine) are published from this
# repository for that host's architecture, so the experiment runs the cells code of the checkout it is in.
# usage: experiments/two-creatures/build.sh
set -euo pipefail
here=$(cd "$(dirname "$0")" && pwd)
root=$(cd "$here/../.." && pwd)
out="$here/build"

case "$(docker info --format '{{.Architecture}}')" in
  aarch64|arm64) arch=arm64 ;;
  x86_64|amd64) arch=x64 ;;
  *) echo "build: unknown Docker host architecture" >&2; exit 1 ;;
esac

publish() { # project rid destination
  dotnet publish "$root/$1" -c Release -r "$2" --self-contained true -p:PublishSingleFile=true \
    -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none -o "$3" --nologo -v quiet
}

rm -rf "$out" && mkdir -p "$out"
echo "build: creatura CLI for linux-$arch"
publish src/Creatura.Cli/Creatura.Cli.csproj "linux-$arch" "$out/creatura"
mv "$out/creatura/Creatura.Cli" "$out/creatura/creatura"
echo "build: ledger hub for linux-musl-$arch"
publish src/Creatura.Cells.Hub/Creatura.Cells.Hub.csproj "linux-musl-$arch" "$out/hub"
mv "$out/hub/Creatura.Cells.Hub" "$out/hub/creatura-hub"

stage() { # image dir, with extra build inputs copied in
  rm -rf "$out/ctx-$1" && cp -R "$here/images/$1" "$out/ctx-$1"
}
stage world && cp -R "$out/hub" "$out/ctx-world/hub"
stage creature && cp -R "$out/creatura" "$out/ctx-creature/creatura"
stage judge

for image in world creature judge; do
  echo "build: tc-$image"
  docker build -q -t "tc-$image" "$out/ctx-$image" >/dev/null || { echo "build: tc-$image failed; rerun: docker build $out/ctx-$image" >&2; exit 1; }
done
echo "build: done (tc-world, tc-creature, tc-judge for $arch)"
