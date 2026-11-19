<#
.SYNOPSIS
  Tests scripts/validate-deployment.ps1 (Phase 7): the shipped production compose with generated secrets must pass,
  and every known-dangerous drift, injected through a compose override, must be refused with its specific message.
  Run by scripts/verify-in-container.ps1; needs Docker (compose) only. Generated secrets live in a temp directory and
  are deleted afterwards.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$tmp = Join-Path ([IO.Path]::GetTempPath()) ("af-validate-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
New-Item -ItemType Directory $tmp | Out-Null
$failures = 0

function Invoke-Validation([string[]]$extraFiles, [hashtable]$extraArgs = @{}) {
    $files = @('deploy/docker-compose.prod.yml') + $extraFiles
    $output = & (Join-Path $root 'scripts/validate-deployment.ps1') -EnvFile (Join-Path $tmp '.env.prod') -ComposeFile $files @extraArgs *>&1 | Out-String
    return @{ Code = $LASTEXITCODE; Output = $output }
}

function Assert-Case([string]$name, [string]$override, [string]$expected, [hashtable]$extraArgs = @{}) {
    $file = Join-Path $tmp ("override-" + [guid]::NewGuid().ToString('N').Substring(0, 6) + '.yml')
    [IO.File]::WriteAllText($file, $override)
    $result = Invoke-Validation @($file) $extraArgs
    if ($result.Code -eq 1 -and $result.Output -match [regex]::Escape($expected)) {
        Write-Host "  ok      $name"
    }
    else {
        Write-Host "  FAILED  $name (exit $($result.Code)); expected '$expected' in:`n$($result.Output)"
        $script:failures++
    }
}

try {
    & (Join-Path $root 'scripts/new-deployment-secrets.ps1') -OutputEnv (Join-Path $tmp '.env.prod') -CertificateFile (Join-Path $tmp 'keyring.pfx') | Out-Null
    Add-Content (Join-Path $tmp '.env.prod') "AF_KEYRING_CERTIFICATE_FILE=$(Join-Path $tmp 'keyring.pfx')"

    Write-Host 'Deployment validation tests:'
    $baseline = Invoke-Validation @()
    if ($baseline.Code -eq 0) { Write-Host '  ok      the shipped production compose with generated secrets is valid' }
    else { Write-Host "  FAILED  baseline refused:`n$($baseline.Output)"; $failures++ }

    $example = & (Join-Path $root 'scripts/validate-deployment.ps1') -EnvFile (Join-Path $root 'deploy/.env.prod.example') *>&1 | Out-String
    if ($LASTEXITCODE -eq 1 -and $example -match 'placeholder') { Write-Host '  ok      the committed template (placeholders) is refused' }
    else { Write-Host "  FAILED  the template was not refused:`n$example"; $failures++ }

    Assert-Case 'worker under tini (init: true)' "services:`n  worker:`n    init: true`n" "breaks graceful-stop fencing"
    Assert-Case 'worker with CAP_SYS_ADMIN' "services:`n  worker:`n    cap_add: [SYS_ADMIN]`n" 'cap_add must be exactly'
    Assert-Case 'privileged worker' "services:`n  worker:`n    privileged: true`n" 'worker: privileged is forbidden'
    Assert-Case 'seccomp unconfined' "services:`n  worker:`n    security_opt: !override [seccomp=unconfined]`n" 'seccomp to deploy/verify/chromium-seccomp.json'
    Assert-Case 'host PID namespace' "services:`n  worker:`n    pid: host`n" "'pid: host' is forbidden"
    Assert-Case 'short stop grace period' "services:`n  worker:`n    stop_grace_period: 10s`n" 'stop_grace_period must be at least 225s'
    Assert-Case 'development browser mode' "services:`n  worker:`n    environment:`n      Automation__Browser__Mode: Development`n" 'Automation__Browser__Mode=Development'
    Assert-Case 'private-network exception' "services:`n  worker:`n    environment:`n      Automation__Browser__DevelopmentPrivateNetworkOrigins__0: http://10.0.0.5`n" 'development-only exception'
    Assert-Case 'self-service registration' "services:`n  api:`n    environment:`n      Auth__AllowRegistration: 'true'`n" 'self-service registration is on'
    Assert-Case 'weak signing key' "services:`n  api:`n    environment:`n      Auth__Jwt__SigningKey: short-key-123`n" 'at least 32 bytes'
    Assert-Case 'Api as the owner role' "services:`n  api:`n    environment:`n      ConnectionStrings__Default: Host=postgres;Database=automataflow;Username=automataflow_owner;Password=Zx81kQ0pL2mN4bV6cX8z`n" 'application role automataflow_app'
    Assert-Case 'published database' "services:`n  postgres:`n    ports: ['5432:5432']`n" 'postgres: must not publish ports'
    Assert-Case 'key ring mounted by another service' "services:`n  web:`n    volumes: ['keys:/var/lib/automataflow/keys']`n" 'web: must not mount /var/lib/automataflow/keys'
    Assert-Case 'Development Api environment' "services:`n  api:`n    environment:`n      ASPNETCORE_ENVIRONMENT: Development`n" 'must be Production'
    Assert-Case 'missing trusted proxy networks' "services:`n  api:`n    environment:`n      Proxy__TrustedNetworks: ''`n" 'Proxy__TrustedNetworks'

    $registration = Join-Path $tmp 'registration.yml'
    [IO.File]::WriteAllText($registration, "services:`n  api:`n    environment:`n      Auth__AllowRegistration: 'true'`n")
    $allowed = Invoke-Validation @($registration) @{ AllowRegistration = $true }
    if ($allowed.Code -eq 0) { Write-Host '  ok      registration is accepted when explicitly allowed' }
    else { Write-Host "  FAILED  explicit -AllowRegistration refused:`n$($allowed.Output)"; $failures++ }
}
finally {
    Remove-Item -Recurse -Force $tmp -ErrorAction SilentlyContinue
}

if ($failures -gt 0) {
    Write-Host "$failures deployment validation test(s) failed."
    exit 1
}

Write-Host 'All deployment validation tests passed.'
exit 0
