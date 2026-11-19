<#
.SYNOPSIS
  Regenerates the dashboard's OpenAPI snapshot from the real Api (ADR-076), then the TypeScript types.

.DESCRIPTION
  1. Builds the solution in the pinned verification image and runs OpenApiContractTests in update mode; the document is
     written to web/automataflow-web/openapi/automataflow-api.json (the working tree is otherwise untouched).
  2. Regenerates web/automataflow-web/src/api/schema.d.ts with openapi-typescript in the pinned Node image.
  Review the diff of both files before committing.
#>
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$packages = [xml](Get-Content (Join-Path $root 'Directory.Packages.props') -Raw)
$playwright = ($packages.Project.ItemGroup.PackageVersion | Where-Object { $_.Include -eq 'Microsoft.Playwright' }).Version
$image = "automataflow-verify:pw-$playwright"
$node = Get-Content (Join-Path $root 'web/automataflow-web/.node-image') -Raw
$out = Join-Path $root 'web/automataflow-web/openapi'

$script = @'
set -e
mkdir -p ~/work && cd /src && tar cf - --exclude=./.git --exclude=./.vs --exclude=bin --exclude=obj --exclude=node_modules . | tar xf - -C ~/work
cd ~/work
dotnet build tests/AutomataFlow.IntegrationTests -v q -nologo
AUTOMATAFLOW_OPENAPI_OUTPUT=/out dotnet test tests/AutomataFlow.IntegrationTests --no-build --filter FullyQualifiedName~The_committed_OpenAPI_snapshot
'@ -replace "`r", ''

$ErrorActionPreference = 'Continue'
docker run --rm -v "${root}:/src:ro" -v "${out}:/out" $image bash -c $script
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$web = Join-Path $root 'web/automataflow-web'
docker run --rm -v "${web}:/app" -v /app/node_modules -w /app $node.Trim() sh -c 'npm ci --no-audit --no-fund && npm run generate:api'
exit $LASTEXITCODE
