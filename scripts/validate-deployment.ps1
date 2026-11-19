<#
.SYNOPSIS
  Refuses an unsafe or incomplete AutomataFlow production configuration before it is deployed (Phase 7, ADR-078).

.DESCRIPTION
  Resolves the compose model exactly as Docker will run it (`docker compose config --format json`, so overrides and
  environment interpolation are included) and checks it:
  - Worker posture (ADR-075): no init, user root, cap_drop ALL, cap_add exactly NET_ADMIN SETUID SETGID SETPCAP
    SYS_CHROOT, the Chromium seccomp profile, never privileged, no host PID/network/IPC, no development browser mode
    or private-network exceptions;
  - Api: Production environment, no Development-only settings, trusted proxy networks set, registration off unless
    explicitly allowed with -AllowRegistration;
  - secrets: no placeholder values, signing key of at least 32 bytes, key ring certificate configured;
  - data: PostgreSQL not published; the key ring and artifacts volumes mounted only by the Api and the Worker;
    only the gateway (and the optional TLS proxy) publish ports.
  Exit code 0 = valid; 1 = problems (listed); 2 = the configuration could not be resolved.

.EXAMPLE
  .\scripts\validate-deployment.ps1 -EnvFile deploy/.env.prod
#>
[CmdletBinding()]
param(
    [string]$EnvFile = 'deploy/.env.prod',
    [string[]]$ComposeFile = @('deploy/docker-compose.prod.yml'),
    [string[]]$Profiles = @(),
    [switch]$AllowRegistration
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    $arguments = @('compose', '--env-file', $EnvFile)
    foreach ($f in $ComposeFile) { $arguments += @('-f', $f) }
    foreach ($p in $Profiles) { $arguments += @('--profile', $p) }
    $arguments += @('config', '--format', 'json')
    $ErrorActionPreference = 'Continue'
    $json = & docker @arguments 2>&1
    $ErrorActionPreference = 'Stop'
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Configuration could not be resolved:`n$($json -join "`n")"
        exit 2
    }

    $model = ($json | Where-Object { $_ -is [string] }) -join "`n" | ConvertFrom-Json
}
finally {
    Pop-Location
}

$problems = New-Object System.Collections.Generic.List[string]
function Fail([string]$message) { $problems.Add($message) }
function Env($service, [string]$name) {
    $environment = $service.environment
    if ($null -eq $environment) { return $null }
    $property = $environment.PSObject.Properties[$name]
    if ($property) { return [string]$property.Value } else { return $null }
}
function Seconds([string]$duration) {
    # Go duration as printed by docker compose, e.g. 4m0s, 1h2m3.5s, 225s.
    $total = 0.0
    foreach ($m in [regex]::Matches($duration, '([0-9.]+)(h|ms|m|s)')) {
        $value = [double]$m.Groups[1].Value
        $total += switch ($m.Groups[2].Value) { 'h' { $value * 3600 } 'm' { $value * 60 } 's' { $value } 'ms' { $value / 1000 } }
    }
    return $total
}
function Placeholder([string]$value) { return [string]::IsNullOrWhiteSpace($value) -or $value -match '(?i)change-me|REQUIRED|placeholder|example' }

$services = $model.services
foreach ($required in 'postgres', 'migrate', 'api', 'worker', 'web') {
    if (-not $services.PSObject.Properties[$required]) { Fail "service '$required' is missing" }
}

# ---- Worker posture (ADR-075) ----
$worker = $services.worker
if ($worker) {
    if ($worker.init -eq $true) { Fail "worker: 'init: true' breaks graceful-stop fencing (the supervisor must be PID 1)" }
    if ($worker.user -ne 'root') { Fail "worker: user must be root (the launcher drops privileges itself)" }
    if ($worker.privileged -eq $true) { Fail 'worker: privileged is forbidden' }
    $drop = @($worker.cap_drop | ForEach-Object { ([string]$_).ToUpperInvariant() -replace '^CAP_', '' })
    $add = @($worker.cap_add | ForEach-Object { ([string]$_).ToUpperInvariant() -replace '^CAP_', '' } | Sort-Object)
    if ($drop -notcontains 'ALL') { Fail 'worker: cap_drop must contain ALL' }
    $expected = @('NET_ADMIN', 'SETGID', 'SETPCAP', 'SETUID', 'SYS_CHROOT')
    if (($add -join ',') -ne ($expected -join ',')) { Fail "worker: cap_add must be exactly $($expected -join ', ') (found: $($add -join ', '))" }
    $seccomp = @($worker.security_opt | Where-Object { $_ -match '^seccomp[=:]' })
    if ($seccomp.Count -ne 1 -or $seccomp[0] -notmatch 'chromium-seccomp\.json$') { Fail 'worker: security_opt must set seccomp to deploy/verify/chromium-seccomp.json' }
    if (@($worker.security_opt) -match 'unconfined') { Fail 'worker: an unconfined security option is forbidden' }
    foreach ($mode in 'pid', 'ipc', 'network_mode', 'userns_mode', 'cgroup') {
        if ([string]$worker.$mode -match 'host') { Fail "worker: '${mode}: host' is forbidden" }
    }
    if ((Env $worker 'Automation__Browser__Mode') -eq 'Development') { Fail 'worker: Automation__Browser__Mode=Development is not allowed in production' }
    if ($worker.environment -and ($worker.environment.PSObject.Properties.Name -match 'DevelopmentPrivateNetworkOrigins')) { Fail 'worker: DevelopmentPrivateNetworkOrigins is a development-only exception' }
    if (-not (Env $worker 'DataProtection__CertificatePath')) { Fail 'worker: DataProtection__CertificatePath is required (key ring encryption at rest)' }
    if ((Seconds ([string]$worker.stop_grace_period)) -lt 225) { Fail 'worker: stop_grace_period must be at least 225s (ADR-075)' }
}

# ---- Api ----
$api = $services.api
if ($api) {
    $environmentName = Env $api 'ASPNETCORE_ENVIRONMENT'
    if ($environmentName -and $environmentName -ne 'Production') { Fail "api: ASPNETCORE_ENVIRONMENT must be Production (found '$environmentName')" }
    if ((Env $api 'Platform__Mode') -eq 'Development') { Fail 'api: Platform__Mode=Development is not allowed in production' }
    if ((Env $api 'Auth__AllowRegistration') -eq 'true' -and -not $AllowRegistration) { Fail 'api: self-service registration is on; pass -AllowRegistration if that is intended' }
    if (-not (Env $api 'Proxy__TrustedNetworks')) { Fail 'api: Proxy__TrustedNetworks must name the gateway network (per-client rate limiting)' }
    $key = Env $api 'Auth__Jwt__SigningKey'
    if (Placeholder $key) { Fail 'api: Auth__Jwt__SigningKey is missing or a placeholder' }
    elseif ([Text.Encoding]::UTF8.GetByteCount($key) -lt 32) { Fail 'api: Auth__Jwt__SigningKey must be at least 32 bytes' }
    if (-not (Env $api 'DataProtection__CertificatePath')) { Fail 'api: DataProtection__CertificatePath is required (key ring encryption at rest)' }
    if ($api.privileged -eq $true) { Fail 'api: privileged is forbidden' }
}

# ---- Secrets ----
foreach ($pair in @(
        @('postgres', 'POSTGRES_PASSWORD'), @('postgres', 'AF_DB_OWNER_PASSWORD'), @('postgres', 'AF_DB_APP_PASSWORD'),
        @('api', 'DataProtection__CertificatePassword'), @('worker', 'DataProtection__CertificatePassword'))) {
    $service = $services.($pair[0])
    if ($service -and (Placeholder (Env $service $pair[1]))) { Fail "$($pair[0]): $($pair[1]) is missing or a placeholder" }
}
foreach ($name in 'api', 'worker') {
    $service = $services.$name
    $connection = if ($service) { Env $service 'ConnectionStrings__Default' } else { $null }
    if ($connection -and $connection -notmatch 'Username=automataflow_app') { Fail "${name}: must connect as the application role automataflow_app (ADR-078)" }
    if ($connection -and (Placeholder ($connection -replace '^.*Password=', ''))) { Fail "${name}: database password is a placeholder" }
}
$migrateConnection = if ($services.migrate) { Env $services.migrate 'ConnectionStrings__Default' } else { $null }
if ($migrateConnection -and $migrateConnection -notmatch 'Username=automataflow_owner') { Fail 'migrate: must connect as the owner role automataflow_owner' }

# ---- Data exposure ----
if ($services.postgres.ports) { Fail 'postgres: must not publish ports' }
foreach ($property in $services.PSObject.Properties) {
    $name = $property.Name
    $service = $property.Value
    if ($service.ports -and $name -notin @('web', 'tls')) { Fail "${name}: must not publish ports (only the gateway and the TLS proxy do)" }
    foreach ($volume in @($service.volumes)) {
        if ($volume.target -in @('/var/lib/automataflow/keys', '/var/lib/automataflow/artifacts') -and $name -notin @('api', 'worker')) {
            Fail "${name}: must not mount $($volume.target) (key ring and artifacts belong to the Api and the Worker only)"
        }
    }
}

if ($problems.Count -gt 0) {
    Write-Host "Deployment configuration REFUSED ($($problems.Count) problem(s)):"
    $problems | ForEach-Object { Write-Host "  - $_" }
    exit 1
}

Write-Host 'Deployment configuration valid.'
exit 0
