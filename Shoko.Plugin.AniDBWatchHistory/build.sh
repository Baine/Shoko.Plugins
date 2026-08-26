#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
shoko_root="${1:-${SHOKO_SERVER_ROOT:-}}"

if [[ -z "$shoko_root" ]]; then
  echo "Usage: ./build.sh /absolute/path/to/ShokoServer" >&2
  exit 2
fi

dotnet build "$project_dir/AniDBWatchHistory.Daily.csproj" \
  --configuration Release \
  -p:ShokoServerRoot="$shoko_root"

echo "Plugin: $project_dir/bin/Release/net10.0/Shoko.Plugin.AniDBWatchHistory.dll"
