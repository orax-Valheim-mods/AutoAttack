<#
.SYNOPSIS
    Publishes and deploys the built mod artifact.
.DESCRIPTION
    Invoked by post-build.ps1 after every build. In Debug mode it copies the DLL,
    PDB and MDB to the Valheim BepInEx plugins folder. In Release mode it packages
    the DLL together with README.md and CHANGELOG.md into a ZIP archive under
    the .RELEASE directory.
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

    [Parameter(Mandatory)]
    [System.String]$ReleasePath,

    [Parameter(Mandatory)]
    [System.String]$ZipPath,

    [Parameter(Mandatory)]
    [System.String]$ChangelogPath,

    [Parameter(Mandatory)]
    [System.String]$ReadmePath,

    [System.String]$DeployPath
)

Set-StrictMode -Version Latest

# Stop the script when a cmdlet or a native command fails.
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

# Set the working directory to the script location.
Push-Location -LiteralPath $PSScriptRoot

# Test some preliminaries
("$TargetPath",
    "$ValheimPath",
    "$(Get-Location)\..\libraries"
) | ForEach-Object {
    if (!(Test-Path "$_")) {Write-Error -ErrorAction Stop -Message "$_ folder is missing"}
}

# Remove the target assembly extension using the literal string method.
$name = $TargetAssembly.Replace('.dll', '')

# Create the mdb file.
$pdb = "$TargetPath\$name.pdb"
if (Test-Path -Path $pdb) {
    Write-Host "Create mdb file for plugin $name."

    $ToolPath = "$(Get-Location)\..\libraries\Debug\pdb2mdb.exe"
    $AssemblyPath = "$TargetPath\$TargetAssembly"

    & $ToolPath $AssemblyPath
}

# Main Script
Write-Host "Publishing for $Target from $TargetPath"

if ($Target.Equals("Debug")) {
    if ([string]::IsNullOrEmpty($DeployPath)) {
        $DeployPath = "$ValheimPath\BepInEx\plugins"
    }

    $plug = New-Item -Type Directory -Path "$DeployPath\$name" -Force
    Write-Host "Copy $TargetAssembly to $plug"
    Copy-Item -Path "$TargetPath\$name.dll" -Destination "$plug" -Force
    Copy-Item -Path "$TargetPath\$name.pdb" -Destination "$plug" -Force
    Copy-Item -Path "$TargetPath\$name.dll.mdb" -Destination "$plug" -Force
}

if ($Target.Equals("Release")) {
    Write-Host "Packaging for ThunderStore..."
    $Package="Package"
    $PackagePath="$ProjectPath\$Package"

    Write-Host "$PackagePath\$TargetAssembly"
    New-Item -Type Directory -Path "$PackagePath\plugins" -Force
    Copy-Item -Path "$TargetPath\$TargetAssembly" -Destination "$PackagePath\plugins\$TargetAssembly" -Force
    # README and CHANGELOG live at the repository root only; both are copied
    # into Package/ so the zip ships them (Nexus extracts CHANGELOG.md from it).
    Copy-Item -Path $ReadmePath -Destination "$PackagePath\README.md" -Force
    Copy-Item -Path $ChangelogPath -Destination "$PackagePath\CHANGELOG.md" -Force
    New-Item -ItemType Directory -Path $ReleasePath -Force | Out-Null
    Compress-Archive -Path "$PackagePath\*" -DestinationPath $ZipPath -Force
    Write-Host "Archive written to $ZipPath"
}

Pop-Location
