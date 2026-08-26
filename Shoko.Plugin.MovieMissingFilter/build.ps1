$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
if (Test-Path dist) { Remove-Item dist -Recurse -Force }
dotnet publish Shoko.Plugin.MovieMissingFilter.csproj -c Release -o dist --no-self-contained
Write-Host "`nPlugin output: $PSScriptRoot/dist"
