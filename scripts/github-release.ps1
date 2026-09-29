param(
    [Parameter(Mandatory)]
    [System.String]$ZipPath,

    [Parameter(Mandatory)]
    [System.String]$ProjectPath,

    # Allow running with uncommitted changes (the released zip may not match the tagged commit).
    [switch]$AllowDirty,

    # Run all checks and print the commands that would run, without pushing or creating anything.
    [switch]$DryRun
)

# Exit code 1 on any problem so the MSBuild Exec target fails the build.
function Fail([string]$Message) {
    Write-Host "GITHUB RELEASE FAILED: $Message" -ForegroundColor Red
    exit 1
}

# --- Preconditions: tooling -------------------------------------------------
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "git not found in PATH" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
    Fail "GitHub CLI (gh) not found - install it first: winget install GitHub.cli"
}

gh auth status | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "gh is not authenticated - run: gh auth login" }

# --- Preconditions: build artifact -----------------------------------------
if (-not (Test-Path $ZipPath)) {
    Fail "zip not found: $ZipPath (did the Release packaging in publish.ps1 run?)"
}
$zip = (Resolve-Path $ZipPath).Path

# --- Version: Package/manifest.json is the source of truth -----------------
$manifestPath = Join-Path $ProjectPath "Package\manifest.json"
if (-not (Test-Path $manifestPath)) { Fail "manifest not found: $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version_number
if (-not $version) { Fail "version_number missing in $manifestPath" }

# Cross-check the plugin version compiled into the assembly (warn only).
$srcHit = Select-String -Path (Join-Path $ProjectPath "*.cs") -Pattern 'PluginVersion\s*=\s*"([^"]+)"' |
    Select-Object -First 1
if ($srcHit) {
    $srcVersion = $srcHit.Matches[0].Groups[1].Value
    if ($srcVersion -ne $version) {
        Write-Host "WARNING: PluginVersion in source is '$srcVersion' but manifest says '$version'" -ForegroundColor Yellow
    }
}

$tag = "v$version"

# --- Preconditions: git state ----------------------------------------------
# Capture output and exit code separately: piping git directly into
# Select-Object -First 1 kills it mid-run and poisons $LASTEXITCODE (-1).
$branchOut = @(git rev-parse --abbrev-ref HEAD)
$branchExit = $LASTEXITCODE
$branch = $branchOut | Select-Object -First 1
if ($branchExit -ne 0 -or -not $branch -or $branch -eq "HEAD") {
    Fail "not on a branch (detached HEAD?) - checkout a branch first"
}

$dirty = git status --porcelain
if ($dirty) {
    if (-not $AllowDirty) {
        git status --short
        Fail "working tree is not clean - commit first (or pass -AllowDirty to release anyway)"
    }
    Write-Host "WARNING: working tree is dirty (-AllowDirty): the zip may not match commit $branch" -ForegroundColor Yellow
}

$repoUrl = git remote get-url origin
if ($LASTEXITCODE -ne 0 -or -not $repoUrl) { Fail "no 'origin' remote configured" }
if ($repoUrl -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') {
    Fail "origin is not a GitHub remote: $repoUrl"
}
$repo = "$($Matches[1])/$($Matches[2])"

gh repo view $repo | Out-Null
if ($LASTEXITCODE -ne 0) { Fail "repository $repo not found on GitHub (or no access with this gh account)" }

# --- Does a release (or draft) already exist for this tag? ------------------
# `gh release view` can miss drafts, and a jq expression containing quotes is
# mangled by Windows PowerShell 5.1 native argument passing, so fetch the raw
# JSON and filter in PowerShell instead. The list includes drafts for accounts
# with push access; html_url works for drafts too (the /releases/tag/ URL
# needs the tag to exist, i.e. a published release).
function Find-Release([string]$TagName) {
    $out = @(gh api "repos/$repo/releases" --paginate --slurp)
    if ($LASTEXITCODE -ne 0) { Fail "failed to list releases of $repo (exit $LASTEXITCODE)" }
    # Two flatten passes: ConvertFrom-Json may or may not unwrap the outer
    # slurp array, so normalize down to individual release objects.
    $pages = ($out -join "`n") | ConvertFrom-Json
    $flat = @($pages | ForEach-Object { $_ } | ForEach-Object { $_ })
    $found = @($flat | Where-Object { $_.tag_name -eq $TagName }) | Select-Object -First 1
    return $found
}

$existing = Find-Release $tag
$releaseExists = [bool]$existing

# --- Commands (checked or executed) ----------------------------------------
$pushArgs = @("push", "origin", $branch)
if ($releaseExists) {
    $ghArgs = @("release", "upload", $tag, $zip, "--clobber", "--repo", $repo)
    $action = "update release $tag (re-upload zip)"
}
else {
    $ghArgs = @("release", "create", $tag, $zip, "--title", $tag, "--generate-notes", "--target", $branch, "--draft", "--repo", $repo)
    $action = "create DRAFT $tag"
}

if ($DryRun) {
    Write-Host "[DRY RUN] version=$version branch=$branch repo=$repo action=$action" -ForegroundColor Cyan
    Write-Host "[DRY RUN] git $($pushArgs -join ' ')" -ForegroundColor Cyan
    Write-Host "[DRY RUN] gh $($ghArgs -join ' ')" -ForegroundColor Cyan
    Write-Host "[DRY RUN] on creation: open the release page in the browser" -ForegroundColor Cyan
    exit 0
}

# --- Push so the tag can be created from this commit when the draft is published
Write-Host "Pushing $branch to origin..."
git push origin $branch
if ($LASTEXITCODE -ne 0) { Fail "git push failed - pull/rebase first, then rebuild" }

# --- Create the draft, or update the existing release ----------------------
$created = -not $releaseExists
if ($releaseExists) {
    Write-Host "Release $tag already exists - re-uploading zip"
}
else {
    Write-Host "Creating DRAFT release $tag on $repo"
}
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release $($ghArgs[1]) failed (exit $LASTEXITCODE)" }

# --- Link to the release (drafts use the edit URL; fall back to the tag URL)
$url = (Find-Release $tag).html_url
if (-not $url) { $url = "https://github.com/$repo/releases/tag/$tag" }

if ($created) {
    Write-Host "Draft created: $url" -ForegroundColor Green
    try { Start-Process $url }
    catch { Write-Host "WARNING: could not open the browser: $_" -ForegroundColor Yellow }
}
else {
    Write-Host "Updated: $url" -ForegroundColor Green
}
exit 0
