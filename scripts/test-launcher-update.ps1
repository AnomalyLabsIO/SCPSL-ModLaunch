[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$LauncherPath,
    [Parameter(Mandatory)][string]$UpdatedLauncherPath
)

# Native Windows qualification using disposable synthetic game directories only.
# Build the second EXE with package.ps1 -ReleaseVersion and retain it outside artifacts/release.
$ErrorActionPreference = 'Stop'
if (-not [OperatingSystem]::IsWindows()) { throw 'This packaged-launcher check requires Windows.' }
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$launcher = (Resolve-Path -LiteralPath $LauncherPath).Path
$updated = (Resolve-Path -LiteralPath $UpdatedLauncherPath).Path
$originalVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($launcher).FileVersion
$updatedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($updated).FileVersion
if ([version]$updatedVersion -le [version]$originalVersion) { throw 'The test update must have a newer embedded version.' }
$updatedHash = (Get-FileHash -LiteralPath $updated -Algorithm SHA256).Hash
$evidence = Join-Path $projectRoot ('artifacts/qualification/UPD-' + (Get-Date -Format 'yyyyMMdd-HHmmss') + '-' + [guid]::NewGuid().ToString('N').Substring(0, 6))
New-Item -ItemType Directory -Path $evidence | Out-Null

function Assert-Check([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw $Message }
}

function New-Fixture([string]$Name, [bool]$Installed) {
    $root = Join-Path $evidence $Name
    $private = Join-Path $root '.modlaunch'
    New-Item -ItemType Directory -Path $private -Force | Out-Null
    [IO.File]::WriteAllText((Join-Path $root 'SCPSL.exe'), 'original game sentinel - never executable')
    $exe = Join-Path $(if ($Installed) { $private } else { $root }) 'SCPSL-ModLaunch.exe'
    Copy-Item -LiteralPath $launcher -Destination $exe
    return @{ Root = $root; Exe = $exe; Private = $private }
}

function Stage-Update($Fixture) {
    $directory = Join-Path $Fixture.Private 'updates'
    New-Item -ItemType Directory -Path $directory -Force | Out-Null
    Copy-Item -LiteralPath $updated -Destination (Join-Path $directory "launcher-$updatedHash.bin")
    @{ schemaVersion = 1; version = $updatedVersion; sha256 = $updatedHash } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $directory 'pending.json') -Encoding utf8NoBOM
}

function Invoke-Launcher($Fixture, [string[]]$LaunchArgs, [string]$Name) {
    $start = [Diagnostics.ProcessStartInfo]::new($Fixture.Exe)
    $start.WorkingDirectory = $Fixture.Root
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    $start.Environment['MODLAUNCH_NO_DIALOG'] = '1'
    foreach ($argument in $LaunchArgs) { $start.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(50000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw 'Synthetic launcher invocation timed out.'
        }
        $output = $stdout.GetAwaiter().GetResult() + $stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $Fixture.Root "$Name.log"), $output)
        return @{ ExitCode = $process.ExitCode; Output = $output }
    }
    finally { $process.Dispose() }
}

function Assert-Installed($Fixture) {
    $installed = Join-Path $Fixture.Private 'SCPSL-ModLaunch.exe'
    Assert-Check ((Get-FileHash -LiteralPath $installed -Algorithm SHA256).Hash -eq $updatedHash) 'Updated EXE hash mismatch.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Fixture.Root 'SCPSL-ModLaunch.exe'))) 'Unexpected root launcher remains.'
    Assert-Check (-not (Test-Path -LiteralPath (Join-Path $Fixture.Private 'updates/pending.json'))) 'Pending launcher update remains.'
    Assert-Check ((Get-Content -LiteralPath (Join-Path $Fixture.Root 'SCPSL.exe') -Raw) -eq 'original game sentinel - never executable') 'Game sentinel changed.'
    $owner = Get-Content -LiteralPath (Join-Path $Fixture.Private 'launcher-installation.json') -Raw | ConvertFrom-Json
    Assert-Check ($owner.executableSha256 -eq $updatedHash) 'Launcher ownership was not updated.'
    $shell = New-Object -ComObject WScript.Shell
    try {
        $link = $shell.CreateShortcut((Join-Path $Fixture.Root 'SCPSL-ModLaunch.lnk'))
        try {
            Assert-Check ($link.TargetPath -eq $installed) 'Shortcut target mismatch.'
            Assert-Check ($link.WorkingDirectory -eq $Fixture.Root) 'Shortcut working directory mismatch.'
        }
        finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
}

$first = New-Fixture 'first-install' $false
Stage-Update $first
$result = Invoke-Launcher $first @('--modlaunch-recover-only') 'update-and-install'
Assert-Check ($result.ExitCode -eq 0) "First-install update failed: $($result.Output)"
Assert-Check ($result.Output.Contains('Installed SCPSL-ModLaunch')) 'Staged update was not applied.'
Assert-Installed $first

$installed = New-Fixture 'installed' $true
$result = Invoke-Launcher $installed @('--modlaunch-recover-only') 'initial-install'
Assert-Check ($result.ExitCode -eq 0) "Installed fixture setup failed: $($result.Output)"
$shortcutPath = Join-Path $installed.Root 'SCPSL-ModLaunch.lnk'
$shell = New-Object -ComObject WScript.Shell
try {
    $link = $shell.CreateShortcut($shortcutPath)
    try {
        $link.Arguments = '-noauth "two words"'
        $link.Description = 'Retain these user settings through the update.'
        $link.Save()
    }
    finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($link) }
}
finally { [void][Runtime.InteropServices.Marshal]::FinalReleaseComObject($shell) }
$shortcutHash = (Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash
Stage-Update $installed
$result = Invoke-Launcher $installed @('--modlaunch-recover-only') 'installed-update'
Assert-Check ($result.ExitCode -eq 0) "Installed update failed: $($result.Output)"
Assert-Installed $installed
Assert-Check ((Get-FileHash -LiteralPath $shortcutPath -Algorithm SHA256).Hash -eq $shortcutHash) 'Custom shortcut bytes changed.'

$invalid = New-Fixture 'mismatched-version' $true
Stage-Update $invalid
$pendingPath = Join-Path $invalid.Private 'updates/pending.json'
$pending = Get-Content -LiteralPath $pendingPath -Raw | ConvertFrom-Json
$pending.version = '65535.65535.65535.65535'
$pending | ConvertTo-Json | Set-Content -LiteralPath $pendingPath -Encoding utf8NoBOM
$result = Invoke-Launcher $invalid @('--modlaunch-recover-only') 'rejected-update'
Assert-Check ($result.ExitCode -eq 0) 'Mismatched update blocked normal recovery-only use.'
Assert-Check ($result.Output.Contains('identity/version does not match')) 'Mismatched executable version was not rejected.'
Assert-Check ((Get-FileHash -LiteralPath $invalid.Exe -Algorithm SHA256).Hash -eq
    (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash) 'Mismatched update replaced the launcher.'

$broken = New-Fixture 'failed-preparation' $false
[IO.File]::WriteAllText((Join-Path $broken.Private 'settings.json'), 'invalid settings JSON')
$result = Invoke-Launcher $broken @() 'failed-preparation'
Assert-Check ($result.ExitCode -eq 1) 'Expected the malformed settings to fail preparation.'
Assert-Check ($result.Output.Contains('Checking for a newer SCPSL-ModLaunch release')) 'Failed preparation skipped the update check.'
Assert-Check ($result.Output -match 'No launcher release|Launcher update check skipped|latest.*release|is downloaded|already current') 'Expected a nonfatal update result on failed preparation.'

@{
    originalVersion = $originalVersion; updatedVersion = $updatedVersion
    originalSha256 = (Get-FileHash -LiteralPath $launcher -Algorithm SHA256).Hash
    updatedSha256 = $updatedHash
    firstInstallUpdate = 'passed'; installedUpdate = 'passed'; customShortcutPreserved = 'passed'
    mismatchedVersionRejected = 'passed'; checkAfterFailedPreparation = 'passed'; fixtureRoot = $evidence
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $evidence 'result.json') -Encoding utf8NoBOM
Write-Output "Native launcher update checks passed. Evidence: $evidence"
