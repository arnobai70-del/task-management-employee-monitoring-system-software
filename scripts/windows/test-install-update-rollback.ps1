param(
    [Parameter(Mandatory = $true)][string]$InitialReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$UpdateReleaseDirectory,
    [Parameter(Mandatory = $true)][string]$InstallRoot,
    [Parameter(Mandatory = $true)][string]$ProgramDataRoot,
    [ValidateRange(1024, 65535)][int]$UpdateHostPort = 18765
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$initialRelease = (Resolve-Path $InitialReleaseDirectory).Path
$updateRelease = (Resolve-Path $UpdateReleaseDirectory).Path
$initialManifest = Get-Content -Raw (Join-Path $initialRelease 'release.json') | ConvertFrom-Json
$updateManifest = Get-Content -Raw (Join-Path $updateRelease 'release.json') | ConvertFrom-Json
$initialVersion = [Version]([string]$initialManifest.version)
$updateVersion = [Version]([string]$updateManifest.version)
if ($updateVersion -le $initialVersion) {
    throw 'Update release version must be greater than initial release version.'
}

$testRoot = Join-Path $env:TEMP ('TaskMonitoring-e2e-' + [Guid]::NewGuid().ToString('N'))
$hostRoot = Join-Path $testRoot 'host'
$stableRoot = Join-Path $hostRoot 'stable'
New-Item -ItemType Directory -Force -Path $stableRoot | Out-Null
Copy-Item -Path (Join-Path $updateRelease 'release.json') -Destination (Join-Path $stableRoot 'release.json') -Force
Copy-Item -Path (Join-Path $updateRelease ([string]$updateManifest.package.file)) -Destination $stableRoot -Force

$python = Get-Command python -ErrorAction SilentlyContinue
if ($null -eq $python) {
    $python = Get-Command python3 -ErrorAction Stop
}
$server = Start-Process -FilePath $python.Source -ArgumentList @('-m', 'http.server', $UpdateHostPort, '--bind', '127.0.0.1', '--directory', $hostRoot) -PassThru -WindowStyle Hidden

$serviceName = 'TaskMonitoringEmployeeService'
$taskName = 'TaskMonitoring Employee Auto Update'
$uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskMonitoringEmployee'

function Assert-InstalledVersion([string]$Expected) {
    $statePath = Join-Path $ProgramDataRoot 'install-state.json'
    $state = Get-Content -Raw $statePath | ConvertFrom-Json
    if ([string]$state.version -ne $Expected) {
        throw "Expected installed version $Expected but state contains '$($state.version)'."
    }

    $desktopExe = Join-Path $InstallRoot 'Desktop\TaskMonitoring.EmployeeDesktop.exe'
    $actualVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($desktopExe).FileVersion
    $parsed = [Version](($actualVersion -split '[+-]')[0])
    $normalized = '{0}.{1}.{2}' -f $parsed.Major, $parsed.Minor, ([Math]::Max(0, $parsed.Build))
    if ($normalized -ne $Expected) {
        throw "Expected Desktop file version $Expected but found $normalized."
    }
}

try {
    Start-Sleep -Seconds 2
    $manifestUrl = "http://127.0.0.1:$UpdateHostPort/stable/release.json"
    & (Join-Path $initialRelease 'install-employee-windows.ps1') `
        -ReleaseDirectory $initialRelease `
        -ServerUrl 'http://127.0.0.1:65534' `
        -UpdateManifestUrl $manifestUrl `
        -InstallRoot $InstallRoot `
        -ProgramDataRoot $ProgramDataRoot `
        -AllowUnsignedDevelopmentBuild `
        -AllowHttpForDevelopment `
        -SkipServerHealthCheck `
        -NoPublicDesktopShortcut

    Assert-InstalledVersion -Expected ([string]$initialManifest.version)
    if ((Get-Service -Name $serviceName).Status -ne 'Running') {
        throw 'Employee Service is not running after installation.'
    }
    $desktopSettings = Get-Content -Raw (Join-Path $InstallRoot 'Desktop\desktop-settings.json') | ConvertFrom-Json
    if ($desktopSettings.lockServerUrl -ne $true) {
        throw 'Installer-managed Desktop server URL is not locked.'
    }
    if ($null -eq (Get-ScheduledTask -TaskName $taskName -ErrorAction Stop)) {
        throw 'Automatic-update scheduled task was not created.'
    }

    $updaterExe = Join-Path $ProgramDataRoot 'Updater\TaskMonitoring.EmployeeUpdater.exe'
    & $updaterExe --settings (Join-Path $ProgramDataRoot 'update-settings.json') --force
    if ($LASTEXITCODE -ne 0) {
        throw "Updater exited with code $LASTEXITCODE."
    }
    Assert-InstalledVersion -Expected ([string]$updateManifest.version)

    $backup = Get-ChildItem (Join-Path $ProgramDataRoot 'backups') -Directory |
        Where-Object { $_.Name -match ('^' + [Regex]::Escape([string]$initialManifest.version) + '-\d{14}$') } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $backup) {
        throw 'Updater did not retain the expected previous-version backup.'
    }

    & (Join-Path $updateRelease 'rollback-employee-windows.ps1') `
        -BackupDirectory $backup.FullName `
        -InstallRoot $InstallRoot `
        -ProgramDataRoot $ProgramDataRoot `
        -AllowUnsignedDevelopmentRollback

    Assert-InstalledVersion -Expected ([string]$initialManifest.version)
    if ((Get-Service -Name $serviceName).Status -ne 'Running') {
        throw 'Employee Service is not running after rollback.'
    }
    if ((Get-ScheduledTask -TaskName $taskName).State -ne 'Disabled') {
        throw 'Automatic-update task should be disabled after rollback.'
    }

    & (Join-Path $ProgramDataRoot 'Maintenance\uninstall-employee-windows.ps1') `
        -InstallRoot $InstallRoot `
        -ProgramDataRoot $ProgramDataRoot `
        -RemoveProgramData

    if ($null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)) {
        throw 'Employee Service still exists after uninstall.'
    }
    if (Test-Path $InstallRoot) {
        throw 'Install root still exists after uninstall.'
    }
    if (Test-Path $ProgramDataRoot) {
        throw 'ProgramData root still exists after full cleanup uninstall.'
    }
    if (Test-Path $uninstallKey) {
        throw 'Add/Remove Programs registration still exists after uninstall.'
    }

    Write-Host "Windows install/update/rollback/uninstall acceptance passed: $($initialManifest.version) -> $($updateManifest.version) -> $($initialManifest.version)."
}
finally {
    if ($null -ne $server -and -not $server.HasExited) {
        Stop-Process -Id $server.Id -Force -ErrorAction SilentlyContinue
    }
    try { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue } catch { }
    & sc.exe delete $serviceName 2>$null | Out-Null
    & schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
    Remove-Item -Recurse -Force $InstallRoot, $ProgramDataRoot, $testRoot -ErrorAction SilentlyContinue
    Remove-Item -Path $uninstallKey -Recurse -Force -ErrorAction SilentlyContinue
}
