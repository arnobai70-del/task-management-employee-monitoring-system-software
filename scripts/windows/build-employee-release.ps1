param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$Version,

    [ValidateSet('stable', 'beta')]
    [string]$Channel = 'stable',

    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$OutputRoot,
    [string]$PackageBaseUrl,

    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$UpdaterVersion = '1.0.0',

    [ValidatePattern('^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$')]
    [string]$MinimumUpdaterVersion = '1.0.0',

    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [switch]$AllowUnsignedDevelopmentBuild
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if ([string]::IsNullOrWhiteSpace($OutputRoot)) {
    $OutputRoot = Join-Path $repoRoot 'artifacts\employee-release'
}

$releaseRoot = Join-Path $OutputRoot "TaskMonitoring.EmployeeRelease-$Version-$Runtime"
$publishRoot = Join-Path $OutputRoot '.publish'
$runtimeRoot = Join-Path $OutputRoot '.runtime'
$desktopPublish = Join-Path $publishRoot 'desktop'
$servicePublish = Join-Path $publishRoot 'service'
$updaterPublish = Join-Path $publishRoot 'updater'

Remove-Item -Recurse -Force $releaseRoot, $publishRoot, $runtimeRoot -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $releaseRoot, $desktopPublish, $servicePublish, $updaterPublish, $runtimeRoot | Out-Null

$desktopProject = Join-Path $repoRoot 'src\EmployeeDesktop\TaskMonitoring.EmployeeDesktop\TaskMonitoring.EmployeeDesktop.csproj'
$serviceProject = Join-Path $repoRoot 'src\EmployeeService\TaskMonitoring.EmployeeService\TaskMonitoring.EmployeeService.csproj'
$updaterProject = Join-Path $repoRoot 'src\EmployeeUpdater\TaskMonitoring.EmployeeUpdater\TaskMonitoring.EmployeeUpdater.csproj'

function Publish-SingleFileApplication {
    param(
        [Parameter(Mandatory = $true)][string]$Project,
        [Parameter(Mandatory = $true)][string]$Destination,
        [Parameter(Mandatory = $true)][string]$ApplicationVersion
    )

    dotnet publish $Project `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $Destination `
        -p:Version=$ApplicationVersion `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Project"
    }
}

function Get-CertificateSha256 {
    param([Parameter(Mandatory = $true)][Security.Cryptography.X509Certificates.X509Certificate2]$Certificate)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        return (($sha.ComputeHash($Certificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join '')
    }
    finally {
        $sha.Dispose()
    }
}

Publish-SingleFileApplication -Project $desktopProject -Destination $desktopPublish -ApplicationVersion $Version
Publish-SingleFileApplication -Project $serviceProject -Destination $servicePublish -ApplicationVersion $Version
Publish-SingleFileApplication -Project $updaterProject -Destination $updaterPublish -ApplicationVersion $UpdaterVersion

Remove-Item (Join-Path $servicePublish 'appsettings*.json') -Force -ErrorAction SilentlyContinue
Get-ChildItem -Path $publishRoot -Recurse -Filter '*.pdb' | Remove-Item -Force

$primaryExecutables = @(
    (Join-Path $desktopPublish 'TaskMonitoring.EmployeeDesktop.exe'),
    (Join-Path $servicePublish 'TaskMonitoring.EmployeeService.exe'),
    (Join-Path $updaterPublish 'TaskMonitoring.EmployeeUpdater.exe')
)
foreach ($exe in $primaryExecutables) {
    if (-not (Test-Path $exe -PathType Leaf)) {
        throw "Expected published executable was not found: $exe"
    }
}

$signingCertificate = $null
$publisherFingerprint = $null
if ([string]::IsNullOrWhiteSpace($PfxPath)) {
    if (-not $AllowUnsignedDevelopmentBuild) {
        throw 'Production release packaging requires -PfxPath. Use -AllowUnsignedDevelopmentBuild only for CI/development validation.'
    }
    Write-Warning 'Building an unsigned development release. The production installer/updater should not accept this package.'
}
else {
    $resolvedPfx = (Resolve-Path $PfxPath).Path
    $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        Select-Object -First 1
    if ($null -eq $signTool) {
        throw 'signtool.exe was not found. Install the Windows SDK signing tools.'
    }

    $signingCertificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
        $resolvedPfx,
        $PfxPassword,
        [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
    if (-not $signingCertificate.HasPrivateKey) {
        throw 'The supplied PFX does not contain a private key.'
    }
    $publisherFingerprint = Get-CertificateSha256 -Certificate $signingCertificate

    foreach ($exe in $primaryExecutables) {
        & $signTool.FullName sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $resolvedPfx /p $PfxPassword $exe
        if ($LASTEXITCODE -ne 0) {
            throw "Authenticode signing failed for $exe"
        }
    }
}

$desktopRuntime = Join-Path $runtimeRoot 'desktop'
$serviceRuntime = Join-Path $runtimeRoot 'service'
New-Item -ItemType Directory -Force -Path $desktopRuntime, $serviceRuntime | Out-Null
Copy-Item -Path (Join-Path $desktopPublish '*') -Destination $desktopRuntime -Recurse -Force
Copy-Item -Path (Join-Path $servicePublish '*') -Destination $serviceRuntime -Recurse -Force

$packageName = "TaskMonitoring.EmployeeRuntime-$Version-$Runtime.zip"
$packagePath = Join-Path $releaseRoot $packageName
Compress-Archive -Path (Join-Path $runtimeRoot '*') -DestinationPath $packagePath -CompressionLevel Optimal

$updaterDestination = Join-Path $releaseRoot 'updater'
New-Item -ItemType Directory -Force -Path $updaterDestination | Out-Null
Copy-Item -Path (Join-Path $updaterPublish '*') -Destination $updaterDestination -Recurse -Force

$packageUrl = $null
if (-not [string]::IsNullOrWhiteSpace($PackageBaseUrl)) {
    $packageUrl = $PackageBaseUrl.TrimEnd('/') + '/' + $packageName
}

$manifest = [ordered]@{
    schemaVersion = 1
    channel = $Channel
    version = $Version
    publishedAtUtc = [DateTime]::UtcNow.ToString('O')
    minimumUpdaterVersion = $MinimumUpdaterVersion
    package = [ordered]@{
        file = $packageName
        url = $packageUrl
        sha256 = (Get-FileHash -Algorithm SHA256 -Path $packagePath).Hash.ToUpperInvariant()
        sizeBytes = (Get-Item $packagePath).Length
    }
}
$manifestPath = Join-Path $releaseRoot 'release.json'
$json = $manifest | ConvertTo-Json -Depth 8
[IO.File]::WriteAllText($manifestPath, $json, [Text.UTF8Encoding]::new($false))

$bundleFiles = @(
    'install-employee-windows.ps1',
    'uninstall-employee-windows.ps1',
    'deployment-common.ps1'
)
foreach ($file in $bundleFiles) {
    Copy-Item -Path (Join-Path $PSScriptRoot $file) -Destination (Join-Path $releaseRoot $file) -Force
}

if ($null -ne $signingCertificate) {
    foreach ($file in $bundleFiles) {
        $signedPath = Join-Path $releaseRoot $file
        $signature = Set-AuthenticodeSignature -FilePath $signedPath -Certificate $signingCertificate -HashAlgorithm SHA256 -TimestampServer $TimestampUrl
        if ($signature.Status -ne [Management.Automation.SignatureStatus]::Valid) {
            throw "PowerShell Authenticode signing failed for $signedPath. Status=$($signature.Status)"
        }
    }

    [IO.File]::WriteAllText(
        (Join-Path $releaseRoot 'publisher-certificate-sha256.txt'),
        $publisherFingerprint,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $releaseRoot 'publisher-certificate.cer'),
        $signingCertificate.Export([Security.Cryptography.X509Certificates.X509ContentType]::Cert))
}

Write-Host "Release bundle: $releaseRoot"
Write-Host "Runtime package: $packagePath"
Write-Host "Manifest SHA-256: $((Get-FileHash -Algorithm SHA256 -Path $manifestPath).Hash)"
if ($publisherFingerprint) {
    Write-Host "Publisher certificate SHA-256: $publisherFingerprint"
}
