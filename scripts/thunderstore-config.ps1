<#
.SYNOPSIS
    Generates the thunderstore.toml that tcli reads when publishing to Thunderstore.

.DESCRIPTION
    The workflow .github/workflows/upload-thunderstore.yml runs this script when a
    GitHub release is published. Everything is derived from Package/manifest.json,
    which stays the single source of truth for the package identity:

      - name, description, website_url and dependencies come from the manifest ;
      - namespace and community come from repository variables (THUNDERSTORE_NAMESPACE,
        THUNDERSTORE_COMMUNITY) ;
      - the version comes from the release tag ("v0.1.0" -> "0.1.0").

    When -ZipPath is given, the manifest inside the release zip is compared with the
    repository manifest and with -Version, so a stale or broken asset can never be
    uploaded.

.PARAMETER Namespace
    Thunderstore team the package is published to (repository variable
    THUNDERSTORE_NAMESPACE), e.g. "orax".

.PARAMETER Community
    Thunderstore community slug (repository variable THUNDERSTORE_COMMUNITY),
    e.g. "valheim".

.PARAMETER Version
    Version to publish, x.y.z (the release tag without its leading "v").

.PARAMETER ManifestPath
    Manifest of the package. When omitted, the script looks for a
    <something>/Package/manifest.json in the repository (there is normally only one).

.PARAMETER ZipPath
    Optional release asset to cross-check (name and version must match the manifest).

.PARAMETER Repository
    Thunderstore instance, defaults to https://thunderstore.io.

.PARAMETER OutFile
    Where to write the configuration, defaults to ./thunderstore.toml.

.PARAMETER ContainsNsfwContent
    Sets containsNsfwContent = true (off by default).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $Namespace,

    [Parameter(Mandatory)]
    [string] $Community,

    [Parameter(Mandatory)]
    [ValidatePattern('^\d+\.\d+\.\d+$')]
    [string] $Version,

    [string] $ManifestPath = '',

    [string] $ZipPath = '',

    [string] $Repository = 'https://thunderstore.io',

    [string] $OutFile = 'thunderstore.toml',

    [switch] $ContainsNsfwContent
)

$ErrorActionPreference = 'Stop'

function Fail([string]$Message) {
    Write-Host "ERROR: $Message" -ForegroundColor Red
    exit 1
}

# --- Locate the package manifest when it was not given -----------------------
if (-not $ManifestPath) {
    $repoRoot = Split-Path -Parent $PSScriptRoot
    $candidates = @(Get-ChildItem -Path $repoRoot -Recurse -File -Filter 'manifest.json' -Depth 4 -ErrorAction SilentlyContinue |
        Where-Object { $_.Directory.Name -eq 'Package' })
    if ($candidates.Count -eq 0) { Fail "no 'Package/manifest.json' found under $repoRoot (pass -ManifestPath)." }
    if ($candidates.Count -gt 1) {
        Fail ("found several package manifests: " + (($candidates | ForEach-Object { $_.FullName }) -join ', ') + " (pass -ManifestPath).")
    }
    $ManifestPath = $candidates[0].FullName
    Write-Host "Package manifest: $ManifestPath" -ForegroundColor DarkGray
}

function ConvertTo-TomlString([string]$Value) {
    if ($null -eq $Value) { $Value = '' }
    $Value = $Value -replace '\\', '\\'
    $Value = $Value -replace '"', '\"'
    $Value = $Value -replace "`r`n", '\n' -replace "`n", '\n' -replace "`r", '\n' -replace "`t", '\t'
    return $Value
}

# --- Manifest of the repository ---------------------------------------------
if (-not (Test-Path -LiteralPath $ManifestPath)) { Fail "manifest not found: $ManifestPath" }
try {
    $manifest = Get-Content -LiteralPath $ManifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
} catch {
    Fail "manifest is not readable: $($_.Exception.Message)"
}

$Name        = [string]$manifest.name
$Description = [string]$manifest.description
$ManifestVer = [string]$manifest.version_number
$WebsiteUrl  = [string]$(if ($manifest.PSObject.Properties['website_url']) { $manifest.website_url } else { '' })

if (-not $Name)        { Fail "${ManifestPath}: missing 'name'." }
if (-not $Description) { Fail "${ManifestPath}: missing 'description'." }
if (-not $ManifestVer) { Fail "${ManifestPath}: missing 'version_number'." }

if ($ManifestVer -ne $Version) {
    Fail "version mismatch: the release tag says '$Version' but $ManifestPath says '$ManifestVer'. Bump version_number (and PluginVersion) before releasing."
}

# --- Cross-check the release asset ------------------------------------------
if ($ZipPath) {
    if (-not (Test-Path -LiteralPath $ZipPath)) { Fail "release asset not found: $ZipPath" }
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
    try {
        $archive = [System.IO.Compression.ZipFile]::OpenRead((Get-Item -LiteralPath $ZipPath).FullName)
    } catch {
        Fail "cannot read $ZipPath : $($_.Exception.Message)"
    }
    try {
        $entry = $archive.Entries | Where-Object { $_.FullName -eq 'manifest.json' } | Select-Object -First 1
        if (-not $entry) { Fail "$ZipPath contains no manifest.json at its root - it is not a Thunderstore package." }
        $reader = New-Object System.IO.StreamReader($entry.Open())
        try   { $zipManifest = ($reader.ReadToEnd() | ConvertFrom-Json) }
        finally { $reader.Dispose() }
    } finally {
        $archive.Dispose()
    }

    if ([string]$zipManifest.name -ne $Name) {
        Fail "name mismatch: the zip contains '$($zipManifest.name)' but the repository manifest says '$Name'."
    }
    if ([string]$zipManifest.version_number -ne $Version) {
        Fail "version mismatch: the zip contains '$($zipManifest.version_number)' but the release tag says '$Version'. Rebuild and re-upload the asset."
    }
    Write-Host "OK: $ZipPath contains $Name $Version" -ForegroundColor Green
}

# --- Dependencies ("Namespace-Name-Version" -> "Namespace-Name" = "Version")
$dependencies = @()
if ($manifest.PSObject.Properties['dependencies'] -and $manifest.dependencies) {
    foreach ($dep in $manifest.dependencies) {
        $dep = [string]$dep
        $idx = $dep.LastIndexOf('-')
        if ($idx -lt 1) { Fail "invalid dependency '$dep' (expected Namespace-Name-Version)." }
        $dependencies += ,@($dep.Substring(0, $idx), $dep.Substring($idx + 1))
    }
}

# --- thunderstore.toml --------------------------------------------------------
$lines = @()
$lines += '[config]'
$lines += 'schemaVersion = "0.0.1"'
$lines += ''
$lines += '[package]'
$lines += "namespace = `"$Namespace`""
$lines += "name = `"$Name`""
$lines += "versionNumber = `"$Version`""
$lines += "description = `"$(ConvertTo-TomlString $Description)`""
$lines += "websiteUrl = `"$(ConvertTo-TomlString $WebsiteUrl)`""
$lines += "containsNsfwContent = $(if ($ContainsNsfwContent) { 'true' } else { 'false' })"
if ($dependencies.Count -gt 0) {
    $lines += ''
    $lines += '[package.dependencies]'
    foreach ($d in $dependencies) { $lines += "$($d[0]) = `"$($d[1])`"" }
}
$lines += ''
$lines += '[publish]'
$lines += "repository = `"$Repository`""
$lines += "communities = [ `"$Community`" ]"

$encoding = New-Object System.Text.UTF8Encoding($false)
$outFileFull = if ([System.IO.Path]::IsPathRooted($OutFile)) {
    [System.IO.Path]::GetFullPath($OutFile)
} else {
    Join-Path (Get-Location).Path $OutFile
}
[System.IO.File]::WriteAllText($outFileFull, (($lines -join "`n") + "`n"), $encoding)

Write-Host "Wrote $OutFile : $Namespace/$Name $Version (community '$Community')" -ForegroundColor Green
exit 0
