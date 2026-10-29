#!/usr/bin/env bash
# Builds a release's files into one folder: each program as a self-contained single-file executable for each platform
# (no .NET needed to run it), one archive per program and platform, the NuGet packages and their symbols, and
# SHA256SUMS. The release workflow runs this; run it locally the same way to check a release before tagging it.
#
#   tools/release/build.sh <version> <out-dir> [rid ...]     e.g. tools/release/build.sh 0.2.0 artifacts osx-arm64
#
# macOS executables must be built on macOS: the SDK signs them ad hoc there, and an unsigned arm64 binary will not run.
# Single-file compression is left off on purpose: it halves the file on disk but not the download (the archive is
# compressed anyway), and it more than doubles start-up time, which `creatura cell hook` pays on every hook event.
set -euo pipefail

version="${1:?version, e.g. 0.2.0}"
out="$(mkdir -p "${2:?out dir}" && cd "$2" && pwd)"
shift 2
rids=("$@")
[ ${#rids[@]} -eq 0 ] && rids=(osx-arm64 osx-x64 linux-x64 linux-arm64 win-x64 win-arm64)

root="$(cd "$(dirname "$0")/../.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT

# name | project | what goes beside it | the README's lines
apps=(
  "creatura|src/Creatura.Cli/Creatura.Cli.csproj"
  "creatura-hub|src/Creatura.Cells.Hub/Creatura.Cells.Hub.csproj"
  "creatura-convoy|samples/Convoy/Convoy.csproj"
)

readme() {
  case "$1" in
    creatura) echo "  creatura cell init      create this vessel's cell (then: whoami, claim, ledger, ...)
  creatura cell           every command
  creatura cell hooks-config / install-hooks   join Claude Code" ;;
    creatura-hub) echo "  creatura-hub --help     the ledger hub's options (--listen on your mesh address, never every interface)" ;;
    creatura-convoy) echo "  creatura-convoy --arm J --seed 0                              three trucks after a flood, as a story
  creatura-convoy --arm J --seed 0 --replay run.jsonl --quiet    a replay; open convoy-viewer.html and drop run.jsonl on it
  creatura-convoy --battery --seeds 50                           every arm over 50 seeds, with the hypotheses scored" ;;
  esac
}

for rid in "${rids[@]}"; do
  ext=""; [[ "$rid" == win-* ]] && ext=".exe"
  for entry in "${apps[@]}"; do
    name="${entry%%|*}"; project="${entry#*|}"
    dotnet publish "$root/$project" -c Release -r "$rid" --self-contained true \
      -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none \
      -p:Version="$version" -o "$work/publish/$name-$rid" --nologo -v quiet

    stage="$work/$name-$version-$rid"
    mkdir -p "$stage"
    # A single-file executable carries everything inside it, so it can be renamed. Nothing else published ships: the
    # hub's web.config and static-assets manifest are for IIS and wwwroot, which it does not use.
    exe="$(find "$work/publish/$name-$rid" -maxdepth 1 -type f \( -name '*.exe' -o -perm -u+x \) ! -name '*.dll' ! -name '*.so' ! -name '*.dylib' | head -1)"
    cp "$exe" "$stage/$name$ext"
    cp "$root/LICENSE.md" "$stage/"
    [ "$name" = creatura-convoy ] && cp "$root/tools/convoy-viewer/index.html" "$stage/convoy-viewer.html"
    cat > "$stage/README.txt" <<TXT
$name $version for $rid, part of Creatura (https://github.com/egarim/creatura)

$(readme "$name")

No .NET is needed to run it. On macOS a downloaded file is quarantined; clear it once with
  xattr -d com.apple.quarantine $name

Licensed under the PolyForm Noncommercial License 1.0.0 (LICENSE.md).
TXT
    if [[ "$rid" == win-* ]]; then
      (cd "$work" && zip -qr "$out/$name-$version-$rid.zip" "$name-$version-$rid")
    else
      tar -C "$work" -czf "$out/$name-$version-$rid.tar.gz" "$name-$version-$rid"
    fi
    echo "built $name-$version-$rid"
  done
done

# The libraries and the tool as NuGet packages (the hub is a web app and does not pack). Each .nupkg gets its symbols
# beside it as a .snupkg (Directory.Build.props), which `dotnet nuget push` sends to nuget.org's symbol server with it.
mkdir -p "$out/nuget"
for project in "$root"/src/*/*.csproj; do
  case "$project" in *Creatura.Cells.Hub*) continue ;; esac
  dotnet pack "$project" -c Release -p:Version="$version" -o "$out/nuget" --nologo -v quiet
done
# The dotnet new templates (Creatura.Templates): packing writes this version into each template's default.
dotnet pack "$root/templates/Creatura.Templates.csproj" -c Release -p:Version="$version" -o "$out/nuget" --nologo -v quiet

(cd "$out" && find . -maxdepth 2 -type f \( -name '*.tar.gz' -o -name '*.zip' -o -name '*.nupkg' -o -name '*.snupkg' \) | sed 's|^\./||' | sort | xargs shasum -a 256 > SHA256SUMS)
echo "done: $out"
