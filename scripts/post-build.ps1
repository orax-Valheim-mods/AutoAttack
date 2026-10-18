<#
.SYNOPSIS
    Orchestrates the post-build automation for the AFK mod.
.DESCRIPTION
    Invoked by MSBuild after the .NET build completes. On Release builds it runs
    prepare-release.ps1 to bump the version, then publish.ps1 to package and deploy
    the artifact, and finally github-release.ps1 to create a GitHub draft release.
#>

param(
    [Parameter(Mandatory)]
    [ValidateSet('Debug','Release')]
    [System.String]$Target,

    [Parameter(Mandatory)]
    [System.String]$TargetPath,

    [Parameter(Mandatory)]
    [System.String]$TargetAssembly,

    [Parameter(Mandatory)]
    [System.String]$ValheimPath,

    [Parameter(Mandatory)]
    [System.String]$ProjectPath,

    [System.String]$DeployPath
)

Set-StrictMode -Version Latest

# Stop the script when a cmdlet or a native command fails.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Log the script execution with its bound parameters.
$boundArgs = ($PSBoundParameters.GetEnumerator() | ForEach-Object { "-$($_.Key) $($_.Value)" }) -join ' '
Write-Output "Execute: $($MyInvocation.MyCommand.Name) $boundArgs"

# Configuration.
$branch = "master"
$repoRoot = Split-Path -Parent $PSScriptRoot
$projectName = Split-Path -Leaf $repoRoot
$releasePath = Join-Path $repoRoot '.RELEASE'
$versionPath = Join-Path $repoRoot 'VERSION.txt'
$changelogPath = Join-Path $repoRoot 'CHANGELOG.md'
$readmePath = Join-Path $repoRoot 'README.md'
$releaseFiles = @(
    'VERSION.txt',
    'CHANGELOG.md',
    (Join-Path $projectName "$projectName.cs"),
    (Join-Path $projectName 'Package/manifest.json')
)
$zipPath = Join-Path $releasePath "$projectName.zip"

if (Test-Path $releasePath) {
    Remove-Item -Path (Join-Path $releasePath '*') -Force
} else {
    New-Item -ItemType Directory -Path $releasePath -Force | Out-Null
}

# Verify that the file no longer exists.
if (Test-Path $zipPath) {
    Write-Error "The release file could not be removed."
    exit 1
}

# Version bump and changelog execution only for release target.
$parameters = @{
    ProjectName = $projectName
    ProjectPath = $ProjectPath
    ChangelogPath = $changelogPath
    VersionPath = $versionPath
}
if ($Target -eq 'Release') {
    $scriptPath = Join-Path $PSScriptRoot 'prepare-release.ps1'
    Write-Output "Prepare release."
    Write-Output "Execute: $scriptPath"
    & $scriptPath @parameters
}

$parameters = @{
    Target = $Target
    TargetPath = $TargetPath
    TargetAssembly = $TargetAssembly
    ValheimPath = $ValheimPath
    DeployPath = $DeployPath
    ReleasePath = $releasePath
    ZipPath = $zipPath
    ChangelogPath = $changelogPath
    ReadmePath = $readmePath
    ProjectPath = $ProjectPath
}
$scriptPath = Join-Path $PSScriptRoot 'publish.ps1'
Write-Output "Publish release."
Write-Output "Execute: $scriptPath"
& $scriptPath @parameters

$parameters = @{
    ProjectName = $projectName
    ZipPath = $zipPath
    VersionPath = $versionPath
    Branch = $branch
    ReleaseFiles = $releaseFiles
    ReleasePath = $releasePath
}
if ($Target -eq 'Release') {
    $scriptPath = Join-Path $PSScriptRoot 'github-release.ps1'
    Write-Output "Create a release on GitHub."
    Write-Output "Execute: $scriptPath"
    & $scriptPath @parameters
}
