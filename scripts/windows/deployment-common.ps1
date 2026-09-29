Set-StrictMode -Version Latest

function Assert-TaskMonitoringAdministrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell session (Run as administrator)."
    }
}

function ConvertTo-TaskMonitoringUri {
    param(
        [Parameter(Mandatory = $true)][string]$Value,
        [Parameter(Mandatory = $true)][string]$Name,
        [switch]$AllowHttp
    )

    $uri = $null
    if (-not [Uri]::TryCreate($Value.Trim(), [UriKind]::Absolute, [ref]$uri)) {
        throw "$Name must be an absolute URL."
    }

    if ($uri.Scheme -eq [Uri]::UriSchemeHttps) {
        return $uri
    }

    if ($AllowHttp -and $uri.Scheme -eq [Uri]::UriSchemeHttp) {
        return $uri
    }

    throw "$Name must use HTTPS. HTTP is permitted only with the explicit development override."
}

function ConvertTo-TaskMonitoringVersion {
    param([Parameter(Mandatory = $true)][string]$Value)

    if ($Value -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
        throw "Version '$Value' must use numeric semantic version format MAJOR.MINOR.PATCH."
    }

    return [Version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
}

function Write-TaskMonitoringJson {
    param(
        [Parameter(Mandatory = $true)]$Value,
        [Parameter(Mandatory = $true)][string]$Path,
        [int]$Depth = 10
    )

    $directory = Split-Path -Parent $Path
    if (-not [string]::IsNullOrWhiteSpace($directory)) {
        New-Item -ItemType Directory -Force -Path $directory | Out-Null
    }

    $json = $Value | ConvertTo-Json -Depth $Depth
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-TaskMonitoringFileSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -Algorithm SHA256 -Path $Path).Hash.ToUpperInvariant()
}

function Get-TaskMonitoringCertificateSha256 {
    param([Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash($Certificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

function Assert-TaskMonitoringAuthenticodeSignature {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$ExpectedPublisherCertificateSha256
    )

    $signature = Get-AuthenticodeSignature -FilePath $Path
    if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid -or $null -eq $signature.SignerCertificate) {
        throw "Authenticode validation failed for '$Path'. Status=$($signature.Status)."
    }

    $actual = Get-TaskMonitoringCertificateSha256 -Certificate $signature.SignerCertificate
    $expected = ($ExpectedPublisherCertificateSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($expected.Length -ne 64) {
        throw "Publisher certificate SHA-256 fingerprint must contain exactly 64 hexadecimal characters."
    }

    if ($actual -ne $expected) {
        throw "Publisher certificate mismatch for '$Path'."
    }
}

function Read-TaskMonitoringReleaseManifest {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path $Path -PathType Leaf)) {
        throw "Release manifest was not found: $Path"
    }

    $manifest = Get-Content -Raw -Path $Path | ConvertFrom-Json
    if ($manifest.schemaVersion -ne 1) {
        throw "Unsupported release manifest schema version '$($manifest.schemaVersion)'."
    }

    [void](ConvertTo-TaskMonitoringVersion -Value ([string]$manifest.version))
    [void](ConvertTo-TaskMonitoringVersion -Value ([string]$manifest.minimumUpdaterVersion))

    if ([string]::IsNullOrWhiteSpace([string]$manifest.channel)) {
        throw "Release manifest channel is required."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.package.file)) {
        throw "Release manifest package.file is required."
    }
    if ([string]::IsNullOrWhiteSpace([string]$manifest.package.sha256)) {
        throw "Release manifest package.sha256 is required."
    }

    return $manifest
}

function Assert-TaskMonitoringPackageHash {
    param(
        [Parameter(Mandatory = $true)][string]$PackagePath,
        [Parameter(Mandatory = $true)][string]$ExpectedSha256
    )

    $actual = Get-TaskMonitoringFileSha256 -Path $PackagePath
    $expected = ($ExpectedSha256 -replace '[^0-9A-Fa-f]', '').ToUpperInvariant()
    if ($expected.Length -ne 64 -or $actual -ne $expected) {
        throw "Package SHA-256 verification failed for '$PackagePath'."
    }
}

function Stop-TaskMonitoringEmployeeService {
    param([string]$ServiceName = 'TaskMonitoringEmployeeService')

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        return
    }

    if ($service.Status -ne [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        Stop-Service -Name $ServiceName -Force
        (Get-Service -Name $ServiceName).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }
}

function Start-TaskMonitoringEmployeeService {
    param([string]$ServiceName = 'TaskMonitoringEmployeeService')

    $service = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        throw "Service '$ServiceName' is not installed."
    }

    Start-Service -Name $ServiceName
    (Get-Service -Name $ServiceName).WaitForStatus(
        [System.ServiceProcess.ServiceControllerStatus]::Running,
        [TimeSpan]::FromSeconds(30))
}
