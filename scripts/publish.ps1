[CmdletBinding()]
param(
    [string]$Runtime = "win-x64"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\LocalDeck.App\LocalDeck.App.csproj"
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "LocalDeck-$Runtime"
$hysteria = Join-Path $root "vendor\hysteria\hysteria.exe"
$expectedHysteriaHash = "162EF8FE55DC7EC810DDA662908F8E65EC36BD6DA39CB87C3BD66184AB68F067"

Push-Location $root
try {
    if (-not (Test-Path $hysteria)) {
        throw "Bundled Hysteria2 engine is missing: $hysteria"
    }
    $actualHysteriaHash = (Get-FileHash $hysteria -Algorithm SHA256).Hash
    if ($actualHysteriaHash -ne $expectedHysteriaHash) {
        throw "Bundled Hysteria2 engine hash mismatch: $actualHysteriaHash"
    }

    [xml]$projectXml = Get-Content $project
    $version = [string]$projectXml.Project.PropertyGroup.Version
    $zip = Join-Path $artifacts "LocalDeck-$version-$Runtime.zip"

    npm ci --prefix frontend
    npm run build --prefix frontend

    if (Test-Path $publish) {
        $resolvedPublish = [IO.Path]::GetFullPath($publish)
        $resolvedArtifacts = [IO.Path]::GetFullPath($artifacts)
        if (-not $resolvedPublish.StartsWith($resolvedArtifacts, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Refusing to remove publish path outside artifacts: $resolvedPublish"
        }
        Remove-Item -LiteralPath $publish -Recurse -Force
    }

    dotnet publish $project `
        -c Release `
        -r $Runtime `
        --self-contained true `
        -o $publish

    Copy-Item -LiteralPath (Join-Path $root "README.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "LICENSE") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "THIRD_PARTY_NOTICES.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "SECURITY.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "CONTRIBUTING.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "CODE_OF_CONDUCT.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "CHANGELOG.md") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "scripts\detect-capabilities.ps1") -Destination $publish
    Copy-Item -LiteralPath (Join-Path $root "docs") -Destination $publish -Recurse

    $forbiddenNames = @(
        "settings.json",
        "active.yaml",
        "imported-clash.yaml",
        "capabilities.json",
        "installations.json"
    )
    $forbiddenFiles = Get-ChildItem -LiteralPath $publish -Recurse -File | Where-Object {
        $forbiddenNames -contains $_.Name -or
        $_.Extension -in ".db", ".log", ".part", ".pem", ".pfx", ".key"
    }
    if ($forbiddenFiles) {
        throw "Refusing to publish local state or credentials: $($forbiddenFiles.FullName -join ', ')"
    }

    $textExtensions = @(".md", ".txt", ".ps1", ".json", ".yaml", ".yml", ".config", ".cs", ".ts", ".tsx", ".css")
    $privatePatterns = @(
        'C:\\Users\\',
        'BEGIN (RSA |OPENSSH |EC |DSA )?PRIVATE KEY',
        'ghp_[A-Za-z0-9]{20,}',
        'github_pat_'
    )
    $leaks = Get-ChildItem -LiteralPath $publish -Recurse -File |
        Where-Object { $textExtensions -contains $_.Extension } |
        Select-String -Pattern $privatePatterns
    if ($leaks) {
        throw "Refusing to publish personal paths or secrets: $($leaks.Path -join ', ')"
    }

    if (Test-Path $zip) {
        Remove-Item -LiteralPath $zip -Force
    }
    Compress-Archive -Path (Join-Path $publish "*") -DestinationPath $zip -CompressionLevel Optimal
    Write-Host "Published: $zip"
}
finally {
    Pop-Location
}
