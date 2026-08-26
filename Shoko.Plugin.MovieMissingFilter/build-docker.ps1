$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (Test-Path dist) { Remove-Item -Recurse -Force dist }

docker run --rm `
  -e DOTNET_CLI_HOME=/tmp/dotnet-home `
  -e NUGET_PACKAGES=/tmp/nuget-packages `
  -v "${PWD}:/src" `
  -w /src `
  mcr.microsoft.com/dotnet/sdk:10.0 `
  dotnet publish Shoko.Plugin.MovieMissingFilter.csproj -c Release -o dist --no-self-contained

Write-Host "`nPlugin output: $PWD/dist"
