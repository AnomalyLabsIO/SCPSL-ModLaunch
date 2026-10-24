#Requires -Version 7.0
[CmdletBinding()]
param(
    [string]$SourceRoot = (Join-Path $PSScriptRoot '../../SCPSL-ModPatch'),
    [string]$NuGetSource,
    [string]$PackageDirectory
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'modpatch-package.ps1')
if (-not $PackageDirectory) {
    $SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
    $packageScript = Join-Path $SourceRoot 'scripts/package.ps1'
    & $packageScript -Package Compatibility -NuGetSource $NuGetSource
    $PackageDirectory = Join-Path $SourceRoot 'artifacts/release'
}
$package = Get-ModPatchPackage -Directory (Resolve-Path -LiteralPath $PackageDirectory).Path
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../vendor/ModPatch'))
foreach ($source in @($package.ArchivePath, $package.SourcePath)) {
    Copy-Item -LiteralPath $source,($source+'.sha256') -Destination $destination -Force
}
# Remove only superseded package archives after the new pair passes verification.
Get-ChildItem -LiteralPath $destination -File | Where-Object {
    $_.Name -like 'SCPSL-ModPatch.Cli-*.zip*' -or
    ($_.Name -like 'SCPSL.ModPatch.sources.*.zip*' -and
        $_.Name -ne [IO.Path]::GetFileName($package.SourcePath) -and
        $_.Name -ne ([IO.Path]::GetFileName($package.SourcePath) + '.sha256'))
} | Remove-Item -Force
Write-Host 'Imported the unified .NET 6 ModPatch program, resources, and exact source archive. Normal packaging needs no sibling checkout.'
