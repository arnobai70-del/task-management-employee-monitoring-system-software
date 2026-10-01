param(
    [Parameter(Mandatory = $true)]
    [string]$ReleaseDirectory,

    [Parameter(Mandatory = $true)]
    [string]$ServerUrl,

    [string]$UpdateManifestUrl,
    [string]$PfxPath,
    [string]$PfxPassword,
    [string]$TimestampUrl = 'http://timestamp.digicert.com',
    [string]$Runtime = 'win-x64',
    [switch]$DevelopmentSetup
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-AbsoluteUrl {
    param([string]$Value, [string]$Name, [switch]$AllowHttp)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        throw "$Name must be supplied."
    }
    $uri = $null
    if (-not [Uri]::TryCreate($Value.Trim(), [UriKind]::Absolute, [ref]$uri)) {
        throw "$Name must be an absolute URL."
    }
    if ($uri.Scheme -ne [Uri]::UriSchemeHttps -and -not ($AllowHttp -and $uri.Scheme -eq [Uri]::UriSchemeHttp)) {
        throw "$Name must use HTTPS$(if ($AllowHttp) { ' or HTTP for development' } else { '' })."
    }
}

$releaseRoot = (Resolve-Path $ReleaseDirectory).Path
$manifestPath = Join-Path $releaseRoot 'release.json'
if (-not (Test-Path $manifestPath -PathType Leaf)) {
    throw "release.json was not found in $releaseRoot"
}
$manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
$version = [string]$manifest.version
if ($version -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw 'Release manifest version is invalid.'
}

Assert-AbsoluteUrl -Value $ServerUrl -Name 'ServerUrl' -AllowHttp:$DevelopmentSetup
if (-not [string]::IsNullOrWhiteSpace($UpdateManifestUrl)) {
    Assert-AbsoluteUrl -Value $UpdateManifestUrl -Name 'UpdateManifestUrl' -AllowHttp:$DevelopmentSetup
}
if (-not $DevelopmentSetup -and [string]::IsNullOrWhiteSpace($UpdateManifestUrl)) {
    throw 'Production standalone setup requires -UpdateManifestUrl.'
}
if ($DevelopmentSetup -and -not [string]::IsNullOrWhiteSpace($PfxPath)) {
    throw 'DevelopmentSetup must not be combined with a production signing certificate.'
}

$setupServerUrl = $ServerUrl.Trim().TrimEnd('/')
$setupManifestUrl = if ([string]::IsNullOrWhiteSpace($UpdateManifestUrl)) { '' } else { $UpdateManifestUrl.Trim() }

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$setupProject = Join-Path $repoRoot 'src\EmployeeSetup\TaskMonitoring.EmployeeSetup\TaskMonitoring.EmployeeSetup.csproj'
$workRoot = Join-Path ([IO.Path]::GetTempPath()) ('TaskMonitoring.SetupBuild-' + [Guid]::NewGuid().ToString('N'))
$payloadRoot = Join-Path $workRoot 'payload'
$payloadZip = Join-Path $workRoot 'TaskMonitoring.Payload.zip'
$setupPublish = Join-Path $workRoot 'setup'
New-Item -ItemType Directory -Force -Path $payloadRoot, $setupPublish | Out-Null

try {
    Get-ChildItem -Path $releaseRoot -Force | Where-Object { $_.Name -ne 'TaskMonitoring.EmployeeSetup.exe' } | ForEach-Object {
        Copy-Item -Path $_.FullName -Destination $payloadRoot -Recurse -Force
    }

    $required = @(
        'release.json',
        'install-employee-windows.ps1',
        'deployment-common.ps1',
        'uninstall-employee-windows.ps1',
        'rollback-employee-windows.ps1',
        'run-central-agent-update.ps1'
    )
    foreach ($file in $required) {
        if (-not (Test-Path (Join-Path $payloadRoot $file) -PathType Leaf)) {
            throw "Standalone payload is missing $file"
        }
    }
    if (-not (Test-Path (Join-Path $payloadRoot 'updater\TaskMonitoring.EmployeeUpdater.exe') -PathType Leaf)) {
        throw 'Standalone payload is missing the updater executable.'
    }

    Compress-Archive -Path (Join-Path $payloadRoot '*') -DestinationPath $payloadZip -CompressionLevel Optimal

    $publisherFingerprint = ''
    $resolvedPfx = $null
    $signTool = $null
    if (-not $DevelopmentSetup) {
        if ([string]::IsNullOrWhiteSpace($PfxPath)) {
            throw 'Production standalone setup requires -PfxPath.'
        }
        $resolvedPfx = (Resolve-Path $PfxPath).Path
        $certificate = [Security.Cryptography.X509Certificates.X509Certificate2]::new(
            $resolvedPfx,
            $PfxPassword,
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::Exportable)
        try {
            if (-not $certificate.HasPrivateKey) {
                throw 'The supplied PFX does not contain a private key.'
            }
            $sha = [Security.Cryptography.SHA256]::Create()
            try {
                $publisherFingerprint = (($sha.ComputeHash($certificate.RawData) | ForEach-Object { $_.ToString('X2') }) -join '')
            }
            finally {
                $sha.Dispose()
            }
        }
        finally {
            $certificate.Dispose()
        }

        $fingerprintFile = Join-Path $releaseRoot 'publisher-certificate-sha256.txt'
        if (Test-Path $fingerprintFile -PathType Leaf) {
            $bundleFingerprint = (Get-Content $fingerprintFile -Raw).Trim().ToUpperInvariant()
            if ($bundleFingerprint -ne $publisherFingerprint) {
                throw 'Standalone setup signing certificate does not match the release bundle publisher certificate.'
            }
        }

        $signTool = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\signtool.exe" -ErrorAction SilentlyContinue |
            Sort-Object FullName -Descending |
            Select-Object -First 1
        if ($null -eq $signTool) {
            throw 'signtool.exe was not found. Install the Windows SDK signing tools.'
        }
    }

    $developmentValue = if ($DevelopmentSetup) { 'true' } else { 'false' }
    dotnet publish $setupProject `
        --configuration Release `
        --runtime $Runtime `
        --self-contained true `
        --output $setupPublish `
        -p:Version=$version `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:DebugType=None `
        -p:DebugSymbols=false `
        "-p:TaskMonitoringServerUrl=$setupServerUrl" `
        "-p:TaskMonitoringUpdateManifestUrl=$setupManifestUrl" `
        "-p:TaskMonitoringPublisherSha256=$publisherFingerprint" `
        "-p:TaskMonitoringDevelopmentSetup=$developmentValue" `
        "-p:TaskMonitoringPayloadZip=$payloadZip"
    if ($LASTEXITCODE -ne 0) {
        throw 'dotnet publish failed while creating the standalone employee setup.'
    }

    $setupExe = Join-Path $setupPublish 'TaskMonitoring.EmployeeSetup.exe'
    if (-not (Test-Path $setupExe -PathType Leaf)) {
        throw 'Standalone setup executable was not produced.'
    }
    if ((Get-Item $setupExe).Length -le (Get-Item $payloadZip).Length) {
        throw 'Standalone setup size check failed; the embedded payload was not included as expected.'
    }

    if (-not $DevelopmentSetup) {
        & $signTool.FullName sign /fd SHA256 /td SHA256 /tr $TimestampUrl /f $resolvedPfx /p $PfxPassword $setupExe
        if ($LASTEXITCODE -ne 0) {
            throw 'Authenticode signing failed for the standalone employee setup.'
        }
    }

    Copy-Item -Path $setupExe -Destination (Join-Path $releaseRoot 'TaskMonitoring.EmployeeSetup.exe') -Force
    Write-Host "Standalone employee setup: $(Join-Path $releaseRoot 'TaskMonitoring.EmployeeSetup.exe')"
    Write-Host "Embedded payload bytes: $((Get-Item $payloadZip).Length)"
    if ($DevelopmentSetup) {
        Write-Warning 'This is an unsigned development test installer. Do not distribute it as a production release.'
    }
}
finally {
    Remove-Item -Recurse -Force $workRoot -ErrorAction SilentlyContinue
}
