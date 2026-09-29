$ErrorActionPreference = "Stop"
$serviceName = "TaskMonitoringEmployeeService"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session (Run as administrator)."
}

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -eq $service) {
    Write-Host "Service '$serviceName' is not installed."
    exit 0
}

if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
    Stop-Service -Name $serviceName -Force
    (Get-Service -Name $serviceName).WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(30))
}

& sc.exe delete $serviceName
if ($LASTEXITCODE -ne 0) {
    throw "Failed to delete service '$serviceName'."
}

Write-Host "Removed service '$serviceName'. Published application files were left in place intentionally."
