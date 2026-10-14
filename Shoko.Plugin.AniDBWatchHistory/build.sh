#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

dotnet build "$project_dir/AniDBWatchHistory.Daily.csproj" \
  --configuration Release

echo "Plugin: $project_dir/bin/Release/net10.0/Shoko.Plugin.AniDBWatchHistory.dll"
