param(
    [string]$ProgramDataRoot = (Join-Path $env:ProgramData 'TaskMonitoring'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$devicePath = Join-Path $ProgramDataRoot 'agent-update-device.json'
$settingsPath = Join-Path $ProgramDataRoot 'update-settings.json'
$statePath = Join-Path $ProgramDataRoot 'install-state.json'
$updaterExe = Join-Path $ProgramDataRoot 'Updater\TaskMonitoring.EmployeeUpdater.exe'
$logRoot = Join-Path $ProgramDataRoot 'logs'
$logPath = Join-Path $logRoot 'central-updater.log'
New-Item -ItemType Directory -Force -Path $logRoot | Out-Null

function Write-CentralUpdateLog([string]$Message) {
    $line = '{0} {1}' -f [DateTime]::UtcNow.ToString('O'), $Message
    Add-Content -LiteralPath $logPath -Value $line -Encoding UTF8
    Write-Host $line
}

function Read-JsonFile([string]$Path) {
    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Required update control file was not found: $Path"
    }
    return Get-Content -Raw -LiteralPath $Path | ConvertFrom-Json
}

function Send-DeviceStatus(
    [object]$Device,
    [Guid]$RolloutId,
    [string]$Status,
    [string]$InstalledVersion,
    [string]$Message
) {
    try {
        $headers = @{ 'X-Agent-Device-Token' = [string]$Device.deviceToken }
        $body = @{
            rolloutId = $RolloutId
            status = $Status
            installedVersion = if ([string]::IsNullOrWhiteSpace($InstalledVersion)) { $null } else { $InstalledVersion }
            message = if ([string]::IsNullOrWhiteSpace($Message)) { $null } else { $Message }
        } | ConvertTo-Json -Depth 5
        $uri = '{0}/api/agent-updates/device/{1}/status' -f ([string]$Device.serverUrl).TrimEnd('/'), [string]$Device.deviceId
        Invoke-RestMethod -Method Post -Uri $uri -Headers $headers -ContentType 'application/json' -Body $body -TimeoutSec 15 | Out-Null
    }
    catch {
        Write-CentralUpdateLog "Warning: could not report device rollout status '$Status'. $($_.Exception.Message)"
    }
}

try {
    if (-not (Test-Path $devicePath -PathType Leaf)) {
        Write-CentralUpdateLog 'Central update device enrollment is not configured; nothing to do.'
        exit 0
    }
    if (-not (Test-Path $settingsPath -PathType Leaf) -or -not (Test-Path $updaterExe -PathType Leaf)) {
        throw 'Updater settings or updater executable is missing.'
    }

    $device = Read-JsonFile $devicePath
    if ([string]::IsNullOrWhiteSpace([string]$device.deviceId) -or
        [string]::IsNullOrWhiteSpace([string]$device.deviceToken) -or
        [string]::IsNullOrWhiteSpace([string]$device.serverUrl)) {
        throw 'Central update device enrollment file is incomplete.'
    }

    $headers = @{ 'X-Agent-Device-Token' = [string]$device.deviceToken }
    $planUri = '{0}/api/agent-updates/device/{1}/plan' -f ([string]$device.serverUrl).TrimEnd('/'), [string]$device.deviceId
    $plan = Invoke-RestMethod -Method Get -Uri $planUri -Headers $headers -TimeoutSec 15
    if (-not $plan.isManaged) {
        Write-CentralUpdateLog "Device is not managed by the central rollout service: $($plan.reason)"
        exit 0
    }
    if (-not $plan.eligibleNow) {
        Write-CentralUpdateLog "No approved update now: $($plan.reason)"
        exit 0
    }

    $settings = Read-JsonFile $settingsPath
    $manifest = Invoke-RestMethod -Method Get -Uri ([string]$settings.manifestUrl) -TimeoutSec 30
    if ([string]$manifest.version -ne [string]$plan.targetVersion) {
        Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'Deferred' -InstalledVersion '' -Message "Published manifest version $($manifest.version) does not match approved target $($plan.targetVersion)."
        Write-CentralUpdateLog 'Approved rollout target does not match the published manifest; update deferred.'
        exit 0
    }

    $beforeState = if (Test-Path $statePath -PathType Leaf) { Read-JsonFile $statePath } else { $null }
    $beforeVersion = if ($null -eq $beforeState) { '' } else { [string]$beforeState.version }
    $startedAtUtc = [DateTime]::UtcNow
    Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'Downloading' -InstalledVersion $beforeVersion -Message 'Approved rollout started by the elevated scheduled updater.'
    Write-CentralUpdateLog "Starting approved rollout $($plan.rolloutId) to version $($plan.targetVersion)."

    if ($Force) {
        & $updaterExe --settings $settingsPath --force
    }
    else {
        & $updaterExe --settings $settingsPath
    }
    $exitCode = $LASTEXITCODE

    $afterState = if (Test-Path $statePath -PathType Leaf) { Read-JsonFile $statePath } else { $null }
    $afterVersion = if ($null -eq $afterState) { '' } else { [string]$afterState.version }
    $rolledBackAt = $null
    if ($null -ne $afterState -and $null -ne $afterState.PSObject.Properties['rolledBackAtUtc'] -and -not [string]::IsNullOrWhiteSpace([string]$afterState.rolledBackAtUtc)) {
        try { $rolledBackAt = [DateTime]::Parse([string]$afterState.rolledBackAtUtc).ToUniversalTime() } catch { $rolledBackAt = $null }
    }

    if ($exitCode -eq 0 -and $afterVersion -eq [string]$plan.targetVersion) {
        Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'Installed' -InstalledVersion $afterVersion -Message 'Target version installed successfully.'
        Write-CentralUpdateLog "Rollout installed successfully: $beforeVersion -> $afterVersion."
        exit 0
    }

    if ($exitCode -eq 10) {
        Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'Deferred' -InstalledVersion $afterVersion -Message 'Employee Desktop is running; updater deferred activation until a later scheduled check.'
        Write-CentralUpdateLog 'Updater deferred because Employee Desktop is running.'
        exit 0
    }

    if ($null -ne $rolledBackAt -and $rolledBackAt -ge $startedAtUtc.AddMinutes(-1)) {
        Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'RolledBack' -InstalledVersion $afterVersion -Message "Update failed and the installation rolled back. Updater exit code: $exitCode."
        Write-CentralUpdateLog "Rollout failed and rolled back. ExitCode=$exitCode; Version=$afterVersion."
    }
    else {
        Send-DeviceStatus -Device $device -RolloutId ([Guid]$plan.rolloutId) -Status 'Failed' -InstalledVersion $afterVersion -Message "Updater failed with exit code $exitCode."
        Write-CentralUpdateLog "Rollout failed. ExitCode=$exitCode; Version=$afterVersion."
    }
    exit $(if ($exitCode -eq 0) { 20 } else { $exitCode })
}
catch {
    Write-CentralUpdateLog "Central update runner failed: $($_.Exception.Message)"
    exit 20
}
