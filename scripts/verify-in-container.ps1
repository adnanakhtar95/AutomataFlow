<#
.SYNOPSIS
  Builds and tests the solution inside the official .NET 10 SDK Linux container.

.DESCRIPTION
  Windows Smart App Control (enforcement mode) can block freshly built, unsigned assemblies on the host, which
  makes `dotnet test` unreliable on some machines. This script copies the source (excluding bin/obj/.git) into a
  throwaway container, builds with warnings as errors, and runs all tests against the compose PostgreSQL server.
  Nothing in the working tree is modified.

  Prerequisites: Docker running, deploy/.env created from deploy/.env.example, and the postgres service started:
    docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d postgres
#>
[CmdletBinding()]
param(
    [string]$Image = 'mcr.microsoft.com/dotnet/sdk:10.0'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envFile = Join-Path $root 'deploy/.env'
if (-not (Test-Path $envFile)) {
    throw "deploy/.env not found. Copy deploy/.env.example to deploy/.env and set POSTGRES_PASSWORD."
}

$settings = @{}
Get-Content $envFile | Where-Object { $_ -match '^\s*[^#=\s][^=]*=' } | ForEach-Object {
    $name, $value = $_ -split '=', 2
    $settings[$name.Trim()] = $value.Trim()
}

$port = if ($settings.ContainsKey('POSTGRES_HOST_PORT')) { $settings['POSTGRES_HOST_PORT'] } else { '5433' }
$testDb = "Host=host.docker.internal;Port=$port;Database=postgres;Username=$($settings['POSTGRES_USER']);Password=$($settings['POSTGRES_PASSWORD'])"

$script = @'
set -e
mkdir /work && cd /src
tar cf - --exclude=./.git --exclude=./.vs --exclude=bin --exclude=obj . | tar xf - -C /work
cd /work
dotnet build AutomataFlow.slnx
dotnet test AutomataFlow.slnx --no-build
'@ -replace "`r", ''

$ErrorActionPreference = 'Continue'   # native stderr (e.g. test failures) must not abort before the exit code is read
docker run --rm `
    -v "${root}:/src:ro" `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 `
    -e "AUTOMATAFLOW_TEST_DB=$testDb" `
    $Image bash -c $script

exit $LASTEXITCODE
