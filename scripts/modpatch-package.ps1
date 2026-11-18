# Shared validation for the pinned .NET 6 ModPatch compatibility release.
function Get-ModPatchPackage {
    param([Parameter(Mandatory)][string]$Directory)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archivePath = Join-Path $Directory 'SCPSL.ModPatch.dotnet6.0.zip'
    function Assert-ArchiveChecksum([string]$Path) {
        $expected = ((Get-Content -LiteralPath ($Path + '.sha256') -Raw) -split '\s+')[0]
        if ($expected -notmatch '^[0-9a-fA-F]{64}$' -or
            (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash -ine $expected) {
            throw "ModPatch archive failed its checksum: $Path"
        }
    }
    Assert-ArchiveChecksum $archivePath
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = [Collections.Generic.Dictionary[string, IO.Compression.ZipArchiveEntry]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $zip.Entries) {
            $path = $entry.FullName
            if (-not $path -or $path.StartsWith('/') -or $path -match '[:\\]' -or
                $path -match '(^|/)\.\.?(/|$)' -or $path.Contains('//') -or
                -not $entries.TryAdd($path, $entry)) {
                throw "ModPatch archive has an unsafe or duplicate path: $path"
            }
        }
        if (-not $entries.ContainsKey('bundle.json')) { throw 'ModPatch bundle.json is missing.' }
        $reader = [IO.StreamReader]::new($entries['bundle.json'].Open())
        try { $identity = $reader.ReadToEnd() | ConvertFrom-Json }
        finally { $reader.Dispose() }
        if ($identity.schemaVersion -ne 2 -or $identity.sourceRepository -cne 'AnomalyLabsIO/SCPSL-ModPatch' -or
            $identity.sourceRevision -notmatch '^[0-9a-fA-F]{40}$' -or
            $identity.sourceSha256 -notmatch '^[0-9a-fA-F]{64}$' -or
            $identity.targetFramework -cne 'net6.0-windows' -or $identity.runtimeIdentifier -cne 'win-x64' -or
            $identity.runtimeFrameworkVersion -cne '6.0.36' -or
            $identity.singleFile -isnot [bool] -or -not $identity.singleFile -or
            $identity.selfContained -isnot [bool] -or $identity.selfContained -or
            $identity.entryPoint -cne 'SCPSL ModPatch.exe' -or 'CLI' -cnotin $identity.modes -or
            'bundle-version-correction' -cnotin $identity.capabilities) {
            throw 'Expected the unified Windows x64 .NET 6 ModPatch compatibility bundle with bundle-version correction.'
        }
        $sourceName = 'SCPSL.ModPatch.sources.' + $identity.sourceSha256.Substring(0, 12).ToLowerInvariant() + '.zip'
        if ($identity.sourceArchive -cne $sourceName) { throw 'ModPatch source archive name does not match its identity.' }
        $sourcePath = Join-Path $Directory $sourceName
        Assert-ArchiveChecksum $sourcePath
        if ((Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash -ine $identity.sourceSha256) {
            throw 'ModPatch binary and source package identities disagree.'
        }
        $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($file in $identity.files) {
            if (-not $file.relativePath -or $file.relativePath -ieq 'bundle.json' -or
                -not $paths.Add($file.relativePath) -or -not $entries.ContainsKey($file.relativePath) -or
                $file.sha256 -notmatch '^[0-9a-fA-F]{64}$') {
                throw 'ModPatch bundle inventory is incomplete or invalid.'
            }
            $stream = $entries[$file.relativePath].Open()
            $hasher = [Security.Cryptography.SHA256]::Create()
            try { $actual = [Convert]::ToHexString($hasher.ComputeHash($stream)) }
            finally { $hasher.Dispose(); $stream.Dispose() }
            if ($actual -ine $file.sha256) { throw "ModPatch bundled file failed verification: $($file.relativePath)" }
        }
        if (-not $paths.Contains('SCPSL ModPatch.exe') -or -not $paths.Contains('patchinfo.json') -or
            $entries.Count -ne $paths.Count + 1) {
            throw 'ModPatch archive does not match its file inventory.'
        }
        [pscustomobject]@{ ArchivePath = $archivePath; SourcePath = $sourcePath; Identity = $identity }
    }
    finally { $zip.Dispose() }
}
