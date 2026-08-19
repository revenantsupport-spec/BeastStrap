<#
.SYNOPSIS
    BeastStrap auto-updater release helper.

    Fully automatic: bumps the version, builds the single-file exe, commits + pushes,
    tags it, creates the GitHub release, uploads the exe, and leaves it published so
    installed BeastStrap copies auto-update. Uses the GitHub login saved in your
    credential manager (no token to paste), or an explicit -Token.

    NOTE: keep this file ASCII-only. Non-ASCII characters (e.g. em dashes) break
    parsing in Windows PowerShell 5.1, which misreads the UTF-8 bytes as ANSI.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\BeastStrapUpdater.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\BeastStrapUpdater.ps1 -Version 421.0.0 -SkipBuild
#>
param(
    # Explicit version to ship (e.g. "421.0.0"). If empty, auto-bumps the current one.
    [string]$Version = "",

    # Skip the local publish - reuse the already-built exe in bin\...\win-x64\publish.
    [switch]$SkipBuild,

    # Personal access token. If empty, reads the one your credential manager saved.
    [string]$Token = "",

    # Don't touch git or GitHub; just print what would happen.
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$Repo     = "revenantsupport-spec/BeastStrap"
$RepoRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$Csproj   = Join-Path $RepoRoot "MrExStrap\BeastStrap.csproj"
$Publish  = Join-Path $RepoRoot "MrExStrap\bin\Release\net6.0-windows\win-x64\publish\BeastStrap.exe"

if (-not (Test-Path $Csproj)) { throw "Couldn't find $Csproj" }

# ---------------------------------------------------------------------------
# 1. resolve the version
# ---------------------------------------------------------------------------
$match = Select-String -Path $Csproj -Pattern '<Version>([^<]*)</Version>' | Select-Object -First 1
$current = $match.Matches[0].Groups[1].Value.Trim()

if ([string]::IsNullOrWhiteSpace($Version)) {
    $v = [Version]$current
    if ($v.Build -gt 0) {
        $Version = "$($v.Major).$($v.Minor).$($v.Build + 1)"
    } else {
        $Version = "$($v.Major).$($v.Minor + 1).0"
    }
}
$Tag = "v$Version"
Write-Host "Current: $current -> shipping $Version (tag $Tag)" -ForegroundColor Cyan

# ---------------------------------------------------------------------------
# 2. bump the csproj
# ---------------------------------------------------------------------------
if (-not $DryRun) {
    $raw = Get-Content $Csproj -Raw
    $raw = $raw.Replace("<Version>$current</Version>", "<Version>$Version</Version>")
    $raw = $raw.Replace("<FileVersion>$current.0</FileVersion>", "<FileVersion>$Version.0</FileVersion>")
    Set-Content -Path $Csproj -Value $raw -NoNewline
    Write-Host "csproj bumped to $Version" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 3. build the single-file exe
# ---------------------------------------------------------------------------
if (-not $SkipBuild -and -not $DryRun) {
    Write-Host "Publishing single-file self-contained exe (couple of minutes)..." -ForegroundColor Yellow
    & dotnet publish (Join-Path $RepoRoot "MrExStrap\BeastStrap.csproj") -c Release -r win-x64 `
        --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true --nologo -v q
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }
    if (-not (Test-Path $Publish)) { throw "publish produced no exe at $Publish" }
}

if (-not (Test-Path $Publish)) { throw "No exe at $Publish - build once (drop -SkipBuild) first." }
$exeInfo = Get-Item $Publish
$mb = [math]::Round($exeInfo.Length / 1MB, 1)
Write-Host ("Release exe: {0} ({1} MB, version {2})" -f $Publish, $mb, $Version) -ForegroundColor Green

# ---------------------------------------------------------------------------
# 4. github token (from credential manager unless -Token given)
# ---------------------------------------------------------------------------
if ([string]::IsNullOrWhiteSpace($Token) -and -not $DryRun) {
    $cred = "protocol=https`nhost=github.com`n`n" | git credential fill 2>$null
    $Token = (($cred -split "`n" | Where-Object { $_ -like "password=*" }) -replace "password=", "")
}
if ([string]::IsNullOrWhiteSpace($Token)) { throw "No GitHub token found. Sign in with 'git credential-manager github login' once, or pass -Token." }
$Headers = @{ "User-Agent" = "BeastStrap"; "Authorization" = "Bearer $Token"; "Accept" = "application/vnd.github+json" }

# ---------------------------------------------------------------------------
# 5. commit, push, tag
# ---------------------------------------------------------------------------
if ($DryRun) {
    Write-Host "`n[DRY RUN] git steps:" -ForegroundColor Yellow
    Write-Host "  git add MrExStrap/BeastStrap.csproj; git commit -m 'Release $Version'; git push origin main"
    Write-Host "  git tag $Tag; git push origin $Tag"
    Write-Host "  POST /repos/$Repo/releases (tag $Tag) + upload $Publish"
    return
}

git -C $RepoRoot add "MrExStrap/BeastStrap.csproj"
if ($LASTEXITCODE -ne 0) { throw "git add failed" }

$pending = git -C $RepoRoot status --porcelain --untracked-files=no
if ($pending) {
    git -C $RepoRoot commit -m "Release $Version"
    if ($LASTEXITCODE -ne 0) { throw "git commit failed" }
    git -C $RepoRoot push origin main
    if ($LASTEXITCODE -ne 0) { throw "git push main failed - are you signed in?" }
}

if (-not (git -C $RepoRoot rev-parse -q --verify "refs/tags/$Tag" 2>$null)) {
    git -C $RepoRoot tag $Tag
    if ($LASTEXITCODE -ne 0) { throw "git tag failed" }
}
git -C $RepoRoot push origin $Tag
if ($LASTEXITCODE -ne 0) { throw "git push tag failed" }

# ---------------------------------------------------------------------------
# 6. create the release and upload the exe (published immediately, no CI needed)
# ---------------------------------------------------------------------------
$body = @{
    tag_name   = $Tag
    name       = "BeastStrap $Version"
    draft      = $false
    prerelease = $false
    body       = "BeastStrap $Version"
} | ConvertTo-Json

Write-Host "Creating GitHub release $Tag ..." -ForegroundColor Yellow
$rel = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases" -Method Post -Headers $Headers -ContentType "application/json" -Body $body
Write-Host "Release created: $($rel.html_url)" -ForegroundColor Green

$assetUrl = "https://uploads.github.com/repos/$Repo/releases/$($rel.id)/assets?name=BeastStrap.exe"
Write-Host "Uploading BeastStrap.exe ($mb MB)..." -ForegroundColor Yellow
$up = curl.exe -sS -X POST -H "Authorization: Bearer $Token" -H "Accept: application/vnd.github+json" `
    -H "Content-Type: application/octet-stream" --data-binary "@$Publish" $assetUrl
if ($LASTEXITCODE -ne 0) { throw "asset upload failed (curl exit $LASTEXITCODE): $up" }
$upJson = $up | ConvertFrom-Json
Write-Host "Uploaded: $($upJson.browser_download_url)" -ForegroundColor Green

# ---------------------------------------------------------------------------
# 7. verify
# ---------------------------------------------------------------------------
Start-Sleep -Seconds 2
$latest = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases/latest" -Headers @{ "User-Agent" = "BeastStrap" }
Write-Host "`nDone. /releases/latest now returns $($latest.tag_name)." -ForegroundColor Green
Write-Host "Installed BeastStrap copies on an older version will auto-update on next launch." -ForegroundColor Green
Write-Host "Release page: https://github.com/$Repo/releases" -ForegroundColor Cyan