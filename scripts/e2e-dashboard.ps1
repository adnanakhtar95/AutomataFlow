<#
.SYNOPSIS
  Phase 6 acceptance: the dashboard journey in a real browser against the release-built stack.

.DESCRIPTION
  1. Builds the release images: Api (deploy/api), Worker (deploy/worker), dashboard (deploy/web) and the DemoCrm TEST
     FIXTURE (deploy/e2e/democrm.Dockerfile, never published).
  2. Starts deploy/e2e/docker-compose.e2e.yml (project af-e2e) with secrets generated for this run only.
  3. Runs web/automataflow-web/e2e in the official Playwright image (pinned to the Microsoft.Playwright version) inside
     the dashboard container's network namespace, so the dashboard is http://localhost:8080.
  4. Always removes the stack and its volumes. Use -KeepStack to inspect it afterwards (then run
     `docker compose -p af-e2e down -v` yourself).
#>
[CmdletBinding()]
param([switch]$KeepStack, [switch]$SkipBuild)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = [xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)
$playwright = ($packages.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.Playwright' }).Version
$tag = (git -C $root rev-parse --short HEAD).Trim() + '-local'
$compose = Join-Path $root 'deploy/e2e/docker-compose.e2e.yml'

function New-Secret([int]$bytes) {
    $buffer = New-Object byte[] $bytes
    $rng = [Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($buffer) } finally { $rng.Dispose() }
    [Convert]::ToBase64String($buffer) -replace '[+/=]', 'x'
}

$env:AF_E2E_API_IMAGE = "automataflow-api:$tag"
$env:AF_E2E_WORKER_IMAGE = "automataflow-worker:$tag"
$env:AF_E2E_WEB_IMAGE = "automataflow-web:$tag"
$env:AF_E2E_DEMOCRM_IMAGE = "automataflow-democrm-fixture:$tag"
$env:AF_E2E_DB_PASSWORD = New-Secret 24
$env:AF_E2E_JWT_KEY = New-Secret 48
$env:AF_E2E_DEMOCRM_TOKEN = New-Secret 24
$env:AF_E2E_OPERATOR_EMAIL = 'operator@platform.example.test'
$env:AF_E2E_OPERATOR_PASSWORD = 'Op-' + (New-Secret 18)

$ErrorActionPreference = 'Continue'
if (-not $SkipBuild) {
    docker build -q -f (Join-Path $root 'deploy/api/Dockerfile') -t $env:AF_E2E_API_IMAGE $root; if ($LASTEXITCODE -ne 0) { exit 1 }
    docker build -q -f (Join-Path $root 'deploy/worker/Dockerfile') --build-arg "PLAYWRIGHT_VERSION=$playwright" -t $env:AF_E2E_WORKER_IMAGE $root; if ($LASTEXITCODE -ne 0) { exit 1 }
    docker build -q -f (Join-Path $root 'deploy/web/Dockerfile') -t $env:AF_E2E_WEB_IMAGE $root; if ($LASTEXITCODE -ne 0) { exit 1 }
    docker build -q -f (Join-Path $root 'deploy/e2e/democrm.Dockerfile') -t $env:AF_E2E_DEMOCRM_IMAGE $root; if ($LASTEXITCODE -ne 0) { exit 1 }
}

$exitCode = 1
try {
    docker compose -f $compose up -d --wait --wait-timeout 300
    if ($LASTEXITCODE -ne 0) { throw 'the e2e stack did not become healthy' }

    # Phase 8: the first platform operator, created by the Api's bootstrap command (credentials via environment only).
    docker compose -f $compose run --rm --no-deps -e AF_BOOTSTRAP_PASSWORD=$env:AF_E2E_OPERATOR_PASSWORD -e AF_BOOTSTRAP_EMAIL=$env:AF_E2E_OPERATOR_EMAIL api bootstrap-operator
    if ($LASTEXITCODE -ne 0) { throw 'bootstrap-operator failed' }

    $runner = 'set -e; mkdir -p /tmp/web && cd /repo/web/automataflow-web && tar cf - --exclude=node_modules --exclude=dist --exclude=test-results --exclude=playwright-report . | tar xf - -C /tmp/web && cd /tmp/web && npm ci --no-audit --no-fund --loglevel=error && status=0 && { npx playwright test || status=$?; }; cp -r playwright-report test-results /out/ 2>/dev/null || true; exit $status'
    $out = Join-Path $root 'web/automataflow-web/.e2e-output'
    New-Item -ItemType Directory -Force $out | Out-Null
    docker run --rm --network container:af-e2e-web-1 --ipc=host `
        -e AF_E2E_REPO=/repo -e AF_E2E_BASE_URL=http://localhost:8080 -e CI=1 -e AF_E2E_OPERATOR_EMAIL -e AF_E2E_OPERATOR_PASSWORD `
        -v "${root}:/repo:ro" -v "${out}:/out" `
        "mcr.microsoft.com/playwright:v$playwright-noble" bash -c $runner
    $exitCode = $LASTEXITCODE
}
finally {
    if ($exitCode -ne 0) {
        docker compose -f $compose logs --no-color --tail 120 api worker 2>&1 | Write-Host
    }

    if (-not $KeepStack) {
        docker compose -f $compose down -v --remove-orphans 2>&1 | Out-Null
    }
}

exit $exitCode
