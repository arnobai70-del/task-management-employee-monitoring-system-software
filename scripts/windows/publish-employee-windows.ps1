param(
    [string]$Configuration = "Release",
    [string]$Runtime = "win-x64",
    [string]$OutputRoot
)

$ErrorActionPreference = "Stop"
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot "artifacts\employee-windows"
}

$desktopProject = Join-Path $repoRoot "src\EmployeeDesktop\TaskMonitoring.EmployeeDesktop\TaskMonitoring.EmployeeDesktop.csproj"
$serviceProject = Join-Path $repoRoot "src\EmployeeService\TaskMonitoring.EmployeeService\TaskMonitoring.EmployeeService.csproj"
$desktopOutput = Join-Path $OutputRoot "desktop"
$serviceOutput = Join-Path $OutputRoot "service"

New-Item -ItemType Directory -Force -Path $desktopOutput, $serviceOutput | Out-Null

dotnet publish $desktopProject --configuration $Configuration --runtime $Runtime --self-contained false --output $desktopOutput
if ($LASTEXITCODE -ne 0) { throw "Desktop publish failed." }

dotnet publish $serviceProject --configuration $Configuration --runtime $Runtime --self-contained false --output $serviceOutput
if ($LASTEXITCODE -ne 0) { throw "Windows service publish failed." }

Write-Host "Employee desktop published to: $desktopOutput"
Write-Host "Employee service published to: $serviceOutput"
