<#
.SYNOPSIS
  Verifies a running production deployment (docs/DEPLOYMENT.md): container state, posture, gateway, permissions,
  database roles and, optionally, a real smoke job.

.DESCRIPTION
  Infrastructure checks (always):
  - every service running, `migrate` exited 0, health checks healthy;
  - Api: uid 10001, read-only root filesystem, no capabilities; Worker: started, containment handshake ok, no refusal;
  - gateway: /healthz, the dashboard with its Content-Security-Policy, /api reachable (401 without a token),
    Swagger and the Api health endpoints NOT exposed;
  - volumes: key ring and artifacts owned by uid 10001 with mode 0700; key files encrypted at rest (ADR-079);
  - database: the application role is not a superuser and cannot create tables (ADR-078).
  Smoke job (with -Email/-Password of an Operator or higher, and -SiteUrl of a site you operate): publishes
  docs/workflows/deployment-smoke.json (once), runs one record through the real Worker and checks the result and the
  screenshot artifact. Requires outbound HTTPS from the Worker.
#>
[CmdletBinding()]
param(
    [string]$EnvFile = 'deploy/.env.prod',
    [string]$Project = 'automataflow-prod',
    [string]$BaseUrl = 'http://127.0.0.1:8080',
    [string]$Email,
    [string]$Password,
    # A site you operate (e.g. your target system's sign-in page). Required for the smoke job; no public default.
    [string]$SiteUrl,
    [int]$JobTimeoutSeconds = 300
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$envPath = if ([IO.Path]::IsPathRooted($EnvFile)) { $EnvFile } else { Join-Path $root $EnvFile }
$compose = @('compose', '--env-file', $envPath, '-p', $Project, '-f', (Join-Path $root 'deploy/docker-compose.prod.yml'))
$helper = 'postgres:18.6@sha256:e3d24f6906d0760bf4da0dae872415391ededefad23fd471c47dc53b0ddf3c42'
$failures = New-Object System.Collections.Generic.List[string]
function Check([string]$name, [bool]$ok, [string]$detail = '') {
    if ($ok) { Write-Host "  ok    $name" } else { Write-Host "  FAIL  $name $detail"; $failures.Add($name) }
}
function Container([string]$service) { ((& docker @compose ps -a -q $service) | Select-Object -First 1) }
function Http([string]$path, [hashtable]$headers = @{}, [string]$method = 'GET', $body = $null) {
    $request = @{ Uri = "$BaseUrl$path"; Method = $method; Headers = $headers; UseBasicParsing = $true; ErrorAction = 'Stop' }
    if ($null -ne $body) { $request.Body = ($body | ConvertTo-Json -Depth 20); $request.ContentType = 'application/json' }
    try { return Invoke-WebRequest @request } catch { if ($_.Exception.Response) { return $_.Exception.Response } else { throw } }
}
function StatusOf($response) { [int]$response.StatusCode }

$ErrorActionPreference = 'Continue'
Write-Host 'Containers:'
foreach ($service in 'postgres', 'api', 'worker', 'web') {
    $id = Container $service
    $state = if ($id) { docker inspect $id --format '{{.State.Status}} {{if .State.Health}}{{.State.Health.Status}}{{end}}' } else { 'missing' }
    Check "$service running" ($state -match '^running( healthy)?\s*$' -or ($service -eq 'worker' -and $state -match '^running')) $state
}
$migrate = Container 'migrate'
Check 'migrate exited 0' ($migrate -and (docker inspect $migrate --format '{{.State.Status}} {{.State.ExitCode}}') -eq 'exited 0')

Write-Host 'Posture:'
$api = Container 'api'
Check 'api runs as uid 10001' ((docker inspect $api --format '{{.Config.User}}') -eq '10001:10001')
Check 'api root filesystem read-only' ((docker inspect $api --format '{{.HostConfig.ReadonlyRootfs}}') -eq 'true')
Check 'api holds no capabilities' ((docker inspect $api --format '{{json .HostConfig.CapDrop}}') -match 'ALL')
$workerLog = (docker logs (Container 'worker') 2>&1) -join "`n"
Check 'worker started' ($workerLog -match 'Worker started')
Check 'worker containment handshake ok' ($workerLog -match 'Containment handshake ok')
Check 'worker never refused to start' ($workerLog -notmatch 'startup refused|af-worker-launch: refused')
Check 'worker container has no init (supervisor is PID 1)' ((docker inspect (Container 'worker') --format '{{.HostConfig.Init}}') -ne 'true')

Write-Host 'Gateway:'
$health = Http '/healthz'
Check 'gateway /healthz' ((StatusOf $health) -eq 200)
$index = Http '/'
Check 'dashboard served' ((StatusOf $index) -eq 200 -and $index.Content -match '<div id="root">')
Check 'Content-Security-Policy present' ([string]$index.Headers['Content-Security-Policy'] -match "script-src 'self'")
Check 'Api reachable through the gateway (401 without token)' ((StatusOf (Http '/api/v1/me')) -eq 401)
Check 'Swagger not exposed' ((StatusOf (Http '/swagger/index.html')) -eq 404)
Check 'Api health endpoints not exposed' ((StatusOf (Http '/health/ready')) -eq 404)

Write-Host 'Volumes:'
foreach ($volume in 'keys', 'artifacts') {
    $stat = docker run --rm -v "${Project}_${volume}:/data:ro" --entrypoint stat $helper -c '%u %a' /data
    Check "$volume volume owned by 10001, mode 700" ($stat -eq '10001 700') $stat
}
$keyFiles = docker run --rm -v "${Project}_keys:/data:ro" --entrypoint sh $helper -c 'cat /data/*.xml 2>/dev/null'
if ($keyFiles) {
    $joined = $keyFiles -join "`n"
    Check 'key ring encrypted at rest' ($joined -match 'encryptedSecret' -and $joined -notmatch '<value>')
}
else { Write-Host '  info  no key yet (created on first use)' }

Write-Host 'Database roles:'
$superuser = (& docker @compose exec -T postgres psql -U postgres -d automataflow -tAc "select rolsuper from pg_roles where rolname = 'automataflow_app'").Trim()
Check 'application role is not a superuser' ($superuser -eq 'f') $superuser
# SQL through stdin: Windows PowerShell 5.1 mangles embedded double quotes in native arguments.
$ddl = 'create table af_probe (id int);' | & docker @compose exec -T postgres sh -c 'PGPASSWORD=$AF_DB_APP_PASSWORD psql -h 127.0.0.1 -U automataflow_app -d automataflow -v ON_ERROR_STOP=1' 2>&1
Check 'application role cannot create tables' (($ddl -join ' ') -match 'permission denied')

if ($Email -and $Password -and -not $SiteUrl) { Write-Host '  FAIL  -SiteUrl is required for the smoke job'; $failures.Add('smoke site') }
elseif ($Email -and $Password) {
    Write-Host "Smoke job ($SiteUrl):"
    $login = Http '/api/v1/auth/login' @{} 'POST' @{ email = $Email; password = $Password }
    Check 'sign-in' ((StatusOf $login) -eq 200)
    if ((StatusOf $login) -eq 200) {
        $auth = @{ Authorization = 'Bearer ' + ($login.Content | ConvertFrom-Json).accessToken }
        $definition = Get-Content (Join-Path $root 'docs/workflows/deployment-smoke.json') -Raw | ConvertFrom-Json
        $existing = (Http '/api/v1/workflows?search=deployment-smoke' $auth).Content | ConvertFrom-Json
        $smoke = @($existing) | Where-Object { $_.name -eq 'deployment-smoke' } | Select-Object -First 1
        if (-not $smoke -or -not $smoke.publishedVersionId) {
            if (-not $smoke) {
                $created = (Http '/api/v1/workflows' $auth 'POST' @{ name = 'deployment-smoke'; description = 'Deployment smoke test'; definition = $definition }).Content | ConvertFrom-Json
                $workflowId = $created.workflowId; $versionId = $created.version.id
            }
            else {
                $detail = (Http "/api/v1/workflows/$($smoke.id)" $auth).Content | ConvertFrom-Json
                $workflowId = $smoke.id; $versionId = ($detail.versions | Sort-Object versionNumber | Select-Object -Last 1).id
            }
            $published = Http "/api/v1/workflows/$workflowId/versions/$versionId/publish" $auth 'POST' @{ acknowledgeAttestations = $false }
            Check 'smoke workflow published' ((StatusOf $published) -eq 200)
        }
        else {
            # Re-publish when the shipped definition changed since the published version (compared by canonical hash).
            $expected = ((Http '/api/v1/workflows/validate' $auth 'POST' @{ definition = $definition }).Content | ConvertFrom-Json).definitionHash
            $published = (Http "/api/v1/workflows/$($smoke.id)/versions/$($smoke.publishedVersionId)" $auth).Content | ConvertFrom-Json
            if ($published.version.definitionHash -eq $expected) { $versionId = $smoke.publishedVersionId }
            else {
                $added = (Http "/api/v1/workflows/$($smoke.id)/versions" $auth 'POST' @{ definition = $definition }).Content | ConvertFrom-Json
                $versionId = $added.version.id
                Check 'updated smoke workflow published' ((StatusOf (Http "/api/v1/workflows/$($smoke.id)/versions/$versionId/publish" $auth 'POST' @{ acknowledgeAttestations = $false })) -eq 200)
            }
        }

        $boundary = [guid]::NewGuid().ToString('N')
        $csv = "reference`nsmoke-$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))`n"
        $form = @(
            "--$boundary", 'Content-Disposition: form-data; name="workflowVersionId"', '', $versionId,
            "--$boundary", 'Content-Disposition: form-data; name="parameters"', '', (@{ siteUrl = $SiteUrl } | ConvertTo-Json -Compress),
            "--$boundary", 'Content-Disposition: form-data; name="file"; filename="smoke.csv"', 'Content-Type: text/csv', '', $csv,
            "--$boundary--", '') -join "`r`n"
        $upload = Invoke-WebRequest -Uri "$BaseUrl/api/v1/jobs" -Method POST -Headers $auth -ContentType "multipart/form-data; boundary=$boundary" -Body $form -UseBasicParsing
        $jobId = ($upload.Content | ConvertFrom-Json).jobId
        Check 'smoke job created' ([bool]$jobId)
        $deadline = (Get-Date).AddSeconds($JobTimeoutSeconds)
        do { Start-Sleep 3; $job = (Http "/api/v1/jobs/$jobId" $auth).Content | ConvertFrom-Json } while ($job.status -in 'Pending', 'Running' -and (Get-Date) -lt $deadline)
        Check 'smoke job completed' ($job.status -eq 'Completed') $job.status
        $record = @((Http "/api/v1/jobs/$jobId/records" $auth).Content | ConvertFrom-Json)[0]
        Check 'smoke result captured (HTTP 200)' ([string]$record.result.httpStatus -eq '200') ([string]$record.result.httpStatus)
        $attempts = @((Http "/api/v1/jobs/$jobId/records/$($record.id)/attempts" $auth).Content | ConvertFrom-Json)
        $artifact = @($attempts | ForEach-Object { $_.artifacts }) | Where-Object { $_.kind -eq 'Screenshot' } | Select-Object -First 1
        Check 'screenshot stored' ([bool]$artifact)
        if ($artifact) { Check 'screenshot served to its workspace' ((StatusOf (Http "/api/v1/artifacts/$($artifact.id)" $auth)) -eq 200) }
        Write-Host "  info  job $jobId status $($record.result.httpStatus)"
    }
}

if ($failures.Count -gt 0) { Write-Host "$($failures.Count) check(s) FAILED."; exit 1 }
Write-Host 'Deployment verified.'
exit 0
