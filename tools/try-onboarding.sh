#!/usr/bin/env bash
# O2 (docs/design-onboarding.md): every template makes a project that builds and runs offline against packages packed
# from this commit, and `creatura new` and `dotnet new` make the same files. Nothing is published: the packages go to
# a scratch folder, restore uses a fresh package cache, and the templates install into a throwaway hive.
#
#   tools/try-onboarding.sh            (a few minutes: it packs every package and restores three projects)
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
version="0.0.0-onboarding.$(date +%s)"
export NUGET_PACKAGES="$work/packages"              # a fresh cache: a package missing from the folder shows
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1

echo "== packing every package at $version"
for project in "$root"/src/*/*.csproj "$root/templates/Creatura.Templates.csproj"; do
  case "$project" in *Creatura.Cells.Hub*) continue ;; esac
  dotnet pack "$project" -c Release -p:Version="$version" -o "$work/nuget" --nologo -v quiet > "$work/pack.log" 2>&1 \
    || { cat "$work/pack.log"; exit 1; }
done
ls "$work/nuget" | sed 's/^/   /'

dotnet build "$root/src/Creatura.Cli" -c Release -p:Version="$version" --nologo -v quiet > "$work/build.log" 2>&1 || { cat "$work/build.log"; exit 1; }
creatura() { dotnet "$root/src/Creatura.Cli/bin/Release/net10.0/Creatura.Cli.dll" "$@"; }
dotnet new install "$work/nuget/Creatura.Templates.$version.nupkg" --debug:custom-hive "$work/hive" > /dev/null

failed=0
for template in agent world sk; do
  echo
  echo "== $template"
  creatura new "$template" --name "Try${template}" --output "$work/new/$template" --source "$work/nuget" > /dev/null
  dotnet new "creatura-$template" -n "Try${template}" -o "$work/dotnet-new/$template" --debug:custom-hive "$work/hive" > /dev/null
  if diff -r -x nuget.config "$work/new/$template" "$work/dotnet-new/$template" > "$work/diff.txt"; then
    echo "   creatura new and dotnet new made the same files: $(cd "$work/new/$template" && ls | tr '\n' ' ')"
  else
    echo "   the two differ:"; sed 's/^/     /' "$work/diff.txt"; failed=1
  fi
  if (cd "$work/new/$template" && env -u OPENAI_API_KEY dotnet run > "$work/run.txt" 2>&1); then
    sed -n '1,4p' "$work/run.txt" | sed 's/^/   | /'
    if grep -q "goal reached" "$work/run.txt" && ! grep -q "not reached" "$work/run.txt"; then echo "   ran: every run reached the goal"; else echo "   ran, but a goal was not reached:"; grep "goal" "$work/run.txt" | sed "s/^/     /"; failed=1; fi
  else
    echo "   did not build or run:"; tail -20 "$work/run.txt" | sed 's/^/     /'; failed=1
  fi
done

echo
[ "$failed" = 0 ] && echo "O2 holds: three templates, both ways, built and ran offline" || { echo "O2 failed"; exit 1; }
