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

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\BeastStrapUpdater.ps1 -BackfillNotes
#>
param(
    # Explicit version to ship (e.g. "421.0.0"). If empty, auto-bumps the current one.
    [string]$Version = "",

    # Skip the local publish - reuse the already-built exe in bin\...\win-x64\publish.
    [switch]$SkipBuild,

    # Personal access token. If empty, reads the one your credential manager saved.
    [string]$Token = "",

    # Don't touch git or GitHub; just print what would happen.
    [switch]$DryRun,

    # Backfill: recompute every published release's body from git history between
    # consecutive tags and PATCH it onto GitHub. Used once to give older releases
    # (which shipped with placeholder "BeastStrap <version>" bodies) a real changelog.
    # Idempotent - notes are derived from git, so re-running writes the same text.
    [switch]$BackfillNotes
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
if (-not $BackfillNotes -and -not $DryRun) {
    $raw = Get-Content $Csproj -Raw
    $raw = $raw.Replace("<Version>$current</Version>", "<Version>$Version</Version>")
    $raw = $raw.Replace("<FileVersion>$current.0</FileVersion>", "<FileVersion>$Version.0</FileVersion>")
    Set-Content -Path $Csproj -Value $raw -NoNewline
    Write-Host "csproj bumped to $Version" -ForegroundColor Green
}

# ---------------------------------------------------------------------------
# 3. build the single-file exe
# ---------------------------------------------------------------------------
if (-not $BackfillNotes -and -not $SkipBuild -and -not $DryRun) {
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
# 4b. backfill release notes (see the -BackfillNotes switch). Iterates the
#     published releases oldest->newest tag order, derives each release's
#     changelog from git log between consecutive tags, and PATCHes the body.
# ---------------------------------------------------------------------------
if ($BackfillNotes) {
    if ($DryRun) {
        Write-Host "[DRY RUN] would backfill release notes from git history" -ForegroundColor Yellow
        return
    }

    $tagsAsc = @(git -C $RepoRoot tag --sort=version:refname)
    if ($tagsAsc.Count -eq 0) { throw "No local tags to derive notes from." }

    $relList = Invoke-RestMethod -Uri "https://api.github.com/repos/$Repo/releases?per_page=100" -Headers @{ "User-Agent" = "BeastStrap" }
    $updated = 0

    foreach ($rel in $relList) {
        $idx = [array]::IndexOf($tagsAsc, $rel.tag_name)
        if ($idx -lt 0) { continue }

        $prevTag = if ($idx -gt 0) { $tagsAsc[$idx - 1] } else { "" }
        if ($prevTag) {
            $commitLog = git -C $RepoRoot log --oneline --no-merges "$prevTag..$($rel.tag_name)"
        } else {
            $commitLog = git -C $RepoRoot log --oneline --no-merges $rel.tag_name
        }

        $noteLines = @($commitLog | Where-Object { $_ -and $_ -notmatch '^[0-9a-f]{7,40}\s+Release v?\d' } | ForEach-Object {
            $m = [regex]::Match($_, '^[0-9a-f]{7,40}\s+(.*)$')
            if ($m.Success) { "- $($m.Groups[1].Value)" } else { "- $_" }
        })
        if ($noteLines.Count -eq 0) { continue }

        # Only touch placeholder bodies ("BeastStrap <version>", empty, "No release
        # notes") so hand-written notes on old pre-fork releases are never clobbered.
        $isPlaceholder = [string]::IsNullOrWhiteSpace($rel.body) -or
            $rel.body -match '^\s*BeastStrap\s+v?\d' -or
            $rel.body -match '^\s*No release notes\.?'

        $newBody = ($noteLines -join "`n")
        if (-not $isPlaceholder) {
            Write-Host "  $($rel.tag_name): has written notes, skipped" -ForegroundColor DarkGray
            continue
        }

        # PS 5.1 corrupts string bodies on the wire for non-ASCII text, so send the
        # JSON as explicit UTF-8 bytes.
        $json = @{ body = $newBody } | ConvertTo-Json
        Invoke-RestMethod -Method Patch -Uri "https://api.github.com/repos/$Repo/releases/$($rel.id)" `
            -Headers $Headers -ContentType "application/json; charset=utf-8" `
            -Body ([System.Text.Encoding]::UTF8.GetBytes($json)) | Out-Null
        Write-Host "  $($rel.tag_name): notes updated ($($noteLines.Count) change(s))" -ForegroundColor Green
        $updated++
    }

    Write-Host "`nBackfilled $updated release(s)." -ForegroundColor Cyan
    return
}

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
# 5b. generate release notes from commits since the last tag, so the GitHub
#     release body is a real changelog ("what changed / what was added") instead
#     of just the version number. Filtered to feature commits (no merges, no
#     "Release vX" bumps).
# ---------------------------------------------------------------------------
$prevTag = git -C $RepoRoot tag --sort=-version:refname | Where-Object { $_ -ne $Tag } | Select-Object -First 1
if ($prevTag) {
    $commitLog = git -C $RepoRoot log --oneline --no-merges "$prevTag..HEAD"
} else {
    $commitLog = git -C $RepoRoot log --oneline --no-merges
}
$noteLines = @($commitLog | Where-Object { $_ -and $_ -notmatch '^[0-9a-f]{7,40}\s+Release v?\d' } | ForEach-Object {
    $m = [regex]::Match($_, '^[0-9a-f]{7,40}\s+(.*)$')
    if ($m.Success) { "- $($m.Groups[1].Value)" } else { "- $_" }
})
if ($noteLines.Count -eq 0) {
    $notes = "BeastStrap $Version"
} else {
    $notes = ($noteLines -join "`n")
}
Write-Host "Release notes: $($noteLines.Count) change(s) since $(if ($prevTag) { $prevTag } else { 'the beginning' })" -ForegroundColor Yellow

# ---------------------------------------------------------------------------
# 6. create the release and upload the exe (published immediately, no CI needed)
# ---------------------------------------------------------------------------
$body = @{
    tag_name   = $Tag
    name       = "BeastStrap $Version"
    draft      = $false
    prerelease = $false
    body       = $notes
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