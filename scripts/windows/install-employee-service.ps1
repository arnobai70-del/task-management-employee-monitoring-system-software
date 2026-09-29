param(
    [Parameter(Mandatory = $true)]
    [string]$ServiceDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,

    [ValidateRange(15, 3600)]
    [int]$HeartbeatSeconds = 60
)

$ErrorActionPreference = "Stop"
$serviceName = "TaskMonitoringEmployeeService"
$displayName = "TaskMonitoring Employee Service"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell session (Run as administrator)."
}

$resolvedServiceDirectory = (Resolve-Path $ServiceDirectory).Path
$exePath = Join-Path $resolvedServiceDirectory "TaskMonitoring.EmployeeService.exe"
if (-not (Test-Path $exePath -PathType Leaf)) {
    throw "TaskMonitoring.EmployeeService.exe was not found in $resolvedServiceDirectory. Publish the service first."
}

$serverUri = $null
if (-not [Uri]::TryCreate($ServerUrl.Trim(), [UriKind]::Absolute, [ref]$serverUri) -or
    ($serverUri.Scheme -ne "https" -and $serverUri.Scheme -ne "http")) {
    throw "ServerUrl must be an absolute HTTP/HTTPS URL."
}

if (Get-Service -Name $serviceName -ErrorAction SilentlyContinue) {
    throw "Service '$serviceName' is already installed. Uninstall it first if you need to replace it."
}

$configPath = Join-Path $resolvedServiceDirectory "appsettings.json"
$config = [ordered]@{
    EmployeeService = [ordered]@{
        ServerUrl = $serverUri.AbsoluteUri.TrimEnd('/')
        HeartbeatSeconds = $HeartbeatSeconds
    }
    Logging = [ordered]@{
        LogLevel = [ordered]@{
            Default = "Information"
            "Microsoft.Hosting.Lifetime" = "Information"
        }
    }
}
$json = $config | ConvertTo-Json -Depth 6
[IO.File]::WriteAllText($configPath, $json, [Text.UTF8Encoding]::new($false))

New-Service -Name $serviceName -BinaryPathName ('"{0}"' -f $exePath) -DisplayName $displayName -StartupType Automatic
& sc.exe description $serviceName "Visible TaskMonitoring employee service for approved server connectivity and service-health heartbeat."
if ($LASTEXITCODE -ne 0) {
    throw "Service was created but its description could not be configured."
}

Start-Service -Name $serviceName
Write-Host "Installed and started '$displayName'."
Write-Host "Server: $($serverUri.AbsoluteUri.TrimEnd('/'))"
Write-Host "Heartbeat: $HeartbeatSeconds seconds"
