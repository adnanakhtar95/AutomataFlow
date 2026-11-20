<#
.SYNOPSIS
  Phase 10 measurements and reliability scenarios on the release-built stack (Api, Worker, gateway, PostgreSQL) against
  the DemoCrm test fixture. Writes a JSON report; never extrapolates.

.DESCRIPTION
  Uses scripts/e2e-dashboard.ps1 -StackOnly to build and start the stack (fresh secrets, fresh volumes), then:
  1. Throughput: one job of -Records new customers with the shipped CRM package, one Worker container
     (Worker:MaxConcurrentAttempts default 2), job MaxConcurrency 4; docker stats sampled every 5 s.
  2. Database interruption: a second job; PostgreSQL is stopped for 20 s while records are running, then started.
  3. Api restart: a third job; the Api container is restarted while records are running.
  For every job: final status, record outcomes, and the target's own counts (customers created, registration POSTs)
  to detect any duplicate side effect. The stack and its volumes are removed afterwards unless -KeepStack.
#>
[CmdletBinding()]
param([int]$Records = 40, [switch]$SkipBuild, [switch]$KeepStack, [string]$Output = 'docs/measurements/release-measurements.json')

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$compose = Join-Path $root 'deploy/e2e/docker-compose.e2e.yml'
$base = 'http://127.0.0.1:8088'
$crm = 'http://127.0.0.1:8089'

& (Join-Path $root 'scripts/e2e-dashboard.ps1') -StackOnly -SkipBuild:$SkipBuild
if ($LASTEXITCODE -ne 0 -and $null -ne $LASTEXITCODE) { throw 'stack start failed' }

function Api($method, $path, $body = $null, $token = $null) {
    $headers = @{}
    if ($token) { $headers.Authorization = "Bearer $token" }
    $request = @{ Uri = "$base$path"; Method = $method; Headers = $headers; ContentType = 'application/json'; UseBasicParsing = $true }
    if ($null -ne $body) { $request.Body = ($body | ConvertTo-Json -Depth 30 -Compress) }
    for ($i = 0; $i -lt 30; $i++) {
        try { return Invoke-RestMethod @request }
        catch { if ($_.Exception.Response -and [int]$_.Exception.Response.StatusCode -lt 500) { throw } ; Start-Sleep 2 } # retry while the Api restarts
    }
    throw "Api $method $path kept failing"
}
function CrmState { Invoke-RestMethod -Uri "$crm/demo/state" -Headers @{ 'X-Demo-Control-Token' = $env:AF_E2E_DEMOCRM_TOKEN } -UseBasicParsing }
function Upload($token, $version, [string]$csv, [string]$name) {
    $boundary = [guid]::NewGuid().ToString('N')
    $form = @(
        "--$boundary", 'Content-Disposition: form-data; name="workflowVersionId"', '', $version,
        "--$boundary", 'Content-Disposition: form-data; name="parameters"', '', '{"crmBaseUrl":"http://democrm:8080","crmUser":"agent@democrm.test"}',
        "--$boundary", 'Content-Disposition: form-data; name="options"', '', '{"maxConcurrency":4,"maxAttempts":3,"retryBackoffSeconds":2}',
        "--$boundary", "Content-Disposition: form-data; name=`"file`"; filename=`"$name.csv`"", 'Content-Type: text/csv', '', $csv,
        "--$boundary--", '') -join "`r`n"
    (Invoke-RestMethod -Uri "$base/api/v1/jobs" -Method POST -Headers @{ Authorization = "Bearer $token" } -ContentType "multipart/form-data; boundary=$boundary" -Body $form -UseBasicParsing).jobId
}
function Csv([string]$prefix, [int]$count, [int]$phoneBase) {
    $rows = @('firstName,lastName,email,phone,company,country,customerType')
    for ($i = 1; $i -le $count; $i++) {
        $suffix = -join ([char[]]"$i" | ForEach-Object { [char]([int][char]'a' + [int]"$_") }) # names: letters only (DemoCrm validation)
        $rows += "Perf,Customer$suffix,$prefix-$i@example.test,+1 555 $($phoneBase + $i),Measure Co,Canada,Standard"
    }
    ($rows -join "`n") + "`n"
}
function Stats {
    $ErrorActionPreference = 'Continue'
    docker stats --no-stream --format '{{.Name}}|{{.CPUPerc}}|{{.MemUsage}}' af-e2e-worker-1 af-e2e-api-1 af-e2e-postgres-1 2>$null | ForEach-Object {
        $name, $cpu, $mem = $_ -split '\|'
        [pscustomobject]@{ container = $name; cpu = [double]($cpu.TrimEnd('%')); memory = ($mem -split ' / ')[0] }
    }
}
function WaitJob($token, $jobId, [int]$timeoutSeconds, [scriptblock]$during = $null, [System.Collections.Generic.List[object]]$samples = $null) {
    $started = Get-Date; $done = $false; $duringRun = $false
    while (-not $done) {
        $job = Api GET "/api/v1/jobs/$jobId" $null $token
        $done = $job.status -notin 'Pending', 'Running'
        # Fire the disruption mid-job: some records done, others still to run.
        if (-not $duringRun -and $during -and -not $done -and [int]$job.recordsByStatus.Succeeded -ge 3) { & $during; $duringRun = $true }
        if ($null -ne $samples) { foreach ($s in Stats) { $samples.Add($s) } } # an empty List is falsy in PowerShell
        if (((Get-Date) - $started).TotalSeconds -gt $timeoutSeconds) { throw "job $jobId did not finish in $timeoutSeconds s ($($job.status))" }
        if (-not $done) { Start-Sleep -Milliseconds $(if ($during -and -not $duringRun) { 500 } else { 5000 }) }
    }
    [pscustomobject]@{ job = $job; seconds = [math]::Round(((Get-Date) - $started).TotalSeconds, 1); disruptionApplied = $duringRun }
}

$report = [ordered]@{}
try {
    $run = (Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss')
    $owner = Api POST '/api/v1/auth/register' @{ email = "perf-$run@example.test"; password = 'Measure-Password-2026'; organizationName = 'Measurements'; organizationSlug = "measure-$run" }
    $token = $owner.accessToken
    $definition = Get-Content (Join-Path $root 'packages/crm-customer-registration/workflow.json') -Raw | ConvertFrom-Json
    $created = Api POST '/api/v1/workflows' @{ name = 'crm-perf'; definition = $definition } $token
    Api POST "/api/v1/workflows/$($created.workflowId)/versions/$($created.version.id)/publish" @{ acknowledgeAttestations = $true } $token | Out-Null
    Invoke-RestMethod -Uri "$base/api/v1/secrets/crmPassword" -Method PUT -Headers @{ Authorization = "Bearer $token" } -ContentType 'application/json' -Body '{"value":"DemoPass-2026!"}' -UseBasicParsing | Out-Null
    $version = $created.version.id

    $report.environment = [ordered]@{
        date = (Get-Date).ToUniversalTime().ToString('o'); host = 'Docker Desktop (WSL2) on Windows 11; other unrelated container stacks were running on the same host'
        cpus = (docker info --format '{{.NCPU}}'); memoryBytes = (docker info --format '{{.MemTotal}}')
        workerContainers = 1; workerMaxConcurrentAttempts = 2; jobMaxConcurrency = 4; target = 'DemoCrm test fixture (in-memory, same Docker network)'
    }

    # 1. Throughput
    $before = CrmState
    $samples = New-Object System.Collections.Generic.List[object]
    $artifactsBefore = [long]((docker run --rm -v af-e2e_artifacts:/a --entrypoint du postgres:18 -sb /a 2>$null) -split '\s')[0]
    $jobId = Upload $token $version (Csv "tp-$run" $Records 3000) 'throughput'
    $result = WaitJob $token $jobId 1800 $null $samples
    $after = CrmState
    $artifactsAfter = [long]((docker run --rm -v af-e2e_artifacts:/a --entrypoint du postgres:18 -sb /a 2>$null) -split '\s')[0]
    $report.throughput = [ordered]@{
        records = $Records; status = $result.job.status; recordsByStatus = $result.job.recordsByStatus; seconds = $result.seconds
        recordsPerMinute = [math]::Round($Records / ($result.seconds / 60), 1)
        customersCreated = $after.customerCount - $before.customerCount; registrationPosts = $after.registrationSubmissions - $before.registrationSubmissions
        artifactBytesAdded = $artifactsAfter - $artifactsBefore; artifactBytesPerRecord = [math]::Round(($artifactsAfter - $artifactsBefore) / $Records)
        peakCpuPercent = ($samples | Group-Object container | ForEach-Object { [ordered]@{ container = $_.Name; peakCpu = ($_.Group | Measure-Object cpu -Maximum).Maximum; lastMemory = ($_.Group | Select-Object -Last 1).memory } })
    }

    # 2. Database interruption while records run
    $before = CrmState
    $jobId = Upload $token $version (Csv "db-$run" 40 5000) 'db-outage'
    $result = WaitJob $token $jobId 1800 { $ErrorActionPreference = 'Continue'; docker compose -f $compose stop postgres | Out-Null; Start-Sleep 20; docker compose -f $compose start postgres | Out-Null }
    $after = CrmState
    $report.databaseInterruption = [ordered]@{ records = 40; outageSeconds = 20; applied = $result.disruptionApplied; status = $result.job.status; recordsByStatus = $result.job.recordsByStatus
        seconds = $result.seconds; customersCreated = $after.customerCount - $before.customerCount; registrationPosts = $after.registrationSubmissions - $before.registrationSubmissions }

    # 3. Api restart while records run (the Worker is independent of the Api)
    $before = CrmState
    $jobId = Upload $token $version (Csv "api-$run" 40 6000) 'api-restart'
    $result = WaitJob $token $jobId 1800 { $ErrorActionPreference = 'Continue'; docker compose -f $compose restart api | Out-Null }
    $after = CrmState
    $report.apiRestart = [ordered]@{ records = 40; applied = $result.disruptionApplied; status = $result.job.status; recordsByStatus = $result.job.recordsByStatus; seconds = $result.seconds
        customersCreated = $after.customerCount - $before.customerCount; registrationPosts = $after.registrationSubmissions - $before.registrationSubmissions }

    # Api latency (through the gateway, warm): 50 sequential job-list requests.
    $times = 1..50 | ForEach-Object { (Measure-Command { Api GET '/api/v1/jobs?take=50' $null $token | Out-Null }).TotalMilliseconds } | Sort-Object
    $report.apiLatencyMs = [ordered]@{ endpoint = 'GET /api/v1/jobs?take=50 via gateway'; samples = 50; p50 = [math]::Round($times[24], 1); p95 = [math]::Round($times[47], 1); max = [math]::Round($times[49], 1) }
    $report.postgres = [ordered]@{ databaseSize = ((docker exec af-e2e-postgres-1 psql -U automataflow -d automataflow -tAc "select pg_size_pretty(pg_database_size('automataflow'))") -join '').Trim() }
}
finally {
    $path = Join-Path $root $Output
    New-Item -ItemType Directory -Force (Split-Path -Parent $path) | Out-Null
    [IO.File]::WriteAllText($path, ($report | ConvertTo-Json -Depth 10) + "`n", (New-Object Text.UTF8Encoding($false)))
    Write-Host "Report: $path"
    if (-not $KeepStack) { $ErrorActionPreference = 'Continue'; docker compose -f $compose down -v --remove-orphans 2>&1 | Out-Null }
}
