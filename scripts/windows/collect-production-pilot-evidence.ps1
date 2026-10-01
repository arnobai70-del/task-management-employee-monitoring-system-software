param(
    [Parameter(Mandatory = $true)][string]$ExpectedVersion,
    [Parameter(Mandatory = $true)][string]$ServerUrl,
    [Parameter(Mandatory = $true)][string]$UpdateManifestUrl,
    [Parameter(Mandatory = $true)][string]$PublisherCertificateSha256,
    [Parameter(Mandatory = $true)][bool]$WorkflowSmokeTestPassed,
    [Parameter(Mandatory = $true)][bool]$MonitoringDisclosureConfirmed,
    [Parameter(Mandatory = $true)][bool]$UpdatePathTestPassed,
    [Parameter(Mandatory = $true)][bool]$RollbackTestPassed,
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'TaskMonitoring'),
    [string]$ProgramDataRoot = (Join-Path $env:ProgramData 'TaskMonitoring'),
    [string]$OutputPath = (Join-Path $env:ProgramData 'TaskMonitoring\production-pilot-evidence.json')
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot 'deployment-common.ps1'
if (-not (Test-Path $commonPath -PathType Leaf)) {
    throw 'deployment-common.ps1 must be located beside this evidence collector.'
}
. $commonPath

Assert-TaskMonitoringAdministrator
$expected = ConvertTo-TaskMonitoringVersion -Value $ExpectedVersion
$serverUri = ConvertTo-TaskMonitoringUri -Value $ServerUrl -Name 'ServerUrl'
$manifestUri = ConvertTo-TaskMonitoringUri -Value $UpdateManifestUrl -Name 'UpdateManifestUrl'
$publisher = ($PublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($publisher.Length -ne 64) {
    throw 'PublisherCertificateSha256 must contain exactly 64 hexadecimal characters.'
}

foreach ($attestation in @(
    @{ Name = 'WorkflowSmokeTestPassed'; Value = $WorkflowSmokeTestPassed },
    @{ Name = 'MonitoringDisclosureConfirmed'; Value = $MonitoringDisclosureConfirmed },
    @{ Name = 'UpdatePathTestPassed'; Value = $UpdatePathTestPassed },
    @{ Name = 'RollbackTestPassed'; Value = $RollbackTestPassed }
)) {
    if (-not $attestation.Value) {
        throw "$($attestation.Name) must be explicitly true for production pilot acceptance."
    }
}

$statePath = Join-Path $ProgramDataRoot 'install-state.json'
$updateSettingsPath = Join-Path $ProgramDataRoot 'update-settings.json'
$desktopExe = Join-Path $InstallRoot 'Desktop\TaskMonitoring.EmployeeDesktop.exe'
$serviceExe = Join-Path $InstallRoot 'Service\TaskMonitoring.EmployeeService.exe'
$updaterExe = Join-Path $ProgramDataRoot 'Updater\TaskMonitoring.EmployeeUpdater.exe'
$backupRoot = Join-Path $ProgramDataRoot 'backups'

foreach ($requiredPath in @($statePath, $updateSettingsPath, $desktopExe, $serviceExe, $updaterExe)) {
    if (-not (Test-Path $requiredPath -PathType Leaf)) {
        throw "Required installed production file is missing: $requiredPath"
    }
}

$state = Get-Content -Raw -Path $statePath | ConvertFrom-Json
if ([string]$state.version -ne $expected.ToString(3)) {
    throw "Installed state version '$($state.version)' does not match expected '$expected'."
}
if ([string]$state.channel -ne 'stable') {
    throw "Production pilot must be on the stable channel; actual '$($state.channel)'."
}

$settings = Get-Content -Raw -Path $updateSettingsPath | ConvertFrom-Json
if (-not [bool]$settings.requireSignedPackages) {
    throw 'Production updater settings must require signed packages.'
}
if ([string]$settings.manifestUrl -ne $manifestUri.AbsoluteUri) {
    throw "Installed update manifest URL does not match the expected production URL."
}
$settingsPublisher = ([string]$settings.publisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
if ($settingsPublisher -ne $publisher) {
    throw 'Installed updater publisher fingerprint does not match the independently verified fingerprint.'
}

Assert-TaskMonitoringFileVersion -Path $desktopExe -ExpectedVersion $ExpectedVersion
Assert-TaskMonitoringFileVersion -Path $serviceExe -ExpectedVersion $ExpectedVersion
Assert-TaskMonitoringAuthenticodeSignature -Path $desktopExe -ExpectedPublisherCertificateSha256 $publisher
Assert-TaskMonitoringAuthenticodeSignature -Path $serviceExe -ExpectedPublisherCertificateSha256 $publisher
Assert-TaskMonitoringAuthenticodeSignature -Path $updaterExe -ExpectedPublisherCertificateSha256 $publisher

$serviceName = 'TaskMonitoringEmployeeService'
$service = Get-Service -Name $serviceName -ErrorAction Stop
if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Running) {
    throw "Service '$serviceName' is not running."
}
$serviceInfo = Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
if ($null -eq $serviceInfo -or $serviceInfo.StartMode -ne 'Auto') {
    throw "Service '$serviceName' is not configured for automatic startup."
}
$delayedAuto = (Get-ItemProperty -Path "HKLM:\SYSTEM\CurrentControlSet\Services\$serviceName" -Name DelayedAutoStart -ErrorAction SilentlyContinue).DelayedAutoStart
if ($delayedAuto -ne 1) {
    throw "Service '$serviceName' is not configured for delayed automatic startup."
}
$failureOutput = (& sc.exe qfailure $serviceName 2>&1) -join "`n"
if ($LASTEXITCODE -ne 0 -or $failureOutput -notmatch 'RESTART') {
    throw "Service '$serviceName' recovery actions are not configured to restart the service."
}

$taskName = 'TaskMonitoring Employee Auto Update'
$task = Get-ScheduledTask -TaskName $taskName -ErrorAction Stop
if ($task.State -eq 'Disabled') {
    throw "Scheduled updater task '$taskName' is disabled."
}
$principalId = [string]$task.Principal.UserId
if ($principalId -notin @('SYSTEM', 'S-1-5-18')) {
    throw "Scheduled updater task '$taskName' must run as SYSTEM."
}

$backups = @()
if (Test-Path $backupRoot -PathType Container) {
    $backups = @(Get-ChildItem -Path $backupRoot -Directory -ErrorAction SilentlyContinue)
}
if ($backups.Count -lt 1) {
    throw 'No local rollback backup exists. Prove update/rollback before final pilot acceptance.'
}

$healthUri = [Uri]::new($serverUri.AbsoluteUri.TrimEnd('/') + '/health/ready')
$health = Invoke-WebRequest -UseBasicParsing -Uri $healthUri -Method Get -TimeoutSec 20
if ($health.StatusCode -lt 200 -or $health.StatusCode -ge 300) {
    throw "Production readiness endpoint returned HTTP $($health.StatusCode)."
}
$manifestResponse = Invoke-WebRequest -UseBasicParsing -Uri $manifestUri -Method Get -TimeoutSec 30
if ($manifestResponse.StatusCode -lt 200 -or $manifestResponse.StatusCode -ge 300) {
    throw "Production update manifest returned HTTP $($manifestResponse.StatusCode)."
}
$remoteManifest = $manifestResponse.Content | ConvertFrom-Json
if ([string]$remoteManifest.version -ne $ExpectedVersion -or [string]$remoteManifest.channel -ne 'stable') {
    throw 'Production update manifest version/channel does not match this accepted pilot.'
}

$evidence = [ordered]@{
    schemaVersion = 1
    kind = 'TaskMonitoringProductionPilotEvidence'
    recordedAtUtc = [DateTime]::UtcNow.ToString('O')
    machineName = $env:COMPUTERNAME
    expectedVersion = $ExpectedVersion
    installedVersion = [string]$state.version
    channel = [string]$state.channel
    serverUrl = $serverUri.AbsoluteUri.TrimEnd('/')
    updateManifestUrl = $manifestUri.AbsoluteUri
    publisherCertificateSha256 = $publisher
    desktopSignatureValid = $true
    serviceSignatureValid = $true
    updaterSignatureValid = $true
    serviceRunning = $true
    serviceDelayedAutoStart = $true
    serviceRecoveryConfigured = $true
    updaterTaskEnabled = $true
    updaterTaskRunsAsSystem = $true
    rollbackBackupPresent = $true
    workflowSmokeTestPassed = $WorkflowSmokeTestPassed
    monitoringDisclosureConfirmed = $MonitoringDisclosureConfirmed
    updatePathTestPassed = $UpdatePathTestPassed
    rollbackTestPassed = $RollbackTestPassed
}

Write-TaskMonitoringJson -Value $evidence -Path $OutputPath -Depth 8
Write-Host "Production pilot evidence written to: $OutputPath"
Write-Host "Evidence SHA-256: $(Get-TaskMonitoringFileSha256 -Path $OutputPath)"
