param(
    [string]$RunnerPath = (Join-Path $PSScriptRoot 'run-central-agent-update.ps1'),
    [string]$WorkingRoot = (Join-Path ([IO.Path]::GetTempPath()) ('taskmonitoring-central-update-' + [Guid]::NewGuid().ToString('N')))
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if (-not (Test-Path $RunnerPath -PathType Leaf)) {
    throw "Central update runner was not found: $RunnerPath"
}

New-Item -ItemType Directory -Force -Path $WorkingRoot | Out-Null
$fakeUpdaterSource = Join-Path $WorkingRoot 'FakeUpdater.cs'
$fakeUpdaterExe = Join-Path $WorkingRoot 'FakeUpdater.exe'

@'
using System;
using System.Globalization;
using System.IO;

public static class FakeUpdater
{
    public static int Main(string[] args)
    {
        var marker = Environment.GetEnvironmentVariable("TM_CENTRAL_TEST_MARKER");
        if (!string.IsNullOrWhiteSpace(marker))
        {
            File.AppendAllText(marker, "invoked" + Environment.NewLine);
        }

        var statePath = Environment.GetEnvironmentVariable("TM_CENTRAL_TEST_STATE_PATH");
        var targetVersion = Environment.GetEnvironmentVariable("TM_CENTRAL_TEST_TARGET_VERSION");
        if (!string.IsNullOrWhiteSpace(statePath) && !string.IsNullOrWhiteSpace(targetVersion))
        {
            File.WriteAllText(statePath, "{\"version\":\"" + targetVersion + "\"}");
        }

        var exitText = Environment.GetEnvironmentVariable("TM_CENTRAL_TEST_EXIT_CODE");
        return int.TryParse(exitText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var exitCode) ? exitCode : 0;
    }
}
'@ | Set-Content -LiteralPath $fakeUpdaterSource -Encoding UTF8

Add-Type -Path $fakeUpdaterSource -OutputAssembly $fakeUpdaterExe -OutputType ConsoleApplication
if (-not (Test-Path $fakeUpdaterExe -PathType Leaf)) {
    throw 'Failed to compile the fake updater executable used by central runner acceptance.'
}

function ConvertTo-SingleQuotedLiteral([string]$Value) {
    return "'" + $Value.Replace("'", "''") + "'"
}

function Invoke-CentralScenario {
    param(
        [Parameter(Mandatory = $true)][string]$Name,
        [Parameter(Mandatory = $true)][bool]$EligibleNow,
        [Parameter(Mandatory = $true)][string]$Reason,
        [string]$RolloutStatus = 'Active',
        [string]$ManifestVersion = '2.0.0',
        [int]$UpdaterExitCode = 0,
        [string]$UpdaterTargetVersion = '2.0.0',
        [int]$ExpectedExitCode = 0,
        [string[]]$ExpectedStatuses = @(),
        [switch]$ExpectUpdater,
        [switch]$IncludeRollout
    )

    $scenarioRoot = Join-Path $WorkingRoot $Name
    $updaterRoot = Join-Path $scenarioRoot 'Updater'
    $logsRoot = Join-Path $scenarioRoot 'logs'
    New-Item -ItemType Directory -Force -Path $scenarioRoot, $updaterRoot, $logsRoot | Out-Null

    $deviceId = [Guid]::NewGuid()
    $rolloutId = [Guid]::NewGuid()
    $deviceToken = 'acceptance-device-token-' + [Guid]::NewGuid().ToString('N')
    $devicePath = Join-Path $scenarioRoot 'agent-update-device.json'
    $settingsPath = Join-Path $scenarioRoot 'update-settings.json'
    $statePath = Join-Path $scenarioRoot 'install-state.json'
    $markerPath = Join-Path $scenarioRoot 'fake-updater.marker'
    $statusPath = Join-Path $scenarioRoot 'reported-statuses.txt'
    $stdoutPath = Join-Path $scenarioRoot 'stdout.txt'
    $stderrPath = Join-Path $scenarioRoot 'stderr.txt'
    $harnessPath = Join-Path $scenarioRoot 'harness.ps1'
    $centralLogPath = Join-Path $logsRoot 'central-updater.log'

    @{
        deviceId = $deviceId
        deviceToken = $deviceToken
        serverUrl = 'https://central.acceptance.invalid'
    } | ConvertTo-Json | Set-Content -LiteralPath $devicePath -Encoding UTF8

    @{
        manifestUrl = 'https://updates.acceptance.invalid/stable/release.json'
    } | ConvertTo-Json | Set-Content -LiteralPath $settingsPath -Encoding UTF8

    @{ version = '1.0.0' } | ConvertTo-Json | Set-Content -LiteralPath $statePath -Encoding UTF8
    Copy-Item -LiteralPath $fakeUpdaterExe -Destination (Join-Path $updaterRoot 'TaskMonitoring.EmployeeUpdater.exe') -Force

    $planRolloutId = if ($IncludeRollout) { $rolloutId.ToString('D') } else { '' }
    $planRolloutIdLiteral = if ($IncludeRollout) { "[Guid]'$planRolloutId'" } else { '$null' }
    $harness = @"
`$ErrorActionPreference = 'Stop'
`$plan = [pscustomobject]@{
    deviceId = [Guid]'$($deviceId.ToString('D'))'
    isManaged = `$true
    eligibleNow = `$$($EligibleNow.ToString().ToLowerInvariant())
    rolloutId = $planRolloutIdLiteral
    rolloutName = 'Acceptance rollout'
    targetVersion = '2.0.0'
    stage = 'General'
    rolloutStatus = '$RolloutStatus'
    maintenanceStartUtc = `$null
    maintenanceEndUtc = `$null
    reason = $(ConvertTo-SingleQuotedLiteral $Reason)
}
`$manifestVersion = $(ConvertTo-SingleQuotedLiteral $ManifestVersion)
`$statusPath = $(ConvertTo-SingleQuotedLiteral $statusPath)
function Invoke-RestMethod {
    param(
        [string]`$Method,
        [object]`$Uri,
        [hashtable]`$Headers,
        [string]`$ContentType,
        [string]`$Body,
        [int]`$TimeoutSec
    )
    `$uriText = [string]`$Uri
    if (`$Method -eq 'Get' -and `$uriText.EndsWith('/plan', [StringComparison]::OrdinalIgnoreCase)) {
        return `$plan
    }
    if (`$Method -eq 'Get' -and `$uriText -eq 'https://updates.acceptance.invalid/stable/release.json') {
        return [pscustomobject]@{ version = `$manifestVersion }
    }
    if (`$Method -eq 'Post' -and `$uriText.EndsWith('/status', [StringComparison]::OrdinalIgnoreCase)) {
        `$payload = `$Body | ConvertFrom-Json
        Add-Content -LiteralPath `$statusPath -Value ([string]`$payload.status) -Encoding UTF8
        return [pscustomobject]@{ recordedAtUtc = [DateTime]::UtcNow.ToString('O') }
    }
    throw "Unexpected mocked REST request: `$Method `$uriText"
}
[Environment]::SetEnvironmentVariable('TM_CENTRAL_TEST_MARKER', $(ConvertTo-SingleQuotedLiteral $markerPath))
[Environment]::SetEnvironmentVariable('TM_CENTRAL_TEST_STATE_PATH', $(ConvertTo-SingleQuotedLiteral $statePath))
[Environment]::SetEnvironmentVariable('TM_CENTRAL_TEST_TARGET_VERSION', $(ConvertTo-SingleQuotedLiteral $UpdaterTargetVersion))
[Environment]::SetEnvironmentVariable('TM_CENTRAL_TEST_EXIT_CODE', '$UpdaterExitCode')
& $(ConvertTo-SingleQuotedLiteral ((Resolve-Path $RunnerPath).Path)) -ProgramDataRoot $(ConvertTo-SingleQuotedLiteral $scenarioRoot)
"@
    Set-Content -LiteralPath $harnessPath -Value $harness -Encoding UTF8

    $process = Start-Process -FilePath 'pwsh.exe' -ArgumentList @('-NoProfile', '-NonInteractive', '-File', $harnessPath) -Wait -PassThru -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath
    if ($process.ExitCode -ne $ExpectedExitCode) {
        $stdout = if (Test-Path $stdoutPath) { Get-Content -Raw -LiteralPath $stdoutPath } else { '' }
        $stderr = if (Test-Path $stderrPath) { Get-Content -Raw -LiteralPath $stderrPath } else { '' }
        throw "Scenario '$Name' exit code $($process.ExitCode), expected $ExpectedExitCode.`nSTDOUT:`n$stdout`nSTDERR:`n$stderr"
    }

    if ($ExpectUpdater -and -not (Test-Path $markerPath -PathType Leaf)) {
        throw "Scenario '$Name' expected the updater to run."
    }
    if (-not $ExpectUpdater -and (Test-Path $markerPath -PathType Leaf)) {
        throw "Scenario '$Name' unexpectedly ran the updater."
    }

    $actualStatuses = if (Test-Path $statusPath -PathType Leaf) {
        @(Get-Content -LiteralPath $statusPath | Where-Object { -not [string]::IsNullOrWhiteSpace($_) })
    }
    else {
        @()
    }
    if (($actualStatuses -join ',') -ne ($ExpectedStatuses -join ',')) {
        throw "Scenario '$Name' reported statuses '$($actualStatuses -join ',')', expected '$($ExpectedStatuses -join ',')'."
    }

    $visibleOutput = @()
    foreach ($path in @($stdoutPath, $stderrPath, $centralLogPath)) {
        if (Test-Path $path -PathType Leaf) {
            $visibleOutput += Get-Content -Raw -LiteralPath $path
        }
    }
    if (($visibleOutput -join "`n").Contains($deviceToken, [StringComparison]::Ordinal)) {
        throw "Scenario '$Name' leaked the device token to runner output or logs."
    }
}

try {
    Invoke-CentralScenario -Name 'no-plan' -EligibleNow $false -Reason 'No active rollout targets this employee.'
    Invoke-CentralScenario -Name 'paused' -EligibleNow $false -Reason 'Rollout is paused.' -RolloutStatus 'Paused' -IncludeRollout
    Invoke-CentralScenario -Name 'outside-window' -EligibleNow $false -Reason 'Maintenance window has not started yet.' -IncludeRollout
    Invoke-CentralScenario -Name 'manifest-mismatch' -EligibleNow $true -Reason 'Update is approved for this device now.' -ManifestVersion '2.0.1' -UpdaterTargetVersion '' -ExpectedStatuses @('Deferred') -IncludeRollout
    Invoke-CentralScenario -Name 'approved-installed' -EligibleNow $true -Reason 'Update is approved for this device now.' -ExpectedStatuses @('Downloading', 'Installed') -ExpectUpdater -IncludeRollout
    Invoke-CentralScenario -Name 'approved-failed' -EligibleNow $true -Reason 'Update is approved for this device now.' -UpdaterExitCode 20 -UpdaterTargetVersion '' -ExpectedExitCode 20 -ExpectedStatuses @('Downloading', 'Failed') -ExpectUpdater -IncludeRollout
    Write-Host 'Centralized updater runner acceptance passed.'
}
finally {
    Remove-Item -Recurse -Force $WorkingRoot -ErrorAction SilentlyContinue
}
