<#
.SYNOPSIS
    Create (or update) a GitHub draft release for the current version.
.DESCRIPTION
    Reads the version from Package/manifest.json, verifies the build artifact
    in .RELEASE, generates the release notes with git-cliff for the version
    being released, then creates a DRAFT release (or re-uploads the zip if a
    release already exists for that tag).

    Invoked automatically by scripts\post-build.ps1 on every Release build.
    Can also be run manually after a Release build:
        .\scripts\github-release.ps1
    The script commits the version bump files, tags the release, pushes to
    origin, and creates a GitHub draft release.
.NOTES
    Exit code 1 on any problem so callers (or an optional Exec target) fail.
#>
param(
    [Parameter(Mandatory)]
    [System.String]$ProjectName,

    [Parameter(Mandatory)]
    [System.String]$ZipPath,

    [Parameter(Mandatory)]
    [System.String]$VersionPath,

    [Parameter(Mandatory)]
    [System.String]$Branch,

    [Parameter(Mandatory)]
    [System.String[]]$ReleaseFiles,

    [Parameter(Mandatory)]
    [System.String[]]$ReleasePath
)

Set-StrictMode -Version Latest

# Stop the script when a cmdlet or a native command fails.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Exit code 1 on any problem so the caller fails.
function Fail([string]$Message) {
    throw "GITHUB RELEASE FAILED: $Message"
}

# Validate the local release prerequisites and identify the GitHub repository.
function Get-ReleaseContext() {
    $version = Get-Content -Path $VersionPath -Raw

    if (-not (Get-Command git -ErrorAction SilentlyContinue)) {
        Fail "git not found in PATH."
    }
    if (-not (Get-Command gh -ErrorAction SilentlyContinue)) {
        Fail "GitHub CLI (gh) not found - install it first: winget install GitHub.cli."
    }
    if (-not (Get-Command git-cliff -ErrorAction SilentlyContinue)) {
        Fail "`git-cliff` not found in PATH - install: https://git-cliff.org/"
    }

    gh auth status | Out-Null
    if ($LASTEXITCODE -ne 0) { Fail "gh is not authenticated - run: `gh auth login`." }
    if (-not (Test-Path $zipPath)) {
        Fail "Zip not found: $zipPath (run a Release build first)."
    }

    # Get the current git branch name.
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()

    # Check if the current branch matches the expected branch.
    if ($branch -ne $Branch) {
        Fail "Not on a branch '$Branch'."
    }

    $repoUrl = git remote get-url origin
    if ($LASTEXITCODE -ne 0 -or -not $repoUrl) { Fail "no 'origin' remote configured" }
    if ($repoUrl -notmatch 'github\.com[:/]([^/]+)/([^/]+?)(\.git)?$') {
        Fail "`origin` is not a GitHub remote: $repoUrl"
    }
    $repo = "$($Matches[1])/$($Matches[2])"

    gh repo view $repo | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Fail "Repository $repo not found on GitHub (or no access with this gh account)."
    }

    $tag = "v$version"
    gh release view $tag | Out-Null
    if ($LASTEXITCODE -eq 0) {
        Fail "Release $tag already exists on GitHub."
    }

    return @{
        Version = $version
        Repository = $repo
        Tag = $tag
    }
}

$RepoRoot = Split-Path -Parent $PSScriptRoot
Push-Location $RepoRoot

$releaseContext = Get-ReleaseContext
$version = $releaseContext.Version
$repo = $releaseContext.Repository
$tag = $releaseContext.Tag

Set-Content -Path $VersionPath -Value $version -NoNewline

$releaseNotesPath = Join-Path $ReleasePath "releaseNotes.md"
git-cliff --bump --unreleased --output $releaseNotesPath | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail "git-cliff failed (exit $LASTEXITCODE)."
}

Write-Host "Committing release preparation changes."

# Add release files to staging area.
git add $ReleaseFiles | Out-Null
$staged = @(git diff --cached --name-only)
if ($staged.Count -eq 0) {
    Fail "Nothing to commit - working tree already clean."
}

# Commit the release-preparation changes (version bump + changelog)
# produced by prepare-release.ps1 so the tag points at the exact
# commit that carries the new version.
git commit -m "chore(release): bump version to v$version" | Out-Null
if ($LASTEXITCODE -ne 0) {
    Fail "git commit failed."
}

# Push so the tag can be created from this commit when the draft publishes.
Write-Host "Pushing $Branch to origin."
git push origin $Branch
if ($LASTEXITCODE -ne 0) {
    Fail "`git push` failed - pull/rebase first, then rebuild."
}

# Tag the release commit before creating the draft so GitHub attaches it to a
# real tag instead of an untagged-… ref.
Write-Host "Tagging $tag at HEAD."
git tag $tag
if ($LASTEXITCODE -ne 0) {
    Fail "`git tag $tag` failed."
}

git push origin $tag
if ($LASTEXITCODE -ne 0) {
    Fail "`git push origin $tag` failed."
}

# Create the draft.
$ghArgs = @(
    "release", "create", $tag, $zipPath,
    "--title", $tag,
    "--target", $Branch,
    "--draft",
    "--verify-tag",
    "--repo", $repo,
    "--notes-file", $releaseNotesPath
)
Write-Host "Creating DRAFT release $tag on $repo."
& gh @ghArgs
if ($LASTEXITCODE -ne 0) {
    Fail "`gh release $($ghArgs[1])` failed (exit $LASTEXITCODE)."
}

# Open the releases page (a draft has no usable tag URL before publishing).
$listUrl = "https://github.com/$repo/releases"
Write-Host "Draft created: $listUrl" -ForegroundColor Green
Start-Process $listUrl
Write-Host "Updated: $listUrl" -ForegroundColor Green

Pop-Location
