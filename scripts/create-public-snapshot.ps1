[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [string]$Branch = "main",
    [string]$CommitMessage = "Initial LocalDeck open-source release"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $root "artifacts\localdeck-public-snapshot"
}

$rootPath = [IO.Path]::GetFullPath($root)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $root "artifacts"))
if (-not $outputPath.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
    throw "Refusing to write the snapshot outside the artifacts directory: $outputPath"
}

Push-Location $root
try {
    $dirty = git status --porcelain
    if ($dirty) {
        throw "Commit or stash current changes before creating a public snapshot."
    }

    if (Test-Path -LiteralPath $outputPath) {
        Remove-Item -LiteralPath $outputPath -Recurse -Force
    }
    New-Item -ItemType Directory -Force -Path $outputPath | Out-Null

    $archive = Join-Path $env:TEMP "localdeck-public-$([guid]::NewGuid().ToString('N')).zip"
    try {
        git archive --format=zip --output=$archive HEAD
        Expand-Archive -LiteralPath $archive -DestinationPath $outputPath
    }
    finally {
        if (Test-Path -LiteralPath $archive) {
            Remove-Item -LiteralPath $archive -Force
        }
    }

    Push-Location $outputPath
    try {
        git init -b $Branch
        if (-not (git config user.name)) {
            git config user.name "LocalDeck"
        }
        if (-not (git config user.email)) {
            git config user.email "localdeck@users.noreply.github.com"
        }
        git add -A
        git commit -m $CommitMessage
    }
    finally {
        Pop-Location
    }

    Write-Host "Clean public snapshot: $outputPath"
    Write-Host "No previous Git history is included."
    Write-Host "Review the snapshot, create a new GitHub repository, then push this branch."
}
finally {
    Pop-Location
}
