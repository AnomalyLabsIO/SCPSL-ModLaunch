[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$NuGetSource
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stageRoot = Join-Path $artifactRoot ('staging/' + [guid]::NewGuid().ToString('N'))
$adapterStage = Join-Path $stageRoot 'adapter'
$launcherStage = Join-Path $stageRoot 'launcher'
$releaseRoot = Join-Path $artifactRoot 'release'
$bundlePath = Join-Path $artifactRoot 'adapter-bundled.zip'

function Invoke-DotNet {
    param([string[]]$DotNetArgs)
    if ($NuGetSource) { $DotNetArgs += @("-p:RestoreSources=$NuGetSource", '-p:NuGetAudit=false') }
    & dotnet @DotNetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Path $adapterStage, $launcherStage, $releaseRoot -Force | Out-Null
Push-Location $projectRoot
try {
    if (-not $SkipTests) {
        Invoke-DotNet @('test', 'tests/SCPSL.ModLaunch.Tests/SCPSL.ModLaunch.Tests.csproj', '-c', $Configuration)
    }
    $publishOptions = @('-c', $Configuration, '-r', 'win-x64', '--self-contained', 'false',
        '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=false',
        '-p:IncludeNativeLibrariesForSelfExtract=true', '-p:DebugType=None', '-p:DebugSymbols=false')
    Invoke-DotNet (@('publish', 'src/SCPSL.ModLaunch.ModPatch.Headless/SCPSL.ModLaunch.ModPatch.Headless.csproj', '-o', $adapterStage) + $publishOptions)
    if (-not (Test-Path -LiteralPath (Join-Path $adapterStage 'SCPSL-ModLaunch.ModPatch.Headless.exe'))) {
        throw 'The adapter publish did not produce SCPSL-ModLaunch.ModPatch.Headless.exe.'
    }
    Copy-Item -LiteralPath (Join-Path $projectRoot 'LICENSE') -Destination $adapterStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'THIRD_PARTY_NOTICES.md') -Destination $adapterStage
    Copy-Item -LiteralPath (Join-Path $projectRoot 'third-party-licenses') -Destination $adapterStage -Recurse
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $uniqueBundle = Join-Path $stageRoot 'adapter-bundled.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($adapterStage, $uniqueBundle, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $uniqueBundle -Destination $bundlePath -Force
    Invoke-DotNet (@('publish', 'src/SCPSL.ModLaunch/SCPSL.ModLaunch.csproj', '-o', $launcherStage, "-p:AdapterBundlePath=$bundlePath") + $publishOptions)
    $launcherExe = Join-Path $launcherStage 'SCPSL-ModLaunch.exe'
    if (-not (Test-Path -LiteralPath $launcherExe)) { throw 'The launcher publish did not produce SCPSL-ModLaunch.exe.' }
    Copy-Item -LiteralPath $launcherExe -Destination $releaseRoot -Force
    $adapterName = 'SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip'
    Copy-Item -LiteralPath $bundlePath -Destination (Join-Path $releaseRoot $adapterName) -Force
    foreach ($name in @('SCPSL-ModLaunch.exe', $adapterName)) {
        $digest = (Get-FileHash -LiteralPath (Join-Path $releaseRoot $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $releaseRoot ($name + '.sha256')), "$digest  $name`n", [Text.UTF8Encoding]::new($false))
    }
    foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $releaseRoot -Force
    }
    $releaseNotices = Join-Path $releaseRoot 'third-party-licenses'
    New-Item -ItemType Directory -Path $releaseNotices -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'third-party-licenses') -File | Copy-Item -Destination $releaseNotices -Force
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'vendor/ModPatch') -Filter '*.txt' -File | Copy-Item -Destination $releaseNotices -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor/ModPatch/PROVENANCE.md') -Destination (Join-Path $releaseNotices 'ModPatch-PROVENANCE.md') -Force
    $releaseDocs = Join-Path $releaseRoot 'docs'
    New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -Filter '*.md' -File | Copy-Item -Destination $releaseDocs -Force
    Write-Host "Release artifacts: $releaseRoot"
}
finally { Pop-Location }
