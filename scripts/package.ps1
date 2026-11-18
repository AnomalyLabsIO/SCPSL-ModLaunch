#Requires -Version 7.0
[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')][string]$Configuration = 'Release',
    [switch]$SkipTests,
    [string]$NuGetSource,
    [string]$ReleaseVersion,
    [string]$NativeBootstrapPath
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'modpatch-package.ps1')
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$artifactRoot = Join-Path $projectRoot 'artifacts'
$stageRoot = Join-Path $artifactRoot ('staging/' + [guid]::NewGuid().ToString('N'))
$adapterStage = Join-Path $stageRoot 'adapter'
$launcherStage = Join-Path $stageRoot 'launcher'
$releaseStage = Join-Path $stageRoot 'release'
$releaseRoot = Join-Path $artifactRoot 'release'
$bundlePath = Join-Path $artifactRoot 'adapter-bundled.zip'
$nativeBundlePath = Join-Path $stageRoot 'native-bootstrap-pr13.zip'
$nativeProvenanceDirectory = Join-Path $projectRoot 'vendor/native-bootstrap-pr13'
$nativeProvenancePath = Join-Path $nativeProvenanceDirectory 'provenance.json'
$nativeExpectedHash = '18B6DA09BB8976F0036AEAE428F21D95E2C222420A17C8B985AF4B6D59551BF0'
$nativeExpectedLength = 11058688
if (-not $NativeBootstrapPath) {
    $NativeBootstrapPath = Join-Path $nativeProvenanceDirectory 'version.dll'
}
$NativeBootstrapPath = [IO.Path]::GetFullPath($NativeBootstrapPath)
if (-not (Test-Path -LiteralPath $NativeBootstrapPath -PathType Leaf)) { throw 'The qualified PR13 native bootstrap is missing. Pass -NativeBootstrapPath with the pinned binary.' }
if ((Get-Item -LiteralPath $NativeBootstrapPath).Length -ne $nativeExpectedLength -or
    (Get-FileHash -LiteralPath $NativeBootstrapPath -Algorithm SHA256).Hash -ne $nativeExpectedHash) {
    throw 'NativeBootstrapPath does not match the qualified PR13 binary identity.'
}
$nativeProvenance = Get-Content -LiteralPath $nativeProvenancePath -Raw | ConvertFrom-Json
if ($nativeProvenance.binary.sha256 -ne $nativeExpectedHash -or $nativeProvenance.binary.length -ne $nativeExpectedLength) {
    throw 'Native bootstrap provenance does not match the packaging pin.'
}


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

function Copy-NativeBootstrapNotices {
    param([string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in @('README.md', 'provenance.json', 'LICENSE-MelonLoader.md', 'NOTICE-MelonLoader.txt', 'LICENSE-Dobby.txt')) {
        Copy-Item -LiteralPath (Join-Path $nativeProvenanceDirectory $name) -Destination $Destination -Force
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
    Copy-NativeBootstrapNotices (Join-Path $adapterStage 'third-party-licenses/native-bootstrap-pr13')

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $modPatchVendor = Join-Path $projectRoot 'vendor/ModPatch'
    $modPatchPackage = Get-ModPatchPackage -Directory $modPatchVendor
    $modPatchArchive = $modPatchPackage.ArchivePath
    $modPatchStage = Join-Path $adapterStage 'ModPatch'
    [IO.Compression.ZipFile]::ExtractToDirectory($modPatchArchive,$modPatchStage)
    $uniqueBundle = Join-Path $stageRoot 'adapter-bundled.zip'
    [IO.Compression.ZipFile]::CreateFromDirectory($adapterStage, $uniqueBundle, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $uniqueBundle -Destination $bundlePath -Force
    # Keep the payload an ordinary deterministic one-entry ZIP; the runtime validates the DLL pin.
    $nativeStream = [IO.File]::Open($nativeBundlePath, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write)
    try {
        $nativeArchive = [IO.Compression.ZipArchive]::new($nativeStream, [IO.Compression.ZipArchiveMode]::Create, $true)
        try {
            $nativeEntry = $nativeArchive.CreateEntry('version.dll', [IO.Compression.CompressionLevel]::Optimal)
            $nativeEntry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
            $nativeEntry.ExternalAttributes = 0
            $nativeInput = [IO.File]::OpenRead($NativeBootstrapPath)
            $nativeOutput = $nativeEntry.Open()
            try { $nativeInput.CopyTo($nativeOutput) }
            finally { $nativeOutput.Dispose(); $nativeInput.Dispose() }
        } finally { $nativeArchive.Dispose() }
    } finally { $nativeStream.Dispose() }
    $nativeCheck = [IO.Compression.ZipFile]::OpenRead($nativeBundlePath)
    try {
        if ($nativeCheck.Entries.Count -ne 1 -or $nativeCheck.Entries[0].FullName -cne 'version.dll' -or $nativeCheck.Entries[0].Length -ne $nativeExpectedLength) {
            throw 'The native bootstrap resource ZIP has an unexpected layout.'
        }
        $nativeEntryStream = $nativeCheck.Entries[0].Open()
        $nativeHasher = [Security.Cryptography.SHA256]::Create()
        try { $nativeArchivedHash = [Convert]::ToHexString($nativeHasher.ComputeHash($nativeEntryStream)) }
        finally { $nativeHasher.Dispose(); $nativeEntryStream.Dispose() }
        if ($nativeArchivedHash -ne $nativeExpectedHash) { throw 'The native resource ZIP changed the qualified DLL bytes.' }
    } finally { $nativeCheck.Dispose() }
    Copy-Item -LiteralPath $nativeBundlePath -Destination (Join-Path $artifactRoot 'native-bootstrap-pr13.zip') -Force
    Invoke-DotNet (@('publish', 'src/SCPSL.ModLaunch/SCPSL.ModLaunch.csproj', '-o', $launcherStage, "-p:AdapterBundlePath=$bundlePath", "-p:NativeBootstrapBundlePath=$nativeBundlePath") + $publishOptions)
    $launcherExe = Join-Path $launcherStage 'SCPSL-ModLaunch.exe'
    if (-not (Test-Path -LiteralPath $launcherExe)) { throw 'The launcher publish did not produce SCPSL-ModLaunch.exe.' }
    if ($ReleaseVersion) {
        $actualVersion = [version][Diagnostics.FileVersionInfo]::GetVersionInfo($launcherExe).FileVersion
        $expectedVersion = [version]::new($parsedVersion.Major, $parsedVersion.Minor, $parsedVersion.Build, [Math]::Max(0, $parsedVersion.Revision))
        if ($actualVersion -ne $expectedVersion) { throw "Published EXE version $actualVersion does not match $ReleaseVersion." }
    }
    Copy-Item -LiteralPath $launcherExe -Destination $releaseStage -Force
    $launcherHash = (Get-FileHash -LiteralPath $launcherExe -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseStage 'SCPSL-ModLaunch.exe.sha256'), "$launcherHash  SCPSL-ModLaunch.exe`n", [Text.UTF8Encoding]::new($false))
    foreach ($name in @('README.md', 'LICENSE', 'THIRD_PARTY_NOTICES.md')) {
        Copy-Item -LiteralPath (Join-Path $projectRoot $name) -Destination $releaseStage -Force
    }
    $releaseNotices = Join-Path $releaseStage 'third-party-licenses'
    New-Item -ItemType Directory -Path $releaseNotices -Force | Out-Null
    Get-ChildItem -LiteralPath (Join-Path $projectRoot 'third-party-licenses') -File | Copy-Item -Destination $releaseNotices -Force
    Copy-NativeBootstrapNotices (Join-Path $releaseNotices 'native-bootstrap-pr13')
    Copy-Item -LiteralPath (Join-Path $adapterStage 'ThirdParty/Il2CppInterop') -Destination (Join-Path $releaseNotices 'Il2CppInterop') -Recurse

    # Use the verified package's notices so dependency updates cannot retain stale vendor copies.
    Copy-Item -LiteralPath (Join-Path $modPatchStage 'licenses') -Destination (Join-Path $releaseNotices 'ModPatch') -Recurse
    Copy-Item -LiteralPath (Join-Path $projectRoot 'vendor/ModPatch/PROVENANCE.md') -Destination (Join-Path $releaseNotices 'ModPatch-PROVENANCE.md') -Force
    $docsPath = Join-Path $projectRoot 'docs'
    if (Test-Path -LiteralPath $docsPath -PathType Container) {
        $releaseDocs = Join-Path $releaseStage 'docs'
        New-Item -ItemType Directory -Path $releaseDocs -Force | Out-Null
        Get-ChildItem -LiteralPath $docsPath -Filter '*.md' -File | Copy-Item -Destination $releaseDocs -Force
    }
    # The full download includes documentation and notices; updates use the separate EXE.
    $distributionName = 'SCPSL-ModLaunch-win-x64.zip'
    $distribution = Join-Path $stageRoot $distributionName
    [IO.Compression.ZipFile]::CreateFromDirectory($releaseStage, $distribution, [IO.Compression.CompressionLevel]::Optimal, $false)
    Copy-Item -LiteralPath $distribution -Destination $releaseStage
    $distributionHash = (Get-FileHash -LiteralPath $distribution -Algorithm SHA256).Hash.ToLowerInvariant()
    [IO.File]::WriteAllText((Join-Path $releaseStage ($distributionName + '.sha256')), "$distributionHash  $distributionName`n", [Text.UTF8Encoding]::new($false))
    # Remove standalone companion and source files left by older packaging runs.
    Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object {
        $_.Name -like 'SCPSL-ModPatch.Cli-*.zip*' -or
        $_.Name -in @('SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip', 'SCPSL-ModLaunch.ModPatch.Headless-win-x64.zip.sha256') -or
        $_.Name -like 'SCPSL.ModPatch.sources.*.zip' -or
        $_.Name -like 'SCPSL.ModPatch.sources.*.zip.sha256'
    } | Remove-Item -Force
    Get-ChildItem -LiteralPath $releaseStage | Copy-Item -Destination $releaseRoot -Recurse -Force
    Write-Host "Release artifacts: $releaseRoot"
}
finally { Pop-Location }
