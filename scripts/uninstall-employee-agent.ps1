$ErrorActionPreference = 'Stop'
$serviceName = 'TaskMonitoringEmployeeAgent'

$currentIdentity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($currentIdentity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw 'Run this script from an elevated PowerShell session.'
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $serviceName -Force
    }

    sc.exe delete $serviceName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete Windows service '$serviceName'."
    }
}

[Environment]::SetEnvironmentVariable('TASK_MONITORING_SERVER_URL', $null, 'Machine')
Write-Host "Removed '$serviceName' and its machine-level server URL configuration."
