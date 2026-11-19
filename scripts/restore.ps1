<#
.SYNOPSIS
  Restores a backup made by scripts/backup.ps1 into a deployment whose database is EMPTY (a fresh deployment, or one
  whose volumes were deliberately removed). Never restores over existing data.

.DESCRIPTION
  Order (docs/DEPLOYMENT.md): verify checksums -> start postgres only (its init script creates the roles and the empty
  database) -> refuse if the database already has tables -> pg_restore as the migration OWNER (objects owned by the
  owner; the application role gets its rights from the default privileges) -> restore the key ring and artifacts
  volumes (refuses non-empty volumes) -> run `migrate` (applies migrations newer than the backup) -> start everything.
  The same deploy/.env.prod secrets and key ring certificate as the backed-up deployment are required.
  RUNBOOK rule: a restored deployment never resumes the old Workers' in-flight attempts on its own; unresolved attempts
  surface as AwaitingFence / NeedsReview and follow the operator procedure.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Backup,
    [string]$EnvFile = 'deploy/.env.prod',
    [string]$Project = 'automataflow-prod'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$source = (Resolve-Path -LiteralPath $Backup).Path
$envPath = if ([IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $root $EnvFile }
$compose = @('compose', '--env-file', $envPath, '-p', $Project, '-f', (Join-Path $root 'deploy/docker-compose.prod.yml'))
$helper = 'postgres:18.6@sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42'

# 1. Checksums.
foreach ($line in Get-Content (Join-Path $source 'SHA256SUMS')) {
    $hash, $name = $line -split '\s+', 2
    if ((Get-FileHash (Join-Path $source $name) -Algorithm SHA256).Hash.ToLowerInvariant() -ne $hash) { throw "checksum mismatch: $name" }
}
Write-Host 'Checksums verified.'

$ErrorActionPreference = 'Continue'
# 2. Database (empty target only).
& docker @compose up -d --wait postgres
if ($LASTEXITCODE -ne 0) { throw 'postgres did not start' }
$tables = (& docker @compose exec -T postgres psql -U postgres -d automataflow -tAc "select count(*) from pg_tables where schemaname = 'public'").Trim()
if ($tables -ne '0') { throw "the target database already has $tables tables; restore only into an empty deployment" }
$container = (& docker @compose ps -q postgres).Trim()
docker cp (Join-Path $source 'database.dump') "${container}:/tmp/automataflow.dump"
& docker @compose exec -T postgres sh -c 'PGPASSWORD="$AF_DB_OWNER_PASSWORD" pg_restore -h 127.0.0.1 -U automataflow_owner -d automataflow --no-owner --no-privileges --exit-on-error /tmp/automataflow.dump; status=$?; rm -f /tmp/automataflow.dump; exit $status'
if ($LASTEXITCODE -ne 0) { throw 'pg_restore failed' }
Write-Host 'Database restored.'

# 3. Volumes (empty only). Ownership and modes come from the archive (uid 10001, 0700).
foreach ($volume in 'keys', 'artifacts') {
    docker volume create "${Project}_${volume}" | Out-Null
    # Plain commands only (no shell script): Windows PowerShell 5.1 mangles quoting in native arguments and stdin.
    $existing = docker run --rm -v "${Project}_${volume}:/data" --entrypoint find $helper /data -mindepth 1 -maxdepth 1
    if ($existing) { throw "the $volume volume is not empty; restore only into an empty deployment" }
    docker run --rm -v "${Project}_${volume}:/data" -v "${source}:/backup:ro" --entrypoint tar $helper --numeric-owner -xzpf "/backup/$volume.tar.gz" -C /data
    if ($LASTEXITCODE -ne 0) { throw "restoring the $volume volume failed" }
}
Write-Host 'Key ring and artifacts restored.'

# 4. Migrations newer than the backup, then the platform.
& docker @compose up -d --wait
if ($LASTEXITCODE -ne 0) { throw 'the deployment did not become healthy after the restore' }
Write-Host 'Restore complete; run scripts/verify-deployment.ps1.'
