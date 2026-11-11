[CmdletBinding()]
param(
    [string]$RepoName = "localdeck",
    [string]$Description = "Local-first Windows all-in-one control deck"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    gh auth status *> $null
    if ($LASTEXITCODE -ne 0) {
        throw "GitHub CLI is not logged in. Run: gh auth login"
    }

    $login = (gh api user --jq ".login").Trim()
    if (-not $login) {
        throw "Could not determine the GitHub account."
    }

    if (-not (Test-Path ".git")) {
        git init -b main
    }

    if (-not (git config user.name)) {
        git config user.name "LocalDeck"
    }
    if (-not (git config user.email)) {
        git config user.email "$login@users.noreply.github.com"
    }

    git add -A
    git diff --cached --quiet
    if ($LASTEXITCODE -ne 0) {
        git commit -m "Initial LocalDeck implementation"
    }

    $repository = "$login/$RepoName"
    gh repo view $repository *> $null
    if ($LASTEXITCODE -ne 0) {
        gh repo create $repository `
            --private `
            --description $Description `
            --source . `
            --remote origin `
            --push
    }
    else {
        $remotes = git remote
        if ($remotes -notcontains "origin") {
            git remote add origin "https://github.com/$repository.git"
        }
        git push -u origin main
    }

    Write-Host "Private repository: https://github.com/$repository"
}
finally {
    Pop-Location
}
