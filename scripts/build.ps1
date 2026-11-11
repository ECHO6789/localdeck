[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    npm ci --prefix frontend
    npm run build --prefix frontend
    dotnet build "src\LocalDeck.App\LocalDeck.App.csproj" -c $Configuration
    dotnet build "tools\LocalDeck.Diagnostics\LocalDeck.Diagnostics.csproj" -c $Configuration
}
finally {
    Pop-Location
}
