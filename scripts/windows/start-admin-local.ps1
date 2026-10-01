param([switch]$ResetLocalDatabase,[switch]$NoBrowser)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest

function Step([string]$m){Write-Host "[TaskMonitoring] $m" -ForegroundColor Cyan}
function Need([string]$n,[string]$hint){if($null -eq (Get-Command $n -ErrorAction SilentlyContinue)){throw "$n is required. $hint"}}
function UrlReady([string]$url,[int]$seconds){$until=[DateTime]::UtcNow.AddSeconds($seconds);do{try{$r=Invoke-WebRequest -UseBasicParsing -Uri $url -TimeoutSec 4;if($r.StatusCode -ge 200 -and $r.StatusCode -lt 500){return $true}}catch{};Start-Sleep 2}while([DateTime]::UtcNow -lt $until);return $false}
function LoadEnv([string]$path){foreach($line in Get-Content $path){$t=$line.Trim();if(!$t -or $t.StartsWith('#')){continue};$i=$line.IndexOf('=');if($i -le 0){continue};$k=$line.Substring(0,$i).Trim();$v=$line.Substring($i+1);[Environment]::SetEnvironmentVariable($k,$v,'Process')}}
function DockerReady(){& docker info *> $null;return $LASTEXITCODE -eq 0}

$repo=(Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$windows=[IO.Path]::GetFullPath($env:WINDIR).TrimEnd('\')
if($repo.StartsWith($windows+'\',[StringComparison]::OrdinalIgnoreCase)){
  $docs=[Environment]::GetFolderPath([Environment+SpecialFolder]::MyDocuments);if(!$docs){$docs=$env:USERPROFILE}
  $target=Join-Path $docs 'TaskMonitoring'
  Step "Copying the project out of the protected Windows folder to $target"
  New-Item -ItemType Directory -Force $target|Out-Null
  & robocopy.exe $repo $target /E /R:1 /W:1 /XD node_modules bin obj artifacts .local | Out-Host
  if($LASTEXITCODE -gt 7){throw "Robocopy failed with exit code $LASTEXITCODE"}
  Start-Process (Join-Path $target 'Start-Admin.cmd') -WorkingDirectory $target
  return
}

Need docker 'Install Docker Desktop.';Need dotnet 'Install .NET 10 SDK.';Need node 'Install Node.js 24.';Need npm.cmd 'Install Node.js 24.'
if(-not ((& dotnet --version).Trim().StartsWith('10.'))){throw '.NET 10 SDK is required.'}
if([Version]((& node --version).Trim().TrimStart('v')) -lt [Version]'24.0.0'){throw 'Node.js 24 or newer is required.'}

if(-not (DockerReady)){
  $desktop=Join-Path $env:ProgramFiles 'Docker\Docker\Docker Desktop.exe';if(!(Test-Path $desktop)){throw 'Docker Desktop engine is not available.'}
  Step 'Starting Docker Desktop';Start-Process $desktop|Out-Null
  $until=[DateTime]::UtcNow.AddMinutes(2);while(-not (DockerReady) -and [DateTime]::UtcNow -lt $until){Start-Sleep 3}
  if(-not (DockerReady)){throw 'Docker Desktop did not become ready within two minutes.'}
}

$envFile=Join-Path $repo '.env';if(!(Test-Path $envFile)){Copy-Item (Join-Path $repo '.env.example') $envFile;throw 'Created .env. Edit its database/JWT/bootstrap values once, then double-click Start-Admin.cmd again.'}
$content=Get-Content $envFile -Raw
if($content -match 'replace-with-' -or $content -notmatch '(?m)^BootstrapAdmin__Email=.+$' -or $content -notmatch '(?m)^BootstrapAdmin__Password=.{12,}$'){throw 'Configure .env first: replace placeholder DB/JWT values and set a bootstrap admin password of at least 12 characters.'}
LoadEnv $envFile
$env:ASPNETCORE_URLS='http://127.0.0.1:5080';$env:ASPNETCORE_ENVIRONMENT='Development';$env:VITE_DEV_API_TARGET='http://127.0.0.1:5080'

if($ResetLocalDatabase){Step 'Resetting local database';& docker compose --env-file $envFile down -v;if($LASTEXITCODE -ne 0){throw 'Database reset failed.'}}
Step 'Starting PostgreSQL';& docker compose --env-file $envFile up -d postgres;if($LASTEXITCODE -ne 0){throw 'PostgreSQL start failed.'}
$id=(& docker compose --env-file $envFile ps -q postgres).Trim();$until=[DateTime]::UtcNow.AddSeconds(60);do{$health=(& docker inspect --format='{{.State.Health.Status}}' $id).Trim();if($health -eq 'healthy'){break};Start-Sleep 2}while([DateTime]::UtcNow -lt $until);if($health -ne 'healthy'){throw "PostgreSQL health is $health"}

$api='http://127.0.0.1:5080/health/live'
if(-not (UrlReady $api 2)){
  Step 'Starting API';$project=Join-Path $repo 'src\Backend\TaskMonitoring.Api\TaskMonitoring.Api.csproj'
  Start-Process dotnet -ArgumentList @('run','--project',$project) -WorkingDirectory $repo|Out-Null
  if(-not (UrlReady $api 120)){throw 'API did not become healthy. If this is a local password mismatch, run Start-Admin.cmd -ResetLocalDatabase once.'}
}

$web=Join-Path $repo 'src\AdminWeb'
if(!(Test-Path (Join-Path $web 'node_modules'))){Step 'Installing Admin Web dependencies (first run only)';Push-Location $web;try{& npm.cmd install --no-audit --no-fund;if($LASTEXITCODE -ne 0){throw 'npm install failed.'}}finally{Pop-Location}}
$admin='http://127.0.0.1:5173'
if(-not (UrlReady $admin 2)){
  Step 'Starting Admin Web';Start-Process npm.cmd -ArgumentList @('run','dev','--','--host','127.0.0.1') -WorkingDirectory $web|Out-Null
  if(-not (UrlReady $admin 120)){throw 'Admin Web did not become reachable.'}
}

Write-Host "TaskMonitoring Admin is ready: $admin" -ForegroundColor Green
if(-not $NoBrowser){Start-Process $admin}
