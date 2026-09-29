param(
    [Parameter(Mandatory)]
    [System.String]$ProjectPath
)

# Exit code 1 on any problem so the MSBuild Exec target fails the build.
function Fail([string]$Message) {
    Write-Host "CHANGELOG FAILED: $Message" -ForegroundColor Red
    exit 1
}

# Commits of a range ("v0.1.0..HEAD", a single tag, or the whole history when
# the range is empty), newest first, as objects {Hash, Subject}.
function Get-Commits([string]$Range) {
    $logArgs = @("log", "--no-merges", "--format=%h%x09%s")
    if ($Range) { $logArgs += $Range }
    $lines = git -C $repoRoot @logArgs
    if ($LASTEXITCODE -ne 0) { Fail "git log failed for range '$Range'" }

    $result = @()
    foreach ($line in @($lines)) {
        $i = $line.IndexOf("`t")
        if ($i -lt 0) { continue }
        $result += [PSCustomObject]@{
            Hash    = $line.Substring(0, $i)
            Subject = $line.Substring($i + 1)
        }
    }
    return $result
}

# Splits a commit subject into a Keep-a-Changelog section and the text shown
# to users: "feat(scope): x" -> Added / "x", everything else -> Other.
function Convert-ToEntry([string]$Subject) {
    $text = $Subject
    $type = $null
    if ($Subject -match '^([a-z]+)(?:\([^)]*\))?:\s+(.+)$') {
        $type = $Matches[1]
        $text = $Matches[2]
    }
    if ($type -eq 'feat')     { $section = 'Added' }
    elseif ($type -eq 'fix')  { $section = 'Fixed' }
    elseif ($type -in @('refactor', 'perf', 'style', 'revert')) { $section = 'Changed' }
    else                      { $section = 'Other' }

    return [PSCustomObject]@{ Section = $section; Text = $text }
}

# Markdown block for one version: "## [x.y.z] - date" plus one subsection per
# change type. Returns nothing when the version has no commits.
function Get-VersionLines([string]$Title, [string]$Date, $Commits) {
    if (-not $Commits -or $Commits.Count -eq 0) { return @() }

    $sections = [ordered]@{ Added = @(); Changed = @(); Fixed = @(); Other = @() }
    foreach ($commit in $Commits) {
        $entry = Convert-ToEntry $commit.Subject
        $sections[$entry.Section] += '- ' + $entry.Text + ' (`' + $commit.Hash + '`)'
    }

    $lines = @("## [$Title] - $Date", "")
    foreach ($name in $sections.Keys) {
        if ($sections[$name].Count -gt 0) {
            $lines += "### $name"
            $lines += ""
            $lines += $sections[$name]
            $lines += ""
        }
    }
    return $lines
}

# --- Preconditions ----------------------------------------------------------
$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not (Get-Command git -ErrorAction SilentlyContinue)) { Fail "git not found in PATH" }

$manifestPath = Join-Path $ProjectPath "Package\manifest.json"
if (-not (Test-Path $manifestPath)) { Fail "manifest not found: $manifestPath" }
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version_number
if (-not $version) { Fail "version_number missing in $manifestPath" }

# Tags are created on GitHub when a draft release gets published; fetch them so
# each version only lists its own commits (best effort - also works offline).
git -C $repoRoot fetch --tags --quiet 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "WARNING: git fetch --tags failed - using local tags only" -ForegroundColor Yellow
}

# Tags, newest first: "v0.1.0<TAB>2026-09-29"
$tagLines = @(git -C $repoRoot for-each-ref "--sort=-creatordate" "--format=%(refname:short)%x09%(creatordate:short)" refs/tags)
if ($LASTEXITCODE -ne 0) { Fail "git for-each-ref failed" }
$tags = @()
foreach ($line in $tagLines) {
    $i = $line.IndexOf("`t")
    if ($i -lt 0) { continue }
    $tags += [PSCustomObject]@{
        Name = $line.Substring(0, $i)
        Date = $line.Substring($i + 1)
    }
}

# --- Build the changelog ----------------------------------------------------
# Current manifest version first (commits since the newest tag, or the whole
# history for the first release), then one block per tag, newest first.
$today = Get-Date -Format 'yyyy-MM-dd'
$outputLines = @(
    "# Changelog"
    ""
    "All notable changes to **AutoAttack** are listed here, newest first."
    "Generated from the git history by ``scripts/changelog.ps1`` - do not edit manually."
    ""
)

$currentRange = ""
if ($tags.Count -gt 0) { $currentRange = "$($tags[0].Name)..HEAD" }
$currentCommits = @(Get-Commits $currentRange)
$outputLines += @(Get-VersionLines $version $today $currentCommits)

$total = $currentCommits.Count
for ($t = 0; $t -lt $tags.Count; $t++) {
    $tag = $tags[$t]
    if ($t -lt ($tags.Count - 1)) {
        # Commits strictly between the previous (older) tag and this one.
        $tagRange = "$($tags[$t + 1].Name)..$($tag.Name)"
    }
    else {
        # Oldest tag: everything reachable from it.
        $tagRange = $tag.Name
    }
    $tagCommits = @(Get-Commits $tagRange)
    $total += $tagCommits.Count

    $tagTitle = $tag.Name -replace '^v', ''
    $outputLines += @(Get-VersionLines $tagTitle $tag.Date $tagCommits)
}

# --- Write Package/CHANGELOG.md (picked up by publish.ps1) ------------------
$outputPath = Join-Path $ProjectPath "Package\CHANGELOG.md"
$text = ($outputLines -join "`r`n") + "`r`n"
[System.IO.File]::WriteAllText($outputPath, $text, [System.Text.UTF8Encoding]::new($false))

Write-Host "CHANGELOG.md written for version $version ($total changes) -> $outputPath"
exit 0
