<#
.SYNOPSIS
  Runs the fictional DemoCrm portal in the official .NET 10 SDK Linux container, reachable at http://localhost:5080.

.DESCRIPTION
  Windows Smart App Control can block locally built binaries, so DemoCrm is run in a container (source is copied in,
  your working tree is not modified). The port is published on the loopback interface only.
  Sign in with the fictional demo credentials in src/DemoCrm/appsettings.json (agent@democrm.test).
  DemoCrm is a demonstration target: never expose it beyond localhost or a private test network.

.EXAMPLE
  .\scripts\run-demo-crm.ps1            # foreground, Ctrl+C to stop
  .\scripts\run-demo-crm.ps1 -Detach    # background; stop with: docker stop democrm-demo
#>
[CmdletBinding()]
param(
    [string]$Image = 'mcr.microsoft.com/dotnet/sdk:10.0',
    [string]$Name = 'democrm-demo',
    [switch]$Detach
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

# The reset/state control API stays disabled unless DemoCrm__ControlApi__Token is set. Use the value from the
# environment if present; otherwise generate a random one for this run (nothing is stored or committed).
if ([string]::IsNullOrEmpty($env:DemoCrm__ControlApi__Token)) {
    $bytes = New-Object byte[] 24
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    $rng.GetBytes($bytes)
    $rng.Dispose()
    $env:DemoCrm__ControlApi__Token = [Convert]::ToBase64String($bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=')
    Write-Host "Control API token for this run (header X-Demo-Control-Token): $($env:DemoCrm__ControlApi__Token)"
}

$script = @'
set -e
mkdir /work && cd /src
tar cf - --exclude=./.git --exclude=./.vs --exclude=bin --exclude=obj . | tar xf - -C /work
cd /work
exec dotnet run --project src/DemoCrm --no-launch-profile
'@ -replace "`r", ''

$mode = if ($Detach) { '-d' } else { '-i' }

docker run --rm $mode --name $Name `
    -p 127.0.0.1:5080:5080 `
    -v "${root}:/src:ro" `
    -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 `
    -e ASPNETCORE_ENVIRONMENT=Development `
    -e ASPNETCORE_URLS=http://0.0.0.0:5080 `
    -e DemoCrm__ControlApi__Token `
    $Image bash -c $script

exit $LASTEXITCODE
