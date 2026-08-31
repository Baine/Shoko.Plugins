#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist
dotnet publish Shoko.Plugin.TmdbLinkFixer.csproj -c Release -o dist --no-self-contained
printf '\nPlugin output: %s/dist\n' "$PWD"
