[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$NuGetSource,
    [string]$ReleaseVersion
)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stageRoot = Join-Path $artifactRoot ('staging/' + [guid]::NewGuid().ToString('N'))
$adapterStage = Join-Path $stageRoot 'adapter'
$launcherStage = Join-Path $stageRoot 'launcher'
$releaseStage = Join-Path $stageRoot 'release'
$releaseRoot = Join-Path $artifactRoot 'release'
$bundlePath = Join-Path $artifactRoot 'adapter-bundled.zip'

# A published tag must describe the actual EXE version, not just its download URL.
if ($ReleaseVersion) {
    if ($ReleaseVersion -cnotmatch '^v?[0-9]+\.[0-9]+\.[0-9]+(?:\.[0-9]+)?$') {
        throw 'ReleaseVersion must be a stable numeric tag such as v0.1.1.'
    }
    $ReleaseVersion = $ReleaseVersion.TrimStart('v')
    $parsedVersion = [version]$ReleaseVersion
    if (@($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, $parsedVersion.Revision).Where({ $_ -gt 65535 }).Count) {
        throw 'Release version components must fit Windows file-version fields (0-65535).'
    }
}

function Invoke-DotNet {
    param([string[]]$DotNetArgs)
    if ($NuGetSource) { $DotNetArgs += @("-p:RestoreSources=$NuGetSource", '-p:NuGetAudit=false') }
    if ($ReleaseVersion) { $DotNetArgs += "-p:Version=$ReleaseVersion" }
    & dotnet @DotNetArgs
    if ($LASTEXITCODE -ne 0) { throw "dotnet failed with exit code $LASTEXITCODE" }
}

New-Item -ItemType Directory -Path $adapterStage, $launcherStage, $releaseStage, $releaseRoot -Force | Out-Null
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
    if ($ReleaseVersion) {
        $actualVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($launcherExe).FileVersion
        $expectedVersion = [version]::new($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, [Math]::Max(0, $parsedVersion.Revision))
        if ($actualVersion -ne $expectedVersion) { throw "Published EXE version $actualVersion does not match $ReleaseVersion." }
    }
    Copy-Item -LiteralPath $launcherExe -Destination $releaseStage -Force
    $adapterName = 'SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip'
    Copy-Item -LiteralPath $bundlePath -Destination (Join-Path $releaseStage $adapterName) -Force
    foreach ($name in @('SCPSL-ModLaunch.exe', $adapterName)) {
        $digest = (Get-FileHash -LiteralPath (Join-Path $releaseStage $name) -Algorithm SHA256).Hash.ToLowerInvariant()
        [IO.File]::WriteAllText((Join-Path $releaseStage ($name + '.sha256')), "$digest  $name`n", [Text.UTF8Encoding]::new($false))
    }
    foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $releaseStage -Force
    }
    $releaseNotices = Join-Path $releaseStage 'third-party-licenses'
    New-Item -ItemType Directory -Path $releaseNotices -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'third-party-licenses') -File | Copy-Item -Destination $releaseNotices -Force
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'vendor/ModPatch') -Filter '*.txt' -File | Copy-Item -Destination $releaseNotices -Force
    Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor/ModPatch/PROVENANCE.md') -Destination (Join-Path $releaseNotices 'ModPatch-PROVENANCE.md') -Force
    $releaseDocs = Join-Path $releaseStage 'docs'
    New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'docs') -Filter '*.md' -File | Copy-Item -Destination $releaseDocs -Force
    # The full download includes documentation and notices; updates use the separate EXE.
    $distributionName = 'SCPSL-ModLaunch-win-x64.zip'
    $distribution = Join-Path $stageRoot $distributionName
    [IO.Compression.ZipFile]::CreateFromDirectory($releaseStage, $distribution, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $distribution -Destination $releaseStage
    $distributionHash = (Get-FileHash -LiteralPath $distribution -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseStage ($distributionName + '.sha256')), "$distributionHash  $distributionName`n", [Text.UTF8Encoding]::new($false))
    Get-ChildItem -LiteralPath $releaseStage | Copy-Item -Destination $releaseRoot -Recurse -Force
    Write-Host "Release artifacts: $releaseRoot"
}
finally { Pop-Location }
