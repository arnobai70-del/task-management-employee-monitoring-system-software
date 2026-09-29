param(
    [ValidateSet('win-x64', 'win-arm64')]
    [string]$Runtime = 'win-x64',

    [string]$OutputRoot = 'artifacts/employee-client'
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$desktopOutput = Join-Path $repoRoot "$OutputRoot/desktop-$Runtime"
$agentOutput = Join-Path $repoRoot "$OutputRoot/agent-$Runtime"

Write-Host "Publishing employee desktop for $Runtime..."
dotnet publish (Join-Path $repoRoot 'src/EmployeeDesktop/TaskMonitoring.EmployeeDesktop/TaskMonitoring.EmployeeDesktop.csproj') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained false `
    --output $desktopOutput

Write-Host "Publishing credential-free connectivity agent for $Runtime..."
dotnet publish (Join-Path $repoRoot 'src/EmployeeAgent/TaskMonitoring.EmployeeAgent/TaskMonitoring.EmployeeAgent.csproj') `
    --configuration Release `
    --runtime $Runtime `
    --self-contained false `
    --output $agentOutput

Write-Host "Desktop: $desktopOutput"
Write-Host "Agent:   $agentOutput"
Write-Host 'Configure TASK_MONITORING_SERVER_URL or the copied JSON settings before deployment.'
