<#
.SYNOPSIS
  Backs up a production deployment (docs/DEPLOYMENT.md, "Backup and restore"): the database, the Data Protection key
  ring and the artifacts, into one timestamped directory.

.DESCRIPTION
  - database: pg_dump custom format, taken inside the postgres container (consistent snapshot while the platform runs);
  - key ring and artifacts: tar of the named volumes, read-only, ownership and modes preserved.
  The key ring is encrypted with the certificate (ADR-079), which is NOT part of this backup: store deploy/.env.prod and
  the certificate separately (a backup of encrypted data plus its key in one place is one theft away from disclosure).
  Writes SHA-256 checksums (SHA256SUMS) for verification by restore.ps1.
#>
[CmdletBinding()]
param(
    [string]$EnvFile = 'deploy/.env.prod',
    [string]$Destination = 'backups',
    [string]$Project = 'automataflow-prod'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$stamp = (Get-Date).ToUniversalTime().ToString('yyyyMMddTHHmmssZ')
$target = Join-Path (Resolve-Path -LiteralPath (New-Item -ItemType Directory -Force (Join-Path $root $Destination))).Path $stamp
New-Item -ItemType Directory $target | Out-Null
$envPath = if ([IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $root $EnvFile }
$compose = @('compose', '--env-file', $envPath, '-p', $Project, '-f', (Join-Path $root 'deploy/docker-compose.prod.yml'))
$helper = 'postgres:18.6@sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42'

$ErrorActionPreference = 'Continue'
Write-Host "Backing up to $target"
# PowerShell pipes mangle binary output: dump to a file inside the container, then copy it out.
& docker @compose exec -T postgres sh -c 'pg_dump -U postgres -d automataflow -Fc --no-owner --no-privileges -f /tmp/automataflow.dump'
if ($LASTEXITCODE -ne 0) { throw 'pg_dump failed' }
$container = (& docker @compose ps -q postgres).Trim()
docker cp "${container}:/tmp/automataflow.dump" (Join-Path $target 'database.dump')
& docker @compose exec -T postgres rm -f /tmp/automataflow.dump

foreach ($volume in 'keys', 'artifacts') {
    docker run --rm -v "${Project}_${volume}:/data:ro" -v "${target}:/backup" --entrypoint tar $helper `
        --numeric-owner -czf "/backup/$volume.tar.gz" -C /data .
    if ($LASTEXITCODE -ne 0) { throw "archiving the $volume volume failed" }
}

$sums = Get-ChildItem $target -File | Sort-Object Name | ForEach-Object { "$((Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant())  $($_.Name)" }
[IO.File]::WriteAllText((Join-Path $target 'SHA256SUMS'), ($sums -join "`n") + "`n")
Write-Host "Backup complete: $target"
Get-ChildItem $target | Format-Table Name, Length
