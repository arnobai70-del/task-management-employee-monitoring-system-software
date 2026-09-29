param(
    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,

    [string]$ReleaseDirectory = $PSScriptRoot,
    [string]$UpdateManifestUrl,
    [string]$PublisherCertificateSha256,

    [ValidateRange(15, 3600)]
    [int]$HeartbeatSeconds = 60,

    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'TaskMonitoring'),
    [string]$ProgramDataRoot = (Join-Path $env:ProgramData 'TaskMonitoring'),

    [switch]$DisableAutoUpdate,
    [switch]$AllowUnsignedDevelopmentBuild,
    [switch]$AllowHttpForDevelopment,
    [switch]$SkipServerHealthCheck,
    [switch]$NoPublicDesktopShortcut,
    [switch]$ForceCloseDesktop
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot 'deployment-common.ps1'
if (-not (Test-Path $commonPath -PathType Leaf)) {
    throw "deployment-common.ps1 must be located beside this installer."
}
. $commonPath

Assert-TaskMonitoringAdministrator

$serviceName = 'TaskMonitoringEmployeeService'
$taskName = 'TaskMonitoring Employee Auto Update'
$resolvedReleaseDirectory = (Resolve-Path $ReleaseDirectory).Path
$manifestPath = Join-Path $resolvedReleaseDirectory 'release.json'
$manifest = Read-TaskMonitoringReleaseManifest -Path $manifestPath
$packagePath = Join-Path $resolvedReleaseDirectory ([string]$manifest.package.file)
$updaterSource = Join-Path $resolvedReleaseDirectory 'updater'
$updaterSourceExe = Join-Path $updaterSource 'TaskMonitoring.EmployeeUpdater.exe'

if (-not (Test-Path $packagePath -PathType Leaf)) {
    throw "Runtime package was not found: $packagePath"
}
if (-not (Test-Path $updaterSourceExe -PathType Leaf)) {
    throw "Updater executable was not found: $updaterSourceExe"
}
Assert-TaskMonitoringPackageHash -PackagePath $packagePath -ExpectedSha256 ([string]$manifest.package.sha256)

$serverUri = ConvertTo-TaskMonitoringUri -Value $ServerUrl -Name 'ServerUrl' -AllowHttp:$AllowHttpForDevelopment
$updateManifestUri = $null
if (-not $DisableAutoUpdate) {
    if ([string]::IsNullOrWhiteSpace($UpdateManifestUrl)) {
        throw 'UpdateManifestUrl is required unless -DisableAutoUpdate is explicitly selected.'
    }
    $updateManifestUri = ConvertTo-TaskMonitoringUri -Value $UpdateManifestUrl -Name 'UpdateManifestUrl' -AllowHttp:$AllowHttpForDevelopment
}

if (-not $AllowUnsignedDevelopmentBuild) {
    $normalizedPublisherFingerprint = ($PublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($normalizedPublisherFingerprint.Length -ne 64) {
        throw 'Production installation requires -PublisherCertificateSha256 with a 64-character SHA-256 certificate fingerprint.'
    }
}
else {
    Write-Warning 'Unsigned development installation mode is enabled. Do not use this override for production deployment.'
}

if (-not $SkipServerHealthCheck) {
    $healthUri = [Uri]::new($serverUri.AbsoluteUri.TrimEnd('/') + '/health/live')
    try {
        $healthResponse = Invoke-WebRequest -UseBasicParsing -Uri $healthUri -Method Get -TimeoutSec 15
        if ($healthResponse.StatusCode -lt 200 -or $healthResponse.StatusCode -ge 300) {
            throw "Health endpoint returned HTTP $($healthResponse.StatusCode)."
        }
    }
    catch {
        throw "Server health preflight failed for $healthUri. $($_.Exception.Message)"
    }
}

$desktopProcesses = @(Get-Process -Name 'TaskMonitoring.EmployeeDesktop' -ErrorAction SilentlyContinue)
if ($desktopProcesses.Count -gt 0) {
    if (-not $ForceCloseDesktop) {
        throw 'Employee Desktop is running. Ask the employee to close it or rerun with -ForceCloseDesktop during a maintenance window.'
    }
    $desktopProcesses | Stop-Process -Force
}

$stagingRoot = Join-Path $ProgramDataRoot ('.install-staging-' + [Guid]::NewGuid().ToString('N'))
$runtimeStage = Join-Path $stagingRoot 'runtime'
$backupRoot = Join-Path $ProgramDataRoot ('backups\manual-' + (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))
$backupInstallRoot = Join-Path $backupRoot 'InstallRoot'
$updaterRoot = Join-Path $ProgramDataRoot 'Updater'
$backupUpdaterRoot = Join-Path $backupRoot 'Updater'
$maintenanceRoot = Join-Path $ProgramDataRoot 'Maintenance'
$logsRoot = Join-Path $ProgramDataRoot 'logs'
$statePath = Join-Path $ProgramDataRoot 'install-state.json'
$updateSettingsPath = Join-Path $ProgramDataRoot 'update-settings.json'

New-Item -ItemType Directory -Force -Path $stagingRoot, $runtimeStage, $backupRoot, $maintenanceRoot, $logsRoot | Out-Null
Expand-Archive -Path $packagePath -DestinationPath $runtimeStage -Force

$desktopStage = Join-Path $runtimeStage 'desktop'
$serviceStage = Join-Path $runtimeStage 'service'
$desktopExe = Join-Path $desktopStage 'TaskMonitoring.EmployeeDesktop.exe'
$serviceExe = Join-Path $serviceStage 'TaskMonitoring.EmployeeService.exe'
if (-not (Test-Path $desktopExe -PathType Leaf) -or -not (Test-Path $serviceExe -PathType Leaf)) {
    throw 'Release package does not contain the expected desktop/service executables.'
}

if (-not $AllowUnsignedDevelopmentBuild) {
    Assert-TaskMonitoringAuthenticodeSignature -Path $desktopExe -ExpectedPublisherCertificateSha256 $PublisherCertificateSha256
    Assert-TaskMonitoringAuthenticodeSignature -Path $serviceExe -ExpectedPublisherCertificateSha256 $PublisherCertificateSha256
    Assert-TaskMonitoringAuthenticodeSignature -Path $updaterSourceExe -ExpectedPublisherCertificateSha256 $PublisherCertificateSha256
}

$serviceExisted = $null -ne (Get-Service -Name $serviceName -ErrorAction SilentlyContinue)
& schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null
Stop-TaskMonitoringEmployeeService -ServiceName $serviceName

$activatedNewInstall = $false
try {
    if (Test-Path $InstallRoot) {
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $backupInstallRoot) | Out-Null
        Move-Item -Path $InstallRoot -Destination $backupInstallRoot
    }
    if (Test-Path $updaterRoot) {
        Move-Item -Path $updaterRoot -Destination $backupUpdaterRoot
    }

    New-Item -ItemType Directory -Force -Path $InstallRoot, $updaterRoot | Out-Null
    Move-Item -Path $desktopStage -Destination (Join-Path $InstallRoot 'Desktop')
    Move-Item -Path $serviceStage -Destination (Join-Path $InstallRoot 'Service')
    Copy-Item -Path (Join-Path $updaterSource '*') -Destination $updaterRoot -Recurse -Force
    $activatedNewInstall = $true

    $desktopSettings = [ordered]@{
        serverUrl = $serverUri.AbsoluteUri.TrimEnd('/')
        lockServerUrl = $true
    }
    Write-TaskMonitoringJson -Value $desktopSettings -Path (Join-Path $InstallRoot 'Desktop\desktop-settings.json')

    $serviceSettings = [ordered]@{
        EmployeeService = [ordered]@{
            ServerUrl = $serverUri.AbsoluteUri.TrimEnd('/')
            HeartbeatSeconds = $HeartbeatSeconds
            AllowInsecureHttp = [bool]$AllowHttpForDevelopment
        }
        Logging = [ordered]@{
            LogLevel = [ordered]@{
                Default = 'Information'
                'Microsoft.Hosting.Lifetime' = 'Information'
            }
        }
    }
    Write-TaskMonitoringJson -Value $serviceSettings -Path (Join-Path $InstallRoot 'Service\appsettings.json')

    $installState = [ordered]@{
        version = [string]$manifest.version
        channel = [string]$manifest.channel
        installedAtUtc = [DateTime]::UtcNow.ToString('O')
        lastSuccessfulUpdateUtc = $null
    }
    Write-TaskMonitoringJson -Value $installState -Path $statePath

    if (-not $DisableAutoUpdate) {
        $updateSettings = [ordered]@{
            manifestUrl = $updateManifestUri.AbsoluteUri
            channel = [string]$manifest.channel
            installRoot = $InstallRoot
            statePath = $statePath
            backupRoot = (Join-Path $ProgramDataRoot 'backups')
            serviceName = $serviceName
            requireSignedPackages = (-not $AllowUnsignedDevelopmentBuild)
            publisherCertificateSha256 = if ($AllowUnsignedDevelopmentBuild) { $null } else { ($PublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant() }
            allowInsecureHttp = [bool]$AllowHttpForDevelopment
            backupRetention = 2
        }
        Write-TaskMonitoringJson -Value $updateSettings -Path $updateSettingsPath
    }
    elseif (Test-Path $updateSettingsPath) {
        Remove-Item $updateSettingsPath -Force
    }

    Copy-Item -Path (Join-Path $PSScriptRoot 'uninstall-employee-windows.ps1') -Destination (Join-Path $maintenanceRoot 'uninstall-employee-windows.ps1') -Force
    Copy-Item -Path $commonPath -Destination (Join-Path $maintenanceRoot 'deployment-common.ps1') -Force

    $installedServiceExe = Join-Path $InstallRoot 'Service\TaskMonitoring.EmployeeService.exe'
    if ($serviceExisted) {
        & sc.exe config $serviceName binPath= ('"{0}"' -f $installedServiceExe) start= delayed-auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to update Windows service configuration.' }
    }
    else {
        New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $installedServiceExe) -DisplayName 'TaskMonitoring Employee Service' -StartupType Automatic
        & sc.exe config $serviceName start= delayed-auto | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to configure delayed automatic service startup.' }
    }

    & sc.exe description $serviceName 'Visible TaskMonitoring service for approved API reachability and service-health heartbeat.' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure service description.' }
    & sc.exe failure $serviceName reset= 86400 actions= restart/5000/restart/15000/""/0 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to configure service recovery actions.' }
    & sc.exe failureflag $serviceName 1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to enable service recovery actions.' }

    Start-TaskMonitoringEmployeeService -ServiceName $serviceName

    $shortcutTarget = Join-Path $InstallRoot 'Desktop\TaskMonitoring.EmployeeDesktop.exe'
    $shell = New-Object -ComObject WScript.Shell
    $startMenuDirectory = Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs'
    $startMenuShortcut = $shell.CreateShortcut((Join-Path $startMenuDirectory 'TaskMonitoring Employee Workspace.lnk'))
    $startMenuShortcut.TargetPath = $shortcutTarget
    $startMenuShortcut.WorkingDirectory = Split-Path -Parent $shortcutTarget
    $startMenuShortcut.Save()

    if (-not $NoPublicDesktopShortcut) {
        $publicDesktop = [Environment]::GetFolderPath('CommonDesktopDirectory')
        $desktopShortcut = $shell.CreateShortcut((Join-Path $publicDesktop 'TaskMonitoring Employee Workspace.lnk'))
        $desktopShortcut.TargetPath = $shortcutTarget
        $desktopShortcut.WorkingDirectory = Split-Path -Parent $shortcutTarget
        $desktopShortcut.Save()
    }

    if (-not $DisableAutoUpdate) {
        $updaterExe = Join-Path $updaterRoot 'TaskMonitoring.EmployeeUpdater.exe'
        $taskCommand = ('"{0}" --settings "{1}"' -f $updaterExe, $updateSettingsPath)
        & schtasks.exe /Create /TN $taskName /TR $taskCommand /SC HOURLY /MO 4 /RU SYSTEM /RL HIGHEST /F | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Failed to create the automatic update scheduled task.' }
    }

    $uninstallKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskMonitoringEmployee'
    New-Item -Path $uninstallKey -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayName -Value 'TaskMonitoring Employee Workspace' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name DisplayVersion -Value ([string]$manifest.version) -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name Publisher -Value 'TaskMonitoring' -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name InstallLocation -Value $InstallRoot -PropertyType String -Force | Out-Null
    $uninstallCommand = 'powershell.exe -NoProfile -ExecutionPolicy Bypass -File "{0}" -InstallRoot "{1}" -ProgramDataRoot "{2}"' -f (Join-Path $maintenanceRoot 'uninstall-employee-windows.ps1'), $InstallRoot, $ProgramDataRoot
    New-ItemProperty -Path $uninstallKey -Name UninstallString -Value $uninstallCommand -PropertyType String -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoModify -Value 1 -PropertyType DWord -Force | Out-Null
    New-ItemProperty -Path $uninstallKey -Name NoRepair -Value 1 -PropertyType DWord -Force | Out-Null

    if (Test-Path $backupRoot) {
        Write-Host "Previous installation backup retained at: $backupRoot"
    }
    Write-Host "Installed TaskMonitoring Employee Workspace $($manifest.version)."
    Write-Host "Server: $($serverUri.AbsoluteUri.TrimEnd('/'))"
    Write-Host "Install root: $InstallRoot"
    Write-Host "Auto update: $((-not $DisableAutoUpdate).ToString().ToLowerInvariant())"
}
catch {
    Write-Warning "Installation failed. Attempting rollback: $($_.Exception.Message)"
    try { Stop-TaskMonitoringEmployeeService -ServiceName $serviceName } catch { }

    if ($activatedNewInstall -and (Test-Path $InstallRoot)) {
        Remove-Item -Recurse -Force $InstallRoot
    }
    if ($activatedNewInstall -and (Test-Path $updaterRoot)) {
        Remove-Item -Recurse -Force $updaterRoot
    }
    if (Test-Path $backupInstallRoot) {
        Move-Item -Path $backupInstallRoot -Destination $InstallRoot
    }
    if (Test-Path $backupUpdaterRoot) {
        Move-Item -Path $backupUpdaterRoot -Destination $updaterRoot
    }

    if ($serviceExisted) {
        try { Start-TaskMonitoringEmployeeService -ServiceName $serviceName } catch { }
    }
    throw
}
finally {
    if (Test-Path $stagingRoot) {
        Remove-Item -Recurse -Force $stagingRoot -ErrorAction SilentlyContinue
    }
}
