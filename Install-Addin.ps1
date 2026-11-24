param(
    [string]$Configuration = 'Release',
    [string]$DestinationRoot = (Join-Path $env:APPDATA 'Autodesk\Revit\Addins\2024'),
    [switch]$Uninstall
)

$ErrorActionPreference = 'Stop'
$payload = Join-Path $PSScriptRoot 'payload'
if (-not (Test-Path -LiteralPath $payload -PathType Container)) {
    $payload = Join-Path $PSScriptRoot "bin\$Configuration\net48"
}
$installRoot = Join-Path $DestinationRoot 'RevitAutoAnnotate'
$manifest = Join-Path $DestinationRoot 'RevitAutoAnnotate.addin'
$previousAssembly = Join-Path $installRoot 'RevitAutoAnnotate.dll'
$addinId = '7F675D05-65CB-427F-A9DD-764B55AA35D1'
if (Test-Path -LiteralPath $manifest -PathType Leaf) {
    [xml]$previous = Get-Content -LiteralPath $manifest -Raw
    if ($previous.RevitAddIns.AddIn.AddInId -ne $addinId) {
        throw "The manifest is registered to another add-in: $manifest"
    }
    $oldPath = [System.IO.Path]::GetFullPath([string]$previous.RevitAddIns.AddIn.Assembly)
    $rootPrefix = [System.IO.Path]::GetFullPath($installRoot) + [System.IO.Path]::DirectorySeparatorChar
    if ($oldPath.StartsWith($rootPrefix, [System.StringComparison]::OrdinalIgnoreCase) -and
        [System.IO.Path]::GetFileName($oldPath) -eq 'RevitAutoAnnotate.dll') {
        $previousAssembly = $oldPath
    }
}

if ($Uninstall) {
    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
        throw 'Close Revit before removing the add-in.'
    }
    foreach ($file in @($manifest, $previousAssembly)) {
        if (Test-Path -LiteralPath $file -PathType Leaf) { Remove-Item -LiteralPath $file }
    }
    Write-Host 'Add-in removed. The family map was retained.'
    exit 0
}

$sourceAssembly = Join-Path $payload 'RevitAutoAnnotate.dll'
$sourceMap = Join-Path $payload 'FamilyMap.xml'
foreach ($file in @($sourceAssembly, $sourceMap)) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Package file missing: $file" }
}

$version = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($sourceAssembly).FileVersion
if ($version -notmatch '^\d+(\.\d+){3}$') { throw "Invalid assembly version: $version" }
$versionRoot = Join-Path $installRoot $version
$installedAssembly = Join-Path $versionRoot 'RevitAutoAnnotate.dll'
$installedMap = Join-Path $versionRoot 'FamilyMap.xml'
New-Item -ItemType Directory -Path $versionRoot -Force | Out-Null
if (-not (Test-Path -LiteralPath $installedAssembly -PathType Leaf)) {
    Copy-Item -LiteralPath $sourceAssembly -Destination $installedAssembly
} elseif ((Get-FileHash -LiteralPath $installedAssembly).Hash -ne (Get-FileHash -LiteralPath $sourceAssembly).Hash) {
    if (Get-Process -Name Revit -ErrorAction SilentlyContinue) {
        throw 'This version already exists with different content. Close Revit to replace this build.'
    }
    Copy-Item -LiteralPath $sourceAssembly -Destination $installedAssembly -Force
}
if (-not (Test-Path -LiteralPath $installedMap -PathType Leaf)) {
    $previousMap = Join-Path (Split-Path -Parent $previousAssembly) 'FamilyMap.xml'
    if (Test-Path -LiteralPath $previousMap -PathType Leaf) { $sourceMap = $previousMap }
    Copy-Item -LiteralPath $sourceMap -Destination $installedMap
}

$escapedAssembly = [System.Security.SecurityElement]::Escape($installedAssembly)
$manifestContent = @"
<?xml version="1.0" encoding="utf-8" standalone="no"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Revit Floor Plan Dimensions</Name>
    <Assembly>$escapedAssembly</Assembly>
    <AddInId>$addinId</AddInId>
    <FullClassName>RevitAutoAnnotate.AddinApplication</FullClassName>
    <VendorId>CCAA</VendorId>
    <VendorDescription>Revit Floor Plan Dimensions</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
$nextManifest = Join-Path $DestinationRoot ('RevitAutoAnnotate-' + [Guid]::NewGuid().ToString('N') + '.tmp')
try {
    Set-Content -LiteralPath $nextManifest -Value $manifestContent -Encoding UTF8
    if (Test-Path -LiteralPath $manifest -PathType Leaf) {
        # Move with replacement keeps the old active manifest intact until the new one is ready.
        Move-Item -LiteralPath $nextManifest -Destination $manifest -Force
    } else {
        [System.IO.File]::Move($nextManifest, $manifest)
    }
} finally {
    if (Test-Path -LiteralPath $nextManifest -PathType Leaf) { Remove-Item -LiteralPath $nextManifest }
}
Write-Host "Version $version installed for the current Windows user. It loads the next time Revit 2024 starts."
