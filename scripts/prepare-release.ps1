<#
.SYNOPSIS
    Automate the version bumping and file updates for the project.
.DESCRIPTION
    Bumps the version with git-cliff, writes it to VERSION.txt, refreshes the
    changelog, and updates the version number in the plugin source and the
    Thunderstore manifest.
#>

param (
    [Parameter(Mandatory)]
    [System.String]$ProjectName,

    [Parameter(Mandatory)]
    [System.String]$ProjectPath,

    [Parameter(Mandatory)]
    [System.String]$ChangelogPath,

    [Parameter(Mandatory)]
    [System.String]$VersionPath
)

Set-StrictMode -Version Latest

# Stop the script when a cmdlet or a native command fails.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Define the replacement rules used to update version numbers across configuration and source files.
# file: Path to the target file.
# search: Regular expression to search for.
# replacement: Replacement pattern with the new version.
$replacements = @(
    @{
        file = Join-Path $ProjectPath "$ProjectName.cs"
        search = 'public const string PluginVersion = "(.+?)";'
        replacement = 'public const string PluginVersion = "{VERSION}";'
    },
    @{
        file = Join-Path $ProjectPath "Package/manifest.json"
        search = '"version_number": "(.+?)"'
        replacement = '"version_number": "{VERSION}"'
    }
)

# Navigate one directory up from the script location.
Push-Location (Get-Item $PSScriptRoot).Parent.FullName

# Get the next semantic version.
$version = (git-cliff --bumped-version) -replace '^v', ''
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "git-cliff returned an empty version."
}
Write-Output "New version is $version."
Set-Content -Path $VersionPath -Value $version -NoNewline

# Bump the version and update the changelog file.
git-cliff --bump -o $ChangelogPath

# Define placeholders for dynamic substitution.
$placeholders = @{
    VERSION = $version
}

# Iterate through each replacement configuration to update files, notably updating the version.
$utf8NoBom = [System.Text.UTF8Encoding]::new($false)
foreach ($item in $replacements) {
    # Resolve the placeholders in the replacement pattern.
    $resolvedSearch = $item.search
    $resolvedReplacement = $item.replacement

    foreach ($key in $placeholders.Keys) {
        $resolvedReplacement = $resolvedReplacement.Replace("{$key}", $placeholders[$key])
    }

    # Read the file content as a single raw string.
    $content = Get-Content -Path $item.file -Raw

    # Throw if the version pattern is absent from the file.
    if (-not [regex]::IsMatch($content, $resolvedSearch)) {
        throw "Pattern '$resolvedSearch' not found in $($item.file)."
    }
    $newContent = [regex]::Replace($content, $resolvedSearch, $resolvedReplacement)

    # Save the updated content back to the file (UTF-8 without BOM).
    [System.IO.File]::WriteAllText($item.file, $newContent, $utf8NoBom)
}

Pop-Location
