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
  - Step 7 (ADR-075): a second, SUPERVISION container with the production Worker posture (root launcher; cap_drop ALL
    plus NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT; same seccomp profile) runs the real launcher, supervisor and
    Worker for the process-death tests. Network, volume and container are created per run and always removed.

  Prerequisites: Docker running, deploy/.env created from deploy/.env.example, and
    docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d postgres
#>
[CmdletBinding()]
param(
    [switch]$RebuildImage,

    # Optional `dotnet test --filter` expression for targeted runs during development. Phase verification always
    # runs WITHOUT a filter (the complete suite).
    [string]$Filter,

    # Skip the dashboard checks (lint, type check, unit tests, build, API-type check) for a backend-only targeted run.
    [switch]$SkipWeb
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

# ---------------------------------------------------------------------------------------------------------------------
# Step 7 (ADR-075): besides the main test container, a SUPERVISION container with the exact production Worker posture
# (root launcher; cap_drop ALL + NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT; same seccomp profile) runs the
# real launcher, supervisor and Worker, driven by a test-only root agent over a Unix socket on a shared volume. Both
# containers mount the volume at /work, so build output, keys and artifacts have identical paths in both. They share a
# private network: the main container is "tests", the supervision container is "workers". Never --privileged, never
# CAP_SYS_ADMIN. Everything is removed afterwards.
# ---------------------------------------------------------------------------------------------------------------------
$runId = [guid]::NewGuid().ToString('N').Substring(0, 10)
$network = "af-verify-$runId"
$volume = "af-work-$runId"
$supervision = "af-supervision-$runId"
$seccomp = Join-Path $root 'deploy/verify/chromium-seccomp.json'

$script = @'
set -e
cd /src
tar cf - --exclude=./.git --exclude=./.vs --exclude=bin --exclude=obj --exclude=node_modules --exclude=./web/automataflow-web/dist . | tar xf - -C /work/src
cd /work/src
dotnet build AutomataFlow.slnx
touch /work/.built
dotnet test AutomataFlow.slnx --no-build __FILTER__
'@ -replace "`r", ''
$filterArgument = if ($Filter) { "--filter '" + ($Filter -replace "'", "") + "'" } else { '' }
$script = $script.Replace('__FILTER__', $filterArgument)

$agent = 'while [ ! -f /work/.built ]; do sleep 1; done; exec dotnet /work/src/tests/AutomataFlow.SupervisionAgent/bin/Debug/net10.0/AutomataFlow.SupervisionAgent.dll /work/agent/agent.sock /work/agent/logs'

$exitCode = 1
try {
    docker network create $network | Out-Null
    docker volume create $volume | Out-Null
    # One-off ownership setup of the empty volume (the default capability set is used only for this chown step).
    docker run --rm --user root -v "${volume}:/work" $image sh -c 'mkdir -p /work/src /work/shared /work/agent/logs && chown -R 10001:10001 /work /work/shared && chmod 1777 /work/agent /work/agent/logs'
    if ($LASTEXITCODE -ne 0) { throw 'volume setup failed' }

    docker run -d --name $supervision --network $network --network-alias workers --init --user root --shm-size=1g `
        --cap-drop ALL --cap-add NET_ADMIN --cap-add SETUID --cap-add SETGID --cap-add SETPCAP --cap-add SYS_CHROOT `
        --security-opt "seccomp=$seccomp" -v "${volume}:/work" $image bash -c $agent | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'supervision container failed to start' }

    docker run --rm --init --shm-size=1g --network $network --network-alias tests `
        --security-opt "seccomp=$seccomp" `
        -v "${root}:/src:ro" -v "${volume}:/work" `
        -e "AUTOMATAFLOW_TEST_DB=$testDb" `
        -e "AUTOMATAFLOW_SUPERVISION_AGENT=/work/agent/agent.sock" `
        -e "AUTOMATAFLOW_SHARED_DIRECTORY=/work/shared" `
        $image bash -c $script
    $exitCode = $LASTEXITCODE

    # Dashboard (ADR-076): lint, type check, unit/component tests, production build and the API-type check, in the pinned
    # Node image. The source is copied; node_modules is installed from the committed lockfile inside the container.
    if ($exitCode -eq 0 -and -not $SkipWeb) {
        $node = (Get-Content (Join-Path $root 'web/automataflow-web/.node-image') -Raw).Trim()
        $web = 'set -e; mkdir -p /tmp/web && cd /src/web/automataflow-web && tar cf - --exclude=node_modules --exclude=dist . | tar xf - -C /tmp/web && cd /tmp/web && npm ci --no-audit --no-fund --loglevel=error && npm run lint && npm run typecheck && npm test && npm run build && npm run check:api'
        Write-Host '--- dashboard checks ---'
        docker run --rm -v "${root}:/src:ro" $node sh -c $web
        $exitCode = $LASTEXITCODE
    }

    # Deployment configuration validation (Phase 7, ADR-078): the shipped production compose must pass and every known
    # dangerous drift must be refused. Host-side (docker compose config only). Skipped for targeted -Filter runs.
    if ($exitCode -eq 0 -and -not $Filter) {
        Write-Host '--- deployment validation ---'
        & (Join-Path $root 'scripts/test-deployment-validation.ps1')
        $exitCode = $LASTEXITCODE
    }
}
finally {
    if ($exitCode -ne 0) {
        Write-Host "--- supervision container log (tail) ---"
        docker logs --tail 80 $supervision 2>&1 | Write-Host
    }
    docker rm -f $supervision 2>&1 | Out-Null
    docker volume rm -f $volume 2>&1 | Out-Null
    docker network rm $network 2>&1 | Out-Null
}

exit $exitCode