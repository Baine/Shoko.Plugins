#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")"
rm -rf dist

docker run --rm \
  --user "$(id -u):$(id -g)" \
  -e DOTNET_CLI_HOME=/tmp/dotnet-home \
  -e NUGET_PACKAGES=/tmp/nuget-packages \
  -v "$PWD:/src" \
  -w /src \
  mcr.microsoft.com/dotnet/sdk:10.0 \
  dotnet publish Shoko.Plugin.MovieMissingFilter.csproj -c Release -o dist --no-self-contained

printf '\nPlugin output: %s/dist\n' "$PWD"
