param(
    [string]$InstallRoot = (Join-Path $env:ProgramFiles 'TaskMonitoring'),
    [string]$ProgramDataRoot = (Join-Path $env:ProgramData 'TaskMonitoring'),
    [switch]$RemoveProgramData
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$commonPath = Join-Path $PSScriptRoot 'deployment-common.ps1'
if (-not (Test-Path $commonPath -PathType Leaf)) {
    throw "deployment-common.ps1 must be located beside this uninstaller."
}
. $commonPath

Assert-TaskMonitoringAdministrator

$serviceName = 'TaskMonitoringEmployeeService'
$taskName = 'TaskMonitoring Employee Auto Update'

$desktopProcesses = @(Get-Process -Name 'TaskMonitoring.EmployeeDesktop' -ErrorAction SilentlyContinue)
if ($desktopProcesses.Count -gt 0) {
    throw 'Employee Desktop is still running. Close the application before uninstalling.'
}

& schtasks.exe /Delete /TN $taskName /F 2>$null | Out-Null

$service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($null -ne $service) {
    Stop-TaskMonitoringEmployeeService -ServiceName $serviceName
    & sc.exe delete $serviceName | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Failed to delete service '$serviceName'."
    }
}

$shortcuts = @(
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\TaskMonitoring Employee Workspace.lnk'),
    (Join-Path ([Environment]::GetFolderPath('CommonDesktopDirectory')) 'TaskMonitoring Employee Workspace.lnk')
)
foreach ($shortcut in $shortcuts) {
    Remove-Item -Force $shortcut -ErrorAction SilentlyContinue
}

Remove-Item -Recurse -Force $InstallRoot -ErrorAction SilentlyContinue
Remove-Item -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\TaskMonitoringEmployee' -Recurse -Force -ErrorAction SilentlyContinue

if ($RemoveProgramData) {
    Remove-Item -Recurse -Force $ProgramDataRoot -ErrorAction SilentlyContinue
    Write-Host 'Removed TaskMonitoring Employee Workspace, updater state, logs and backups.'
}
else {
    $updaterRoot = Join-Path $ProgramDataRoot 'Updater'
    Remove-Item -Recurse -Force $updaterRoot -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $ProgramDataRoot 'update-settings.json') -ErrorAction SilentlyContinue
    Write-Host "Removed TaskMonitoring Employee Workspace. Operational logs/backups under '$ProgramDataRoot' were retained."
    Write-Host 'Use -RemoveProgramData for a full data cleanup.'
}
