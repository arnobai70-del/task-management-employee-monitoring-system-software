param(
    [switch]$ResetLocalDatabase,
    [switch]$NoBrowser,
    [switch]$LanTest,
    [ValidateSet('Auto', 'Native', 'Docker')]
    [string]$DatabaseMode = 'Auto',
    [string]$PostgresAdminUser = 'postgres',
    [ValidateRange(1, 65535)]
    [int]$PostgresPort = 5432,
    [switch]$ValidateOnly
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Step([string]$message) { Write-Host "[TaskMonitoring] $message" -ForegroundColor Cyan }
function Need([string]$name, [string]$hint) {
    if ($null -eq (Get-Command $name -ErrorAction SilentlyContinue)) {
        throw "$name is required. $hint"
    }
}
function UrlReady([string]$url, [int]$seconds) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    do {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 4
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 500) { return $true }
        } catch {}
        Start-Sleep 2
    } while ([DateTime]::UtcNow -lt $until)
    return $false
}
function LoadEnv([string]$path) {
    foreach ($line in Get-Content $path) {
        $trimmed = $line.Trim()
        if (!$trimmed -or $trimmed.StartsWith('#')) { continue }
        $index = $line.IndexOf('=')
        if ($index -le 0) { continue }
        $key = $line.Substring(0, $index).Trim()
        $value = $line.Substring($index + 1)
        [Environment]::SetEnvironmentVariable($key, $value, 'Process')
    }
}
function SetLocalApiDatabaseConnection([int]$port) {
    foreach ($name in @('POSTGRES_DB', 'POSTGRES_USER', 'POSTGRES_PASSWORD')) {
        $value = [Environment]::GetEnvironmentVariable($name, 'Process')
        if ([string]::IsNullOrWhiteSpace($value)) {
            throw "Local database configuration requires $name in .env."
        }
    }

    $builder = [System.Data.Common.DbConnectionStringBuilder]::new()
    $builder['Host'] = '127.0.0.1'
    $builder['Port'] = $port
    $builder['Database'] = $env:POSTGRES_DB
    $builder['Username'] = $env:POSTGRES_USER
    $builder['Password'] = $env:POSTGRES_PASSWORD
    $builder['Pooling'] = 'true'
    $builder['Timeout'] = 15
    $builder['Command Timeout'] = 30
    [Environment]::SetEnvironmentVariable('ConnectionStrings__DefaultConnection', $builder.ConnectionString, 'Process')
}
function DockerReady() {
    if ($null -eq (Get-Command docker -ErrorAction SilentlyContinue)) { return $false }
    & docker info *> $null
    return $LASTEXITCODE -eq 0
}
function DockerInstalled() {
    if ($null -ne (Get-Command docker -ErrorAction SilentlyContinue)) { return $true }
    return Test-Path (Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe')
}
function IsAdministrator() {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = [Security.Principal.WindowsPrincipal]::new($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
function GetLanIpv4() {
    try {
        return Get-NetIPAddress -AddressFamily IPv4 -ErrorAction Stop |
            Where-Object { $_.IPAddress -ne '127.0.0.1' -and $_.IPAddress -notlike '169.254.*' -and $_.PrefixOrigin -ne 'WellKnown' } |
            Sort-Object InterfaceMetric, SkipAsSource |
            Select-Object -ExpandProperty IPAddress -First 1
    } catch {
        return $null
    }
}
function FindPostgresTools() {
    $psqlCommand = Get-Command psql.exe -ErrorAction SilentlyContinue
    if ($null -ne $psqlCommand) {
        $bin = Split-Path -Parent $psqlCommand.Source
        $ready = Join-Path $bin 'pg_isready.exe'
        if (Test-Path $ready) {
            return [pscustomobject]@{ Psql = $psqlCommand.Source; PgIsReady = $ready; Bin = $bin }
        }
    }

    $roots = @()
    if ($env:ProgramFiles) { $roots += (Join-Path $env:ProgramFiles 'PostgreSQL') }
    $programFilesX86 = ${env:ProgramFiles(x86)}
    if ($programFilesX86) { $roots += (Join-Path $programFilesX86 'PostgreSQL') }

    foreach ($root in $roots) {
        if (!(Test-Path $root)) { continue }
        $installations = @(Get-ChildItem $root -Directory -ErrorAction SilentlyContinue | Sort-Object Name -Descending)
        foreach ($installation in $installations) {
            $bin = Join-Path $installation.FullName 'bin'
            $psql = Join-Path $bin 'psql.exe'
            $ready = Join-Path $bin 'pg_isready.exe'
            if ((Test-Path $psql) -and (Test-Path $ready)) {
                return [pscustomobject]@{ Psql = $psql; PgIsReady = $ready; Bin = $bin }
            }
        }
    }

    return $null
}
function AssertPostgres17([object]$tools) {
    $versionText = (& $tools.Psql --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $versionText -notmatch '(\d+)(?:\.\d+)?') {
        throw 'Could not determine the installed PostgreSQL version.'
    }
    $major = [int]$Matches[1]
    if ($major -lt 17) {
        throw "PostgreSQL 17 or newer is required for native mode. Detected: $versionText"
    }
}
function GetPostgresService() {
    $services = @(Get-Service -Name 'postgresql*' -ErrorAction SilentlyContinue)
    if ($services.Count -eq 0) {
        $services = @(Get-Service -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'PostgreSQL*' })
    }
    return $services | Sort-Object @{ Expression = { if ($_.Status -eq 'Running') { 0 } else { 1 } } }, Name | Select-Object -First 1
}
function NativePostgresInstalled() {
    return $null -ne (FindPostgresTools)
}
function TestPostgresReady([object]$tools, [int]$port) {
    & $tools.PgIsReady -h 127.0.0.1 -p $port -t 2 *> $null
    return $LASTEXITCODE -eq 0
}
function EnsureNativePostgresRunning([object]$tools, [int]$port) {
    if (TestPostgresReady $tools $port) { return }

    $service = GetPostgresService
    if ($null -eq $service) {
        throw "PostgreSQL is installed but no Windows PostgreSQL service was found and port $port is not ready. Start PostgreSQL 17, then retry."
    }

    if ($service.Status -ne 'Running') {
        Step "Starting native PostgreSQL service $($service.Name)"
        try {
            Start-Service -Name $service.Name -ErrorAction Stop
        } catch {
            throw "Could not start PostgreSQL service '$($service.Name)'. Start it from Windows Services or run this launcher as Administrator. $($_.Exception.Message)"
        }
    }

    $until = [DateTime]::UtcNow.AddSeconds(60)
    do {
        if (TestPostgresReady $tools $port) { return }
        Start-Sleep 2
    } while ([DateTime]::UtcNow -lt $until)

    throw "PostgreSQL service is running but did not become ready on 127.0.0.1:$port within 60 seconds."
}
function SecureToPlainText([Security.SecureString]$secure) {
    $pointer = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
    try {
        return [Runtime.InteropServices.Marshal]::PtrToStringBSTR($pointer)
    } finally {
        [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($pointer)
    }
}
function SqlLiteral([string]$value) { return "'" + $value.Replace("'", "''") + "'" }
function SqlIdentifier([string]$value) { return '"' + $value.Replace('"', '""') + '"' }
function InvokePsql(
    [object]$tools,
    [string]$database,
    [string]$user,
    [string]$password,
    [string]$sql,
    [int]$port,
    [switch]$AllowFailure,
    [switch]$SensitiveSql
) {
    $previousPassword = $env:PGPASSWORD
    try {
        $env:PGPASSWORD = $password
        $output = $sql | & $tools.Psql -X -q -h 127.0.0.1 -p $port -U $user -d $database -v ON_ERROR_STOP=1 -w -tA 2>&1
        $exitCode = $LASTEXITCODE
        if ($exitCode -ne 0 -and !$AllowFailure) {
            if ($SensitiveSql) {
                throw 'PostgreSQL command failed while applying protected database credentials.'
            }
            $message = ($output | Out-String).Trim()
            throw "PostgreSQL command failed. $message"
        }
        return [pscustomobject]@{ ExitCode = $exitCode; Output = (($output | Out-String).Trim()) }
    } finally {
        $env:PGPASSWORD = $previousPassword
    }
}
function AppDatabaseReady([object]$tools, [string]$database, [string]$user, [string]$password, [int]$port) {
    $result = InvokePsql $tools $database $user $password 'SELECT 1;' $port -AllowFailure
    return $result.ExitCode -eq 0 -and $result.Output -match '1'
}
function EnsureNativeDatabase([object]$tools, [int]$port, [switch]$reset) {
    $database = $env:POSTGRES_DB
    $appUser = $env:POSTGRES_USER
    $appPassword = $env:POSTGRES_PASSWORD
    if ([string]::IsNullOrWhiteSpace($database) -or [string]::IsNullOrWhiteSpace($appUser) -or [string]::IsNullOrWhiteSpace($appPassword)) {
        throw 'Native PostgreSQL mode requires POSTGRES_DB, POSTGRES_USER and POSTGRES_PASSWORD in .env.'
    }
    if (@('postgres', 'template0', 'template1') -contains $database.ToLowerInvariant()) {
        throw "POSTGRES_DB '$database' is reserved. Choose an application-specific database name."
    }

    if (!$reset -and (AppDatabaseReady $tools $database $appUser $appPassword $port)) {
        Step "Native PostgreSQL database '$database' is ready"
        return
    }

    if ($reset) {
        Write-Warning "ResetLocalDatabase will permanently delete the native PostgreSQL database '$database'."
    } else {
        Step "Native database '$database' is not initialized for application user '$appUser'"
    }

    Write-Host "One-time PostgreSQL administrator authentication is required. The password is used only in this process and is not saved by TaskMonitoring." -ForegroundColor Yellow
    $secureAdminPassword = Read-Host "PostgreSQL administrator password for '$PostgresAdminUser'" -AsSecureString
    $adminPassword = SecureToPlainText $secureAdminPassword
    try {
        $adminProbe = InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword 'SELECT 1;' $port -AllowFailure
        if ($adminProbe.ExitCode -ne 0) {
            throw "Could not authenticate to local PostgreSQL as '$PostgresAdminUser'. Verify the administrator password and retry."
        }

        $dbLiteral = SqlLiteral $database
        $userLiteral = SqlLiteral $appUser
        $dbIdentifier = SqlIdentifier $database
        $userIdentifier = SqlIdentifier $appUser
        $passwordLiteral = SqlLiteral $appPassword

        if ($reset) {
            Step "Resetting native PostgreSQL database '$database'"
            [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "SELECT pg_terminate_backend(pid) FROM pg_stat_activity WHERE datname = $dbLiteral AND pid <> pg_backend_pid();" $port)
            $existsBeforeDrop = InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "SELECT 1 FROM pg_database WHERE datname = $dbLiteral;" $port
            if ($existsBeforeDrop.Output -match '1') {
                [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "DROP DATABASE $dbIdentifier;" $port)
            }
        }

        $roleExists = InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "SELECT 1 FROM pg_roles WHERE rolname = $userLiteral;" $port
        if ($roleExists.Output -notmatch '1') {
            Step "Creating PostgreSQL application role '$appUser'"
            [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "CREATE ROLE $userIdentifier LOGIN PASSWORD $passwordLiteral;" $port -SensitiveSql)
        } else {
            [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "ALTER ROLE $userIdentifier WITH LOGIN PASSWORD $passwordLiteral;" $port -SensitiveSql)
        }

        $databaseExists = InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "SELECT 1 FROM pg_database WHERE datname = $dbLiteral;" $port
        if ($databaseExists.Output -notmatch '1') {
            Step "Creating PostgreSQL database '$database'"
            [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "CREATE DATABASE $dbIdentifier OWNER $userIdentifier;" $port)
        } else {
            [void](InvokePsql $tools 'postgres' $PostgresAdminUser $adminPassword "ALTER DATABASE $dbIdentifier OWNER TO $userIdentifier;" $port)
        }
    } finally {
        $adminPassword = $null
        $secureAdminPassword.Dispose()
    }

    if (!(AppDatabaseReady $tools $database $appUser $appPassword $port)) {
        throw "Native PostgreSQL setup completed but the application user still cannot connect to '$database'."
    }
    Step "Native PostgreSQL database '$database' is ready"
}
function EnsureDockerDatabase([string]$envFile, [switch]$reset) {
    Need docker 'Install Docker Desktop, or use Start-Admin-Native.cmd with PostgreSQL 17 installed.'
    if (!(DockerReady)) {
        $desktop = Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe'
        if (!(Test-Path $desktop)) { throw 'Docker Desktop engine is not available.' }
        Step 'Starting Docker Desktop'
        Start-Process $desktop | Out-Null
        $until = [DateTime]::UtcNow.AddMinutes(2)
        while (!(DockerReady) -and [DateTime]::UtcNow -lt $until) { Start-Sleep 3 }
        if (!(DockerReady)) { throw 'Docker Desktop did not become ready within two minutes.' }
    }

    if ($reset) {
        Step 'Resetting Docker PostgreSQL database'
        & docker compose --env-file $envFile down -v
        if ($LASTEXITCODE -ne 0) { throw 'Database reset failed.' }
    }

    Step 'Starting Docker PostgreSQL'
    & docker compose --env-file $envFile up -d postgres
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL start failed.' }
    $id = (& docker compose --env-file $envFile ps -q postgres).Trim()
    if ([string]::IsNullOrWhiteSpace($id)) { throw 'Docker PostgreSQL container was not created.' }
    $until = [DateTime]::UtcNow.AddSeconds(60)
    $health = 'unknown'
    do {
        $health = (& docker inspect --format='{{.State.Health.Status}}' $id).Trim()
        if ($health -eq 'healthy') { break }
        Start-Sleep 2
    } while ([DateTime]::UtcNow -lt $until)
    if ($health -ne 'healthy') { throw "PostgreSQL health is $health" }
}
function ResolveDatabaseMode([string]$requestedMode) {
    if ($requestedMode -ne 'Auto') { return $requestedMode }
    if (NativePostgresInstalled) { return 'Native' }
    if (DockerInstalled) { return 'Docker' }
    throw 'No local database runtime was found. Install PostgreSQL 17 for Docker-free mode, or install Docker Desktop as a fallback.'
}

if ($ValidateOnly) {
    Write-Host "TaskMonitoring local launcher validation passed for DatabaseMode=$DatabaseMode."
    return
}

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windows = [IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\')
if ($repo.StartsWith($windows + '\', [StringComparison]::OrdinalIgnoreCase)) {
    $docs = [Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments)
    if (!$docs) { $docs = $env:USERPROFILE }
    $target = Join-Path $docs 'TaskMonitoring'
    Step "Copying the project out of the protected Windows folder to $target"
    New-Item -ItemType Directory -Force $target | Out-Null
    & robocopy.exe $repo $target /E /R:1 /W:1 /XD node_modules bin obj artifacts .local | Out-Host
    if ($LASTEXITCODE -gt 7) { throw "Robocopy failed with exit code $LASTEXITCODE" }
    $forward = @('-DatabaseMode', $DatabaseMode, '-PostgresAdminUser', $PostgresAdminUser, '-PostgresPort', [string]$PostgresPort)
    if ($ResetLocalDatabase) { $forward += '-ResetLocalDatabase' }
    if ($NoBrowser) { $forward += '-NoBrowser' }
    if ($LanTest) { $forward += '-LanTest' }
    Start-Process (Join-Path $target 'Start-Admin.cmd') -ArgumentList $forward -WorkingDirectory $target
    return
}

Need dotnet 'Install .NET 10 SDK.'
Need node 'Install Node.js 24.'
Need npm.cmd 'Install Node.js 24.'
if (!((& dotnet --version).Trim().StartsWith('10.'))) { throw '.NET 10 SDK is required.' }
if ([Version]((& node --version).Trim().TrimStart('v')) -lt [Version]'24.0.0') { throw 'Node.js 24 or newer is required.' }

$envFile = Join-Path $repo '.env'
if (!(Test-Path $envFile)) {
    Copy-Item (Join-Path $repo '.env.example') $envFile
    throw 'Created .env. Edit its database/JWT/bootstrap values once, then run Start-Admin.cmd again.'
}
$content = Get-Content $envFile -Raw
if ($content -match 'replace-with-' -or $content -notmatch '(?m)^BootstrapAdmin__Email=.+$' -or $content -notmatch '(?m)^BootstrapAdmin__Password=.{12,}$') {
    throw 'Configure .env first: replace placeholder DB/JWT values and set a bootstrap admin password of at least 12 characters.'
}
LoadEnv $envFile
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:VITE_DEV_API_TARGET = 'http://127.0.0.1:5080'

$selectedDatabaseMode = ResolveDatabaseMode $DatabaseMode
$apiDatabasePort = if ($selectedDatabaseMode -eq 'Native') { $PostgresPort } else { 5432 }
SetLocalApiDatabaseConnection $apiDatabasePort
Step "Database mode: $selectedDatabaseMode"
if ($selectedDatabaseMode -eq 'Native') {
    $postgresTools = FindPostgresTools
    if ($null -eq $postgresTools) { throw 'PostgreSQL 17 command-line tools were not found. Install PostgreSQL 17 and retry.' }
    AssertPostgres17 $postgresTools
    EnsureNativePostgresRunning $postgresTools $PostgresPort
    EnsureNativeDatabase $postgresTools $PostgresPort -reset:$ResetLocalDatabase
} else {
    EnsureDockerDatabase $envFile -reset:$ResetLocalDatabase
}

$localRoot = Join-Path $repo '.local'
New-Item -ItemType Directory -Force $localRoot | Out-Null
$modeFile = Join-Path $localRoot 'admin-database-mode.txt'
$lanServerFile = Join-Path $localRoot 'employee-test-server.txt'
$lanUrl = $null
if ($LanTest) {
    $lanIp = GetLanIpv4
    if ([string]::IsNullOrWhiteSpace($lanIp)) { throw 'Could not determine a private IPv4 address for LAN testing.' }
    $lanUrl = "http://$lanIp`:5080"
    $env:ASPNETCORE_URLS = 'http://0.0.0.0:5080'
    Write-Warning 'LAN TEST MODE exposes the development API over HTTP to the local private network. Do not use this mode for production.'

    $existing = @(Get-NetTCPConnection -State Listen -LocalPort 5080 -ErrorAction SilentlyContinue)
    $lanCapable = @($existing | Where-Object { $_.LocalAddress -eq '0.0.0.0' -or $_.LocalAddress -eq '::' -or $_.LocalAddress -eq $lanIp })
    if ((UrlReady 'http://127.0.0.1:5080/health/live' 2) -and $lanCapable.Count -eq 0) {
        throw 'The API is already running in localhost-only mode. Close the existing TaskMonitoring API window, then run the LAN launcher again.'
    }

    if ((IsAdministrator) -and $null -ne (Get-Command New-NetFirewallRule -ErrorAction SilentlyContinue)) {
        $ruleName = 'TaskMonitoring Development API LAN Test'
        if ($null -eq (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
            New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5080 -Profile Private -RemoteAddress LocalSubnet | Out-Null
        }
    } else {
        Write-Warning 'Could not create the Private-network firewall rule automatically. If another PC cannot connect, run this launcher as Administrator.'
    }
    [IO.File]::WriteAllText($lanServerFile, $lanUrl, [Text.UTF8Encoding]::new($false))
} else {
    $env:ASPNETCORE_URLS = 'http://127.0.0.1:5080'
    Remove-Item $lanServerFile -Force -ErrorAction SilentlyContinue
}

$api = 'http://127.0.0.1:5080/health/live'
if (UrlReady $api 2) {
    if (Test-Path $modeFile) {
        $runningMode = (Get-Content $modeFile -Raw).Trim()
        if ($runningMode -and $runningMode -ne $selectedDatabaseMode) {
            throw "The API is already running with database mode '$runningMode'. Close the existing TaskMonitoring API window before switching to '$selectedDatabaseMode'."
        }
    }
} else {
    Step 'Starting API'
    [IO.File]::WriteAllText($modeFile, $selectedDatabaseMode, [Text.UTF8Encoding]::new($false))
    $project = Join-Path $repo 'src\Backend\TaskMonitoring.Api\TaskMonitoring.Api.csproj'
    Start-Process dotnet -ArgumentList @('run', '--project', $project) -WorkingDirectory $repo | Out-Null
    if (!(UrlReady $api 120)) {
        throw 'API did not become healthy. Check the API console and PostgreSQL settings in .env.'
    }
}

if ($LanTest -and !(UrlReady ($lanUrl + '/health/live') 10)) {
    throw "LAN test API is not reachable at $lanUrl. Check the Windows network profile/firewall and ensure the active network is Private."
}

$web = Join-Path $repo 'src\AdminWeb'
if (!(Test-Path (Join-Path $web 'node_modules'))) {
    Step 'Installing Admin Web dependencies (first run only)'
    Push-Location $web
    try {
        & npm.cmd install --no-audit --no-fund
        if ($LASTEXITCODE -ne 0) { throw 'npm install failed.' }
    } finally {
        Pop-Location
    }
}
$admin = 'http://127.0.0.1:5173'
if (!(UrlReady $admin 2)) {
    Step 'Starting Admin Web'
    Start-Process npm.cmd -ArgumentList @('run', 'dev', '--', '--host', '127.0.0.1') -WorkingDirectory $web | Out-Null
    if (!(UrlReady $admin 120)) { throw 'Admin Web did not become reachable.' }
}

Write-Host "TaskMonitoring Admin is ready: $admin" -ForegroundColor Green
Write-Host "Database: $selectedDatabaseMode PostgreSQL" -ForegroundColor Green
if ($LanTest) {
    Write-Host "Employee LAN TEST server: $lanUrl" -ForegroundColor Yellow
    Write-Host 'Now double-click Build-Employee-Test-Installer.cmd to create the single EXE test installer.' -ForegroundColor Yellow
}
if (!$NoBrowser) { Start-Process $admin }
