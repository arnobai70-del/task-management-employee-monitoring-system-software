param(
    [string]$BackupDirectory,
    [string]$PublisherCertificateSha256,
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'TaskMonitoring'),
    [string]$ProgramDataRoot = (Join-Path $env:ProgramData 'TaskMonitoring'),
    [switch]$AllowUnsignedDevelopmentRollback,
    [switch]$ForceCloseDesktop,
    [switch]$KeepAutoUpdateEnabled
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot 'deployment-common.ps1'
if (-not (Test-Path $commonPath -PathType Leaf)) {
    throw 'deployment-common.ps1 must be located beside this rollback script.'
}
. $commonPath

Assert-TaskMonitoringAdministrator

$serviceName = 'TaskMonitoringEmployeeService'
$taskName = 'TaskMonitoring Employee Auto Update'
$backupsRoot = Join-Path $ProgramDataRoot 'backups'
$statePath = Join-Path $ProgramDataRoot 'install-state.json'

if ([string]::IsNullOrWhiteSpace($BackupDirectory)) {
    if (-not (Test-Path $backupsRoot -PathType Container)) {
        throw "No updater backup directory exists: $backupsRoot"
    }

    $candidate = Get-ChildItem -Path $backupsRoot -Directory |
        Where-Object {
            $_.Name -match '^\d+\.\d+\.\d+-\d{14}$' -and
            (Test-Path (Join-Path $_.FullName 'Desktop\TaskMonitoring.EmployeeDesktop.exe') -PathType Leaf) -and
            (Test-Path (Join-Path $_.FullName 'Service\TaskMonitoring.EmployeeService.exe') -PathType Leaf)
        } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $candidate) {
        throw 'No automatic-update backup containing Desktop and Service was found.'
    }
    $BackupDirectory = $candidate.FullName
}
else {
    $BackupDirectory = (Resolve-Path $BackupDirectory).Path
}

$backupDesktop = Join-Path $BackupDirectory 'Desktop'
$backupService = Join-Path $BackupDirectory 'Service'
$backupDesktopExe = Join-Path $backupDesktop 'TaskMonitoring.EmployeeDesktop.exe'
$backupServiceExe = Join-Path $backupService 'TaskMonitoring.EmployeeService.exe'
if (-not (Test-Path $backupDesktopExe -PathType Leaf) -or -not (Test-Path $backupServiceExe -PathType Leaf)) {
    throw 'Rollback backup must contain Desktop and Service application directories.'
}

$fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($backupDesktopExe).FileVersion
$parsedFileVersion = $null
if ([string]::IsNullOrWhiteSpace($fileVersion) -or
    -not [Version]::TryParse(($fileVersion -split '[+-]')[0], [ref]$parsedFileVersion)) {
    throw 'Rollback Desktop executable does not contain valid file-version metadata.'
}
$rollbackVersion = '{0}.{1}.{2}' -f $parsedFileVersion.Major, $parsedFileVersion.Minor, ([Math]::Max(0, $parsedFileVersion.Build))
Assert-TaskMonitoringFileVersion -Path $backupDesktopExe -ExpectedVersion $rollbackVersion
Assert-TaskMonitoringFileVersion -Path $backupServiceExe -ExpectedVersion $rollbackVersion

if (-not $AllowUnsignedDevelopmentRollback) {
    $normalizedPublisherFingerprint = ($PublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalizedPublisherFingerprint.Length -ne 64) {
        throw 'Production rollback requires -PublisherCertificateSha256 with a 64-character SHA-256 certificate fingerprint.'
    }
    Assert-TaskMonitoringAuthenticodeSignature -Path $backupDesktopExe -ExpectedPublisherCertificateSha256 $normalizedPublisherFingerprint
    Assert-TaskMonitoringAuthenticodeSignature -Path $backupServiceExe -ExpectedPublisherCertificateSha256 $normalizedPublisherFingerprint
}
else {
    Write-Warning 'Unsigned development rollback mode is enabled. Do not use this override on production office machines.'
}

$desktopProcesses = @(Get-Process -Name 'TaskMonitoring.EmployeeDesktop' -ErrorAction SilentlyContinue)
if ($desktopProcesses.Count -gt 0) {
    if (-not $ForceCloseDesktop) {
        throw 'Employee Desktop is running. Close it before rollback or use -ForceCloseDesktop during an approved maintenance window.'
    }
    $desktopProcesses | Stop-Process -Force
}

$desktopRoot = Join-Path $InstallRoot 'Desktop'
$serviceRoot = Join-Path $InstallRoot 'Service'
if (-not (Test-Path $desktopRoot -PathType Container) -or -not (Test-Path $serviceRoot -PathType Container)) {
    throw "Current installation is incomplete under $InstallRoot."
}

$safetyRoot = Join-Path $backupsRoot ('rollback-current-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))
$safetyDesktop = Join-Path $safetyRoot 'Desktop'
$safetyService = Join-Path $safetyRoot 'Service'
New-Item -ItemType Directory -Force -Path $safetyRoot | Out-Null

Stop-TaskMonitoringEmployeeService -ServiceName $serviceName
$replacementStarted = $false
try {
    Move-Item -Path $desktopRoot -Destination $safetyDesktop
    Move-Item -Path $serviceRoot -Destination $safetyService
    New-Item -ItemType Directory -Force -Path $desktopRoot, $serviceRoot | Out-Null
    Copy-Item -Path (Join-Path $backupDesktop '*') -Destination $desktopRoot -Recurse -Force
    Copy-Item -Path (Join-Path $backupService '*') -Destination $serviceRoot -Recurse -Force
    $replacementStarted = $true

    Start-TaskMonitoringEmployeeService -ServiceName $serviceName

    $channel = $null
    $installedAtUtc = $null
    if (Test-Path $statePath -PathType Leaf) {
        try {
            $previousState = Get-Content -Raw -Path $statePath | ConvertFrom-Json
            $channel = $previousState.channel
            $installedAtUtc = $previousState.installedAtUtc
        }
        catch {
        }
    }

    $newState = [ordered]@{
        version = $rollbackVersion
        channel = $channel
        installedAtUtc = $installedAtUtc
        lastSuccessfulUpdateUtc = $null
        rolledBackAtUtc = [DateTime]::UtcNow.ToString('O')
    }
    Write-TaskMonitoringJson -Value $newState -Path $statePath

    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskMonitoringEmployee'
    if (Test-Path $uninstallKey) {
        New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value $rollbackVersion -PropertyType String -Force | Out-Null
    }

    if (-not $KeepAutoUpdateEnabled) {
        & schtasks.exe /Change /TN $taskName /DISABLE 2>$null | Out-Null
        Write-Warning 'Automatic update task was disabled after rollback. Re-enable it only after the update channel is corrected or intentionally advanced.'
    }

    Write-Host "Rolled back TaskMonitoring Employee Workspace to $rollbackVersion."
    Write-Host "Pre-rollback files are retained at: $safetyRoot"
}
catch {
    Write-Warning "Rollback activation failed. Restoring the pre-rollback installation: $($_.Exception.Message)"
    try { Stop-TaskMonitoringEmployeeService -ServiceName $serviceName } catch { }

    if ($replacementStarted) {
        Remove-Item -Recurse -Force $desktopRoot, $serviceRoot -ErrorAction SilentlyContinue
    }
    if (Test-Path $safetyDesktop) {
        Move-Item -Path $safetyDesktop -Destination $desktopRoot
    }
    if (Test-Path $safetyService) {
        Move-Item -Path $safetyService -Destination $serviceRoot
    }
    try { Start-TaskMonitoringEmployeeService -ServiceName $serviceName } catch { }
    throw
}
