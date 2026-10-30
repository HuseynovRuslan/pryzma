<#
.SYNOPSIS
  Local PostgreSQL for MEYDAN development and integration tests — no Docker, no admin rights.

.DESCRIPTION
  Uses an installed PostgreSQL (e.g. C:\Program Files\PostgreSQL\18) to run a private cluster owned by the
  current user, outside the repository. It never touches the system PostgreSQL service.

  First run: initialises the cluster (UTF-8) with a random password for role "meydan", starts it, creates the
  development database and stores the connection strings in .NET user-secrets:
    backend/src/Meydan.Api            ConnectionStrings:Meydan              (dev database)
    backend/tests/Meydan.Api.Tests    TestDatabase:AdminConnectionString    (tests create/drop their own DBs)
  Later runs just start the cluster if it is stopped.

.EXAMPLE
  ./scripts/dev-postgres.ps1            # init (first time) and start
  ./scripts/dev-postgres.ps1 -Stop
#>
param(
  [string]$PgBin = $env:MEYDAN_PG_BIN,
  [string]$DataDir = (Join-Path $env:LOCALAPPDATA 'Meydan\pg-dev'),
  [int]$Port = 55433,
  [switch]$Stop
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot

if (-not $PgBin) {
  $PgBin = Get-ChildItem 'C:\Program Files\PostgreSQL\*\bin\pg_ctl.exe' -ErrorAction SilentlyContinue |
    Sort-Object FullName -Descending | Select-Object -First 1 | ForEach-Object { $_.DirectoryName }
}
if (-not $PgBin -or -not (Test-Path (Join-Path $PgBin 'pg_ctl.exe'))) {
  throw 'PostgreSQL binaries not found. Install PostgreSQL 16+ or pass -PgBin <path to bin>.'
}
$pgCtl = Join-Path $PgBin 'pg_ctl.exe'
$log = Join-Path (Split-Path $DataDir) 'pg-dev.log'

if ($Stop) {
  Start-Process -FilePath $pgCtl -WindowStyle Hidden -Wait -ArgumentList @('-D', "`"$DataDir`"", 'stop', '-m', 'fast')
  return
}

$secretFile = Join-Path (Split-Path $DataDir) 'pg-dev.password'
$fresh = -not (Test-Path (Join-Path $DataDir 'PG_VERSION'))
if ($fresh) {
  New-Item -ItemType Directory -Force (Split-Path $DataDir) | Out-Null
  $bytes = New-Object byte[] 24
  [System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
  $password = [Convert]::ToBase64String($bytes).Replace('+', 'A').Replace('/', 'B').TrimEnd('=')
  Set-Content -Path $secretFile -Value $password -NoNewline -Encoding ascii
  $pwFile = [System.IO.Path]::GetTempFileName()
  Set-Content -Path $pwFile -Value $password -NoNewline -Encoding ascii
  try {
    & (Join-Path $PgBin 'initdb.exe') -D $DataDir -U meydan --pwfile=$pwFile -E UTF8 --locale=C `
      --auth-host=scram-sha-256 --auth-local=scram-sha-256 | Out-Null
  } finally {
    Remove-Item $pwFile -Force
  }
  Write-Host "Initialised cluster in $DataDir"
}
$password = Get-Content $secretFile -Raw

& $pgCtl -D $DataDir status *> $null
if ($LASTEXITCODE -ne 0) {
  # Start-Process: the server must not inherit this shell's output pipes, or the shell waits on it forever.
  Start-Process -FilePath $pgCtl -WindowStyle Hidden -Wait `
    -ArgumentList @('-D', "`"$DataDir`"", '-l', "`"$log`"", '-o', "`"-p $Port -c listen_addresses=localhost`"", 'start')
  $ready = Join-Path $PgBin 'pg_isready.exe'
  for ($i = 0; $i -lt 30; $i++) {
    & $ready -h localhost -p $Port *> $null
    if ($LASTEXITCODE -eq 0) { break }
    Start-Sleep -Milliseconds 500
  }
  Write-Host "PostgreSQL running on localhost:$Port"
}

# Idempotent: create the dev database if missing and (re)write the connection strings to user-secrets.
$psql = Join-Path $PgBin 'psql.exe'
$env:PGPASSWORD = $password
try {
  $exists = & $psql -h localhost -p $Port -U meydan -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname = 'meydan_dev'"
  if ($LASTEXITCODE -ne 0) { throw "Cannot connect to PostgreSQL on localhost:$Port (see $log)." }
  if ($exists -ne '1') {
    & $psql -h localhost -p $Port -U meydan -d postgres -v ON_ERROR_STOP=1 -q `
      -c "CREATE DATABASE meydan_dev ENCODING 'UTF8' TEMPLATE template0;"
    if ($LASTEXITCODE -ne 0) { throw 'Creating database meydan_dev failed.' }
    Write-Host 'Created database meydan_dev'
  }
} finally {
  Remove-Item Env:PGPASSWORD
}

$dev = "Host=localhost;Port=$Port;Database=meydan_dev;Username=meydan;Password=$password"
$admin = "Host=localhost;Port=$Port;Database=postgres;Username=meydan;Password=$password"
dotnet user-secrets set 'ConnectionStrings:Meydan' $dev --project (Join-Path $repo 'backend/src/Meydan.Api') | Out-Null
dotnet user-secrets set 'TestDatabase:AdminConnectionString' $admin --project (Join-Path $repo 'backend/tests/Meydan.Api.Tests') | Out-Null
Write-Host 'Connection strings stored in user-secrets (Meydan.Api and Meydan.Api.Tests).'
