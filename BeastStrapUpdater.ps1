<#
.SYNOPSIS
    BeastStrap auto-updater release helper.

    Bumps the version, builds the single-file release exe, and pushes the source +
    a version tag to GitHub. Pushing a v* tag triggers the .github/workflows/ci-release.yml
    workflow, which builds the same exe on GitHub and drops a DRAFT release for you to
    publish (the "Publish release" button on the releases page).

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\BeastStrapUpdater.ps1

.EXAMPLE
    # pick the exact version yourself and skip the local build
    powershell -ExecutionPolicy Bypass -File .\BeastStrapUpdater.ps1 -Version 421.0.0 -SkipBuild
#>
param(
    # Explicit version to tag (e.g. "420.49.0"). If empty, auto-bumps the current one.
    [string]$Version = "",

    # Skip the local publish (CI builds on GitHub anyway) — just bump + tag + push.
    [switch]$SkipBuild,

    # Do everything except push. Prints the git commands you can run yourself.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$RepoRoot   = Split-Path -Parent $MyInvocation.MyCommand.Path
$CsprojPath = Join-Path $RepoRoot "MrExStrap\BeastStrap.csproj"
$PublishDir = Join-Path $RepoRoot "MrExStrap\bin\Release\net6.0-windows\win-x64\publish"
$DistDir    = Join-Path $RepoRoot "dist"

if (-not (Test-Path $CsprojPath)) { throw "Couldn't find $CsprojPath" }

function Get-CurrentVersion {
    $match = Select-String -Path $CsprojPath -Pattern '<Version>(.*)</Version>' | Select-Object -First 1
    if (-not $match) { throw "No <Version> tag found in csproj" }
    return $match.Matches[0].Groups[1].Value.Trim()
}

function New-BumpedVersion([string]$current) {
    $v = [Version]$current
    if ($v.Build -gt 0) {
        # 420.48.1 -> 420.48.2
        return "$($v.Major).$($v.Minor).$($v.Build + 1)"
    }
    # 420.48.0 -> 420.49.0
    return "$($v.Major).$($v.Minor + 1).0"
}

# ---- 1. resolve the version to ship ---------------------------------------
$current = Get-CurrentVersion
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = New-BumpedVersion $current
}
$Tag = "v$Version"
Write-Host "Current version: $current" -ForegroundColor Gray
Write-Host "Shipping version: $Version (tag $Tag)" -ForegroundColor Cyan

# ---- 2. bump the version in the csproj ------------------------------------
(Get-Content $CsprojPath -Raw)
    .Replace("<Version>$current</Version>", "<Version>$Version</Version>")
    .Replace("<FileVersion>$current.0</FileVersion>", "<FileVersion>$Version.0</FileVersion>") `
    | Set-Content $CsprojPath -NoNewline
Write-Host "Bumped csproj to $Version" -ForegroundColor Green

# ---- 3. build the release exe (optional) ----------------------------------
if (-not $SkipBuild) {
    Write-Host "Publishing single-file self-contained exe... (takes a couple of minutes)" -ForegroundColor Yellow
    & dotnet publish (Join-Path $RepoRoot "MrExStrap\BeastStrap.csproj") `
        -c Release -r win-x64 --self-contained true `
        -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
        --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

    New-Item -ItemType Directory -Path $DistDir -Force | Out-Null
    Copy-Item (Join-Path $PublishDir "BeastStrap.exe") (Join-Path $DistDir "BeastStrap.exe") -Force
    $exe = Get-Item (Join-Path $DistDir "BeastStrap.exe")
    Write-Host "Release exe ready: $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB, version $Version)" -ForegroundColor Green
}

# ---- 4. git commit + tag + push -------------------------------------------
if ($DryRun) {
    Write-Host "`n--- DRY RUN: run these yourself ---" -ForegroundColor Yellow
    Write-Host "  git add MrExStrap/BeastStrap.csproj"
    Write-Host "  git commit -m ""Release $Version"""
    Write-Host "  git push origin main"
    Write-Host "  git tag $Tag"
    Write-Host "  git push origin $Tag"
    Write-Host "`nPushing $Tag triggers CI which creates a draft release; publish it on GitHub."
    return
}

git add "MrExStrap/BeastStrap.csproj"
if ($LASTEXITCODE -ne 0) { throw "git add failed" }

if (git status --porcelain --untracked-files=no | Select-Object -First 1) {
    git commit -m "Release $Version"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
} else {
    Write-Host "No pending changes, skipping commit." -ForegroundColor Gray
}

git push origin main
if ($LASTEXITCODE -ne 0) { throw "git push main failed — are you signed in to GitHub?" }

if (git rev-parse -q --verify "refs/tags/$Tag" 2>$null) {
    Write-Host "Tag $Tag already exists locally, skipping." -ForegroundColor Gray
} else {
    git tag $Tag
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
}
git push origin $Tag
if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }

Write-Host "`nPushed $Tag. CI is building the release exe now." -ForegroundColor Green
Write-Host "When the workflow finishes, go to:" -ForegroundColor Green
Write-Host "  https://github.com/revenantsupport-spec/BeastStrap/releases" -ForegroundColor Cyan
Write-Host "find the DRAFT release '$Tag', check the exe, and hit 'Publish release'." -ForegroundColor Green
Write-Host "Installed BeastStrap copies (on an older version) will then auto-update." -ForegroundColor Green