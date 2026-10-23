[CmdletBinding()]
param([string]$SourceRoot = (Join-Path $PSScriptRoot '../../SCPSL-ModPatch'), [string]$NuGetSource)
$ErrorActionPreference = 'Stop'
$SourceRoot = (Resolve-Path -LiteralPath $SourceRoot).Path
$packageScript = Join-Path $SourceRoot 'scripts/package-cli.ps1'
& $packageScript -NuGetSource $NuGetSource
$destination = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../vendor/ModPatch'))
foreach ($name in @('SCPSL-ModPatch.Cli-win-x64.zip','SCPSL-ModPatch.Cli-sources.zip')) {
    $source = Join-Path $SourceRoot ('artifacts/cli-release/'+$name)
    $expected = ((Get-Content -LiteralPath ($source+'.sha256') -Raw) -split '\s+')[0]
    if ((Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash -ne $expected) { throw "Invalid CLI package checksum: $name" }
    Copy-Item -LiteralPath $source,($source+'.sha256') -Destination $destination -Force
}
Write-Host 'Imported the fork CLI, pattern resources, and exact source archive. Normal packaging needs no sibling checkout.'
