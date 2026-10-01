param(
    [string]$ServerUrl,
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version = '0.1.0',
    [switch]$NoExplorer
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-LanIpv4 {
    try {
        return Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object {
                $_.IPAddress -ne '127.0.0.1' -and
                $_.IPAddress -notlike '169.254.*' -and
                $_.PrefixOrigin -ne 'WellKnown'
            } |
            Sort-Object InterfaceMetric, SkipAsSource |
            Select-Object -ExpandProperty IPAddress -First 1
    }
    catch {
        return $null
    }
}

function Assert-DevServerUrl([string]$Value) {
    $uri = $null
    if (-not [Uri]::TryCreate($Value.Trim(), [UriKind]::Absolute, [ref]$uri) -or ($uri.Scheme -ne 'http' -and $uri.Scheme -ne 'https')) {
        throw 'ServerUrl must be an absolute HTTP or HTTPS URL.'
    }
}

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$detected = Get-LanIpv4
if ([string]::IsNullOrWhiteSpace($ServerUrl)) {
    $suggestion = if ($detected) { "http://$detected`:5080" } else { 'http://127.0.0.1:5080' }
    Write-Host ''
    Write-Host 'TaskMonitoring Employee TEST Installer Builder' -ForegroundColor Cyan
    Write-Warning 'This creates an unsigned development-only installer. Use the signed production release for real deployment.'
    Write-Host "Suggested test server URL: $suggestion"
    $entered = Read-Host 'Server URL (press Enter to use the suggestion)'
    $ServerUrl = if ([string]::IsNullOrWhiteSpace($entered)) { $suggestion } else { $entered.Trim() }
}
Assert-DevServerUrl $ServerUrl

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if ($null -eq $dotnet -or -not ((& dotnet --version).Trim().StartsWith('10.'))) {
    throw '.NET 10 SDK is required to build the employee test installer.'
}

$outputRoot = Join-Path $repoRoot 'artifacts\employee-test-build'
$finalRoot = Join-Path $repoRoot 'artifacts\employee-test-installer'
Remove-Item -Recurse -Force $outputRoot, $finalRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $finalRoot | Out-Null

& (Join-Path $PSScriptRoot 'build-employee-release.ps1') `
    -Version $Version `
    -ServerUrl $ServerUrl `
    -OutputRoot $outputRoot `
    -AllowUnsignedDevelopmentBuild
if ($LASTEXITCODE -ne 0) {
    throw 'Employee development release build failed.'
}

$release = Join-Path $outputRoot "TaskMonitoring.EmployeeRelease-$Version-win-x64"
& (Join-Path $PSScriptRoot 'make-employee-setup-standalone.ps1') `
    -ReleaseDirectory $release `
    -ServerUrl $ServerUrl `
    -DevelopmentSetup
if ($LASTEXITCODE -ne 0) {
    throw 'Standalone development setup build failed.'
}

$source = Join-Path $release 'TaskMonitoring.EmployeeSetup.exe'
$destination = Join-Path $finalRoot 'TaskMonitoring.EmployeeSetup-TEST.exe'
Copy-Item -Path $source -Destination $destination -Force

Write-Host ''
Write-Host 'Employee TEST installer is ready:' -ForegroundColor Green
Write-Host $destination -ForegroundColor Green
Write-Host "Configured server: $ServerUrl"
Write-Warning 'TEST installer allows unsigned packages/HTTP and has automatic updates disabled. Do not use it for production.'

if (-not $NoExplorer) {
    Start-Process explorer.exe -ArgumentList @('/select,', $destination)
}
