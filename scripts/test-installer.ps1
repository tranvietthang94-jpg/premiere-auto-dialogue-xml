[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$InstallerPath,

    [Parameter(Mandatory)]
    [string]$PayloadManifestPath,

    [Parameter(Mandatory)]
    [string]$ReportPath,

    [string]$TestRoot = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerFullPath = [System.IO.Path]::GetFullPath($InstallerPath)
$payloadManifestFullPath = [System.IO.Path]::GetFullPath($PayloadManifestPath)
$reportFullPath = [System.IO.Path]::GetFullPath($ReportPath)

if (-not (Test-Path -LiteralPath $installerFullPath -PathType Leaf)) {
    throw 'Installer was not found.'
}
if (-not (Test-Path -LiteralPath $payloadManifestFullPath -PathType Leaf)) {
    throw 'Payload manifest was not found.'
}
if (Test-Path -LiteralPath $reportFullPath) {
    throw 'Installer test report already exists; this script does not overwrite.'
}

$scriptDirectory = Split-Path -Parent $MyInvocation.MyCommand.Path
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $scriptDirectory '..'))
if ([string]::IsNullOrWhiteSpace($TestRoot)) {
    $testBase = Join-Path $repositoryRoot 'artifacts\installer-test'
}
elseif ([System.IO.Path]::IsPathRooted($TestRoot)) {
    $testBase = [System.IO.Path]::GetFullPath($TestRoot)
}
else {
    $testBase = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $TestRoot))
}

[System.IO.Directory]::CreateDirectory($testBase) | Out-Null
$testDirectory = Join-Path $testBase ([Guid]::NewGuid().ToString('N'))
$installDirectory = Join-Path $testDirectory 'installed'
$installLog = Join-Path $testDirectory 'install.log'
$uninstallLog = Join-Path $testDirectory 'uninstall.log'
$sentinelName = 'user-output-do-not-delete.txt'
$sentinelPath = Join-Path $installDirectory $sentinelName
$uninstallRegistryPath = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\{99295721-E0F1-4299-85FF-51B020C49600}_is1'

$testBaseFull = [System.IO.Path]::GetFullPath($testBase).TrimEnd('\')
$testDirectoryFull = [System.IO.Path]::GetFullPath($testDirectory)
if (-not $testDirectoryFull.StartsWith("$testBaseFull\", [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Resolved installer test directory is outside the requested test root.'
}
if (Test-Path -LiteralPath $uninstallRegistryPath) {
    throw 'Premiere Auto Dialogue XML is already registered for the current user; refusing to overwrite it during installer testing.'
}

$payloadManifest = Get-Content -LiteralPath $payloadManifestFullPath -Raw | ConvertFrom-Json
$installerSha256 = (Get-FileHash -LiteralPath $installerFullPath -Algorithm SHA256).Hash
$installedAppOpened = $false
$payloadVerified = 0
$uninstallerFile = $null
$sentinelSurvived = $false
$uninstallRegistryKey = $null
$uninstallRegistryRemoved = $false
$installCompleted = $false
$uninstallCompleted = $false

try {
    [System.IO.Directory]::CreateDirectory($testDirectoryFull) | Out-Null
    $installArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        '/SP-',
        '/NOICONS',
        "/DIR=`"$installDirectory`"",
        "/LOG=`"$installLog`""
    )
    $installProcess = Start-Process -FilePath $installerFullPath -ArgumentList $installArguments -Wait -PassThru -WindowStyle Hidden
    if ($installProcess.ExitCode -ne 0) {
        throw "Silent install failed with exit code $($installProcess.ExitCode)."
    }
    $installCompleted = $true

    if (-not (Test-Path -LiteralPath $uninstallRegistryPath)) {
        throw 'Current-user uninstall registry entry was not created.'
    }
    $uninstallProperties = Get-ItemProperty -LiteralPath $uninstallRegistryPath
    $registeredLocation = [System.IO.Path]::GetFullPath(([string]$uninstallProperties.InstallLocation).Trim().Trim('"').TrimEnd('\'))
    if (-not $registeredLocation.Equals($installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Current-user uninstall registry entry points to an unexpected location: $registeredLocation"
    }
    $uninstallRegistryKey = $uninstallRegistryPath

    foreach ($entry in $payloadManifest.files) {
        $installedPath = Join-Path $installDirectory ($entry.path -replace '/', '\')
        if (-not (Test-Path -LiteralPath $installedPath -PathType Leaf)) {
            throw "Installed payload is missing: $($entry.path)"
        }
        $item = Get-Item -LiteralPath $installedPath
        $hash = (Get-FileHash -LiteralPath $installedPath -Algorithm SHA256).Hash
        if ($item.Length -ne [long]$entry.bytes -or $hash -ne $entry.sha256) {
            throw "Installed payload does not match manifest: $($entry.path)"
        }
        $payloadVerified++
    }

    $installedPublishManifest = Join-Path $installDirectory 'publish-manifest.json'
    if (-not (Test-Path -LiteralPath $installedPublishManifest -PathType Leaf)) {
        throw 'Installed publish-manifest.json is missing.'
    }

    $installedExecutable = Join-Path $installDirectory 'PremiereAutoDialogueXml.exe'
    $appProcess = Start-Process -FilePath $installedExecutable -WorkingDirectory $installDirectory -PassThru -WindowStyle Hidden
    try {
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds(20)
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($appProcess.HasExited) {
                throw "Installed app exited early with code $($appProcess.ExitCode)."
            }
            $appProcess.Refresh()
            if ($appProcess.MainWindowHandle -ne 0) {
                $installedAppOpened = $true
                break
            }
            Start-Sleep -Milliseconds 200
        }
        if (-not $installedAppOpened) {
            throw 'Installed app did not open a main window within 20 seconds.'
        }
        [void]$appProcess.CloseMainWindow()
        if (-not $appProcess.WaitForExit(10000)) {
            $appProcess.Kill($true)
            $appProcess.WaitForExit()
        }
    }
    finally {
        if (-not $appProcess.HasExited) {
            $appProcess.Kill($true)
            $appProcess.WaitForExit()
        }
        $appProcess.Dispose()
    }

    'This file simulates user-created output and must survive uninstall.' | Set-Content -LiteralPath $sentinelPath -Encoding utf8
    $uninstallers = @(Get-ChildItem -LiteralPath $installDirectory -File -Filter 'unins*.exe')
    if ($uninstallers.Count -ne 1) {
        throw "Expected exactly one uninstaller; found $($uninstallers.Count)."
    }
    $uninstallerFile = $uninstallers[0].Name
    $uninstallArguments = @(
        '/VERYSILENT',
        '/SUPPRESSMSGBOXES',
        '/NORESTART',
        "/LOG=`"$uninstallLog`""
    )
    $uninstallProcess = Start-Process -FilePath $uninstallers[0].FullName -ArgumentList $uninstallArguments -Wait -PassThru -WindowStyle Hidden
    if ($uninstallProcess.ExitCode -ne 0) {
        throw "Silent uninstall failed with exit code $($uninstallProcess.ExitCode)."
    }

    $cleanupDeadline = [DateTimeOffset]::UtcNow.AddSeconds(15)
    while ([DateTimeOffset]::UtcNow -lt $cleanupDeadline -and (
        (Test-Path -LiteralPath (Join-Path $installDirectory $uninstallerFile)) -or
        (Test-Path -LiteralPath $uninstallRegistryPath))) {
        Start-Sleep -Milliseconds 200
    }

    foreach ($entry in $payloadManifest.files) {
        $installedPath = Join-Path $installDirectory ($entry.path -replace '/', '\')
        if (Test-Path -LiteralPath $installedPath -PathType Leaf) {
            throw "Uninstaller left an installed payload file: $($entry.path)"
        }
    }
    $sentinelSurvived = Test-Path -LiteralPath $sentinelPath -PathType Leaf
    if (-not $sentinelSurvived) {
        throw 'Uninstaller deleted the simulated user-created output.'
    }
    $uninstallRegistryRemoved = -not (Test-Path -LiteralPath $uninstallRegistryKey)
    if (-not $uninstallRegistryRemoved) {
        throw 'Uninstaller left its current-user registry entry behind.'
    }
    $uninstallCompleted = $true

    $reportDirectory = Split-Path -Parent $reportFullPath
    [System.IO.Directory]::CreateDirectory($reportDirectory) | Out-Null
    $report = [ordered]@{
        schemaVersion = '1.0'
        testedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
        installerFile = [System.IO.Path]::GetFileName($installerFullPath)
        installerSha256 = $installerSha256
        silentInstallExitCode = 0
        payloadFilesVerified = $payloadVerified
        installedAppOpened = $installedAppOpened
        silentUninstallExitCode = 0
        uninstallerFile = $uninstallerFile
        installedPayloadRemoved = $true
        userCreatedFileSurvived = $sentinelSurvived
        currentUserUninstallEntryCreated = $true
        currentUserUninstallEntryRemoved = $uninstallRegistryRemoved
        passed = $true
    }
    $report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $reportFullPath -Encoding utf8
    [pscustomobject]$report
}
finally {
    if ($installCompleted -and -not $uninstallCompleted) {
        $recoveryUninstaller = @(Get-ChildItem -LiteralPath $installDirectory -File -Filter 'unins*.exe' -ErrorAction SilentlyContinue | Select-Object -First 1)
        if ($recoveryUninstaller.Count -eq 1) {
            try {
                $recoveryProcess = Start-Process -FilePath $recoveryUninstaller[0].FullName -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART') -Wait -PassThru -WindowStyle Hidden
                $recoveryProcess.Dispose()
            }
            catch {
                Write-Warning "Recovery uninstall failed: $($_.Exception.Message)"
            }
        }

        if (Test-Path -LiteralPath $uninstallRegistryPath) {
            $recoveryProperties = Get-ItemProperty -LiteralPath $uninstallRegistryPath -ErrorAction SilentlyContinue
            $locationProperty = if ($null -ne $recoveryProperties) { $recoveryProperties.PSObject.Properties['InstallLocation'] } else { $null }
            if ($null -ne $locationProperty) {
                $recoveryLocation = [System.IO.Path]::GetFullPath(([string]$locationProperty.Value).Trim().Trim('"').TrimEnd('\'))
                if ($recoveryLocation.Equals($installDirectory.TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
                    Remove-Item -LiteralPath $uninstallRegistryPath -Recurse -Force
                }
            }
        }
    }
    if (Test-Path -LiteralPath $testDirectoryFull -PathType Container) {
        Remove-Item -LiteralPath $testDirectoryFull -Recurse -Force
    }
}
