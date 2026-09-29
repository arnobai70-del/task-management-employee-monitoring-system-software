param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,
    [string]$PublisherCertificateSha256,
    [switch]$AllowUnsignedDevelopmentBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'deployment-common.ps1')

$releaseRoot = (Resolve-Path $ReleaseDirectory).Path
$manifest = Read-TaskMonitoringReleaseManifest -Path (Join-Path $releaseRoot 'release.json')
$packagePath = Join-Path $releaseRoot ([string]$manifest.package.file)
Assert-TaskMonitoringPackageHash -PackagePath $packagePath -ExpectedSha256 ([string]$manifest.package.sha256)

if ((Get-Item $packagePath).Length -ne [long]$manifest.package.sizeBytes) {
    throw 'Release manifest package size does not match the runtime archive.'
}

$updaterExe = Join-Path $releaseRoot 'updater\TaskMonitoring.EmployeeUpdater.exe'
if (-not (Test-Path $updaterExe -PathType Leaf)) {
    throw 'Release bundle does not contain TaskMonitoring.EmployeeUpdater.exe.'
}

$tempRoot = Join-Path $env:TEMP ('TaskMonitoring-release-test-' + [Guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -Path $packagePath -DestinationPath $tempRoot -Force
    $desktopExe = Join-Path $tempRoot 'desktop\TaskMonitoring.EmployeeDesktop.exe'
    $serviceExe = Join-Path $tempRoot 'service\TaskMonitoring.EmployeeService.exe'
    foreach ($required in @($desktopExe, $serviceExe)) {
        if (-not (Test-Path $required -PathType Leaf)) {
            throw "Required runtime executable is missing: $required"
        }
    }

    Assert-TaskMonitoringFileVersion -Path $desktopExe -ExpectedVersion ([string]$manifest.version)
    Assert-TaskMonitoringFileVersion -Path $serviceExe -ExpectedVersion ([string]$manifest.version)

    if (Test-Path (Join-Path $tempRoot 'service\appsettings.json')) {
        throw 'Runtime archive must not ship an environment-specific service appsettings.json.'
    }
    if (Test-Path (Join-Path $tempRoot 'desktop\desktop-settings.json')) {
        throw 'Runtime archive must not ship an environment-specific desktop-settings.json.'
    }

    if (-not $AllowUnsignedDevelopmentBuild) {
        if ([string]::IsNullOrWhiteSpace($PublisherCertificateSha256)) {
            throw 'PublisherCertificateSha256 is required for signed bundle validation.'
        }

        foreach ($signedFile in @(
            $desktopExe,
            $serviceExe,
            $updaterExe,
            (Join-Path $releaseRoot 'install-employee-windows.ps1'),
            (Join-Path $releaseRoot 'uninstall-employee-windows.ps1'),
            (Join-Path $releaseRoot 'deployment-common.ps1')
        )) {
            if (-not (Test-Path $signedFile -PathType Leaf)) {
                throw "Required signed release file is missing: $signedFile"
            }
            Assert-TaskMonitoringAuthenticodeSignature -Path $signedFile -ExpectedPublisherCertificateSha256 $PublisherCertificateSha256
        }
    }

    Write-Host "Release bundle validation passed for version $($manifest.version), channel $($manifest.channel)."
}
finally {
    Remove-Item -Recurse -Force $tempRoot -ErrorAction SilentlyContinue
}
