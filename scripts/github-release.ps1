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

# --- Does the release already exist? ---------------------------------------
gh release view $tag --repo $repo | Out-Null
$releaseExists = ($LASTEXITCODE -eq 0)

# --- Commands (checked or executed) ----------------------------------------
$pushArgs = @("push", "origin", $branch)
if ($releaseExists) {
    $ghArgs = @("release", "upload", $tag, $zip, "--clobber", "--repo", $repo)
    $action = "update release $tag (re-upload zip)"
}
else {
    $ghArgs = @("release", "create", $tag, $zip, "--title", $tag, "--generate-notes", "--target", $branch, "--repo", $repo)
    $action = "create release $tag"
}

if ($DryRun) {
    Write-Host "[DRY RUN] version=$version branch=$branch repo=$repo action=$action" -ForegroundColor Cyan
    Write-Host "[DRY RUN] git $($pushArgs -join ' ')" -ForegroundColor Cyan
    Write-Host "[DRY RUN] gh $($ghArgs -join ' ')" -ForegroundColor Cyan
    exit 0
}

# --- Push so the tag points at code that exists on GitHub -------------------
Write-Host "Pushing $branch to origin..."
git push origin $branch
if ($LASTEXITCODE -ne 0) { Fail "git push failed - pull/rebase first, then rebuild" }

# --- Create or update the release ------------------------------------------
if ($releaseExists) {
    Write-Host "Release $tag already exists - re-uploading zip"
}
else {
    Write-Host "Creating release $tag on $repo"
}
& gh @ghArgs
if ($LASTEXITCODE -ne 0) { Fail "gh release $($ghArgs[1]) failed (exit $LASTEXITCODE)" }

Write-Host "OK: https://github.com/$repo/releases/tag/$tag" -ForegroundColor Green
exit 0
