<#
.SYNOPSIS
  Builds and tests the whole solution, including real-browser tests, inside the pinned verification image.

.DESCRIPTION
  Standard verification for every phase (Windows Smart App Control stays enabled on the host, ADR-021).
  - Image: automataflow-verify:pw-<version> from deploy/verify/Dockerfile (ADR-033). Built automatically if missing;
    <version> is the Microsoft.Playwright pin in Directory.Packages.props (single source of truth).
  - Chromium runs WITH its sandbox, as a non-root user, under deploy/verify/chromium-seccomp.json (ADR-035).
  - Source is copied into the container (bin/obj/.git excluded); the working tree is never modified.
  - Database tests use the compose PostgreSQL (port from deploy/.env, default 5433) via host.docker.internal.

  Prerequisites: Docker running, deploy/.env created from deploy/.env.example, and
    docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d postgres
#>
[CmdletBinding()]
param(
    [switch]$RebuildImage
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

$packages = [xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)
$playwright = ($packages.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.Playwright' }).Version
if (-not $playwright) { throw 'Microsoft.Playwright version not found in Directory.Packages.props.' }
$image = "automataflow-verify:pw-$playwright"

$ErrorActionPreference = 'Continue'   # native stderr (docker build progress, test failures) must not abort the script
docker image inspect $image *> $null
if ($RebuildImage -or $LASTEXITCODE -ne 0) {
    Write-Host "Building verification image $image ..."
    docker build --build-arg "PLAYWRIGHT_VERSION=$playwright" -t $image (Join-Path $root 'deploy/verify')
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$script = @'
set -e
mkdir -p ~/work && cd /src
tar cf - --exclude=./.git --exclude=./.vs --exclude=bin --exclude=obj . | tar xf - -C ~/work
cd ~/work
dotnet build AutomataFlow.slnx
dotnet test AutomataFlow.slnx --no-build
'@ -replace "`r", ''

docker run --rm --init --shm-size=1g `
    --security-opt "seccomp=$(Join-Path $root 'deploy/verify/chromium-seccomp.json')" `
    -v "${root}:/src:ro" `
    -e "AUTOMATAFLOW_TEST_DB=$testDb" `
    $image bash -c $script

exit $LASTEXITCODE
