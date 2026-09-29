param(
    [Parameter(Mandatory = $true)]
    [string]$AgentDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ServerBaseUrl
)

$ErrorActionPreference = 'Stop'
$serviceName = 'TaskMonitoringEmployeeAgent'
$displayName = 'Task Monitoring Employee Agent'
$exePath = Join-Path (Resolve-Path $AgentDirectory) 'TaskMonitoring.EmployeeAgent.exe'

if (-not (Test-Path $exePath)) {
    throw "Employee agent executable was not found: $exePath"
}

$uri = $null
if (-not [Uri]::TryCreate($ServerBaseUrl, [UriKind]::Absolute, [ref]$uri)) {
    throw 'ServerBaseUrl must be an absolute URL.'
}

if ($uri.Scheme -ne 'https' -and -not $uri.IsLoopback) {
    throw 'Employee agent requires HTTPS. Plain HTTP is allowed only for loopback development URLs.'
}

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' already exists. Uninstall it before reinstalling."
}

[Environment]::SetEnvironmentVariable('TASK_MONITORING_SERVER_URL', $ServerBaseUrl, 'Machine')

$quotedExe = '"' + $exePath + '"'
sc.exe create $serviceName binPath= $quotedExe start= delayed-auto DisplayName= $displayName | Out-Host
if ($LASTEXITCODE -ne 0) {
    [Environment]::SetEnvironmentVariable('TASK_MONITORING_SERVER_URL', $null, 'Machine')
    throw "Failed to create Windows service '$serviceName'."
}

sc.exe description $serviceName 'Checks Task Monitoring backend reachability only. It receives no employee password or refresh token and does not collect user activity.' | Out-Host
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/""/0 | Out-Host
Start-Service -Name $serviceName

Write-Host "Installed and started '$displayName'."
Write-Host "Server: $ServerBaseUrl"
