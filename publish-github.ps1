param(
    [string]$Repository = 'SandrikPff/PFF',
    [string]$Tag = 'v5.4.0'
)
$ErrorActionPreference = 'Stop'
Set-Location -LiteralPath $PSScriptRoot

function EnsureCommand($Name, $WingetId) {
    $cmd = Get-Command $Name -ErrorAction SilentlyContinue
    if ($cmd) { return }
    $winget = Get-Command winget -ErrorAction SilentlyContinue
    if (-not $winget) { throw "$Name is missing. Install $WingetId, then retry." }
    & winget install --id $WingetId --exact --accept-source-agreements --accept-package-agreements
    if ($LASTEXITCODE -ne 0) { throw "Failed to install $WingetId (exit code $LASTEXITCODE)." }
    $env:PATH += ";$env:ProgramFiles\Git\cmd;$env:ProgramFiles\GitHub CLI"
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) { throw "$Name not in PATH. Open a new PowerShell and rerun the command." }
}

EnsureCommand git 'Git.Git'
EnsureCommand gh 'GitHub.cli'

& gh auth status --hostname github.com *> $null
if ($LASTEXITCODE -ne 0) {
    Write-Host 'Sign in to GitHub in the browser that opens.' -ForegroundColor Cyan
    & gh auth login --hostname github.com --git-protocol https --web
    if ($LASTEXITCODE -ne 0) { throw 'GitHub authentication failed.' }
}
& gh auth setup-git
if ($LASTEXITCODE -ne 0) { throw 'Failed to configure Git credentials.' }

$repoInfo = & gh api "repos/$Repository" --jq '.full_name'
if ($LASTEXITCODE -ne 0 -or $repoInfo.Trim() -ne $Repository) { throw "Repository $Repository is not accessible to the GitHub CLI account." }

if (-not (Test-Path -LiteralPath '.git')) { & git init -b main }
& git config user.name *> $null
if ($LASTEXITCODE -ne 0) { & git config user.name 'PFF Maintainer' }
& git config user.email *> $null
if ($LASTEXITCODE -ne 0) {
    $login = (& gh api user --jq '.login').Trim()
    $numericId = (& gh api user --jq '.id').Trim()
    & git config user.email "$numericId+$login@users.noreply.github.com"
}
& git add --all
if ($LASTEXITCODE -ne 0) { throw 'git add failed.' }
& git diff --cached --quiet
$hasChanges = $LASTEXITCODE -ne 0
if ($hasChanges) {
    & git commit -m 'Initial publication of PFF application and documentation'
    if ($LASTEXITCODE -ne 0) { throw 'git commit failed.' }
}
& git remote get-url origin *> $null
if ($LASTEXITCODE -ne 0) { & git remote add origin "https://github.com/$Repository.git" }
$remote = (& git remote get-url origin).Trim()
if ($remote -notlike "*github.com/$Repository.git" -and $remote -notlike "*github.com/$Repository") { throw "Unexpected origin remote: $remote" }
& git push -u origin main
if ($LASTEXITCODE -ne 0) { throw 'git push failed. Check whether the repository is empty or already has commits.' }

& git tag --list $Tag | Out-Null
$tagExists = (& git tag --list $Tag).Trim()
if (-not $tagExists) {
    & git tag -a $Tag -m "PFF $Tag"
    if ($LASTEXITCODE -ne 0) { throw 'git tag failed.' }
}
& git push origin $Tag
if ($LASTEXITCODE -ne 0) { throw 'Could not push release tag.' }

Write-Host ''
Write-Host 'Source code pushed.' -ForegroundColor Green
Write-Host "Repository: https://github.com/$Repository" -ForegroundColor Cyan
Write-Host "Release workflow: https://github.com/$Repository/actions" -ForegroundColor Cyan
Write-Host 'The Windows build must finish successfully before the installer appears under Releases.' -ForegroundColor Yellow
