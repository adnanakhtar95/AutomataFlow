# AutomataFlow

Browser automation for repetitive business work in web applications without usable APIs. Define a workflow once
(validated, versioned JSON; no code), upload a CSV/JSON file of records, and AutomataFlow processes them with a real,
sandboxed browser: duplicate-safe, with screenshots, logs and a results export. Multi-workspace, self-hostable with
Docker Compose, no paid third-party services.

**Status:** Phases 1–10 delivered (docs/FINAL_PROJECT_COMPLETION_REPORT.md). Supported deployments: self-hosted or
managed single-tenant. Shared multi-tenant SaaS for mutually untrusted customers needs the hosted-isolation work listed
in docs/SECURITY.md §8.

## Documentation
| For | Read |
|---|---|
| What the product does, editions, onboarding process | docs/PRODUCT.md |
| Daily use (workspaces, workflows, jobs, outcomes) | docs/USER_GUIDE.md |
| Writing workflows | docs/WORKFLOWS.md, packages/README.md |
| Installing, upgrading, backup and restore | docs/DEPLOYMENT.md |
| Operating (monitoring, alerts, fences, retention) | docs/RUNBOOK.md |
| Problems | docs/TROUBLESHOOTING.md |
| Security controls and known limitations | docs/SECURITY.md |
| Measured performance and reliability | docs/PERFORMANCE.md |
| Architecture and decisions | docs/ARCHITECTURE.md, docs/DECISIONS.md, docs/ROADMAP.md |

## Quick start (evaluation on one machine)
```powershell
./scripts/new-deployment-secrets.ps1                          # deploy/.env.prod + key ring certificate
# set AF_HTTP_PORT if 8080 is taken, then:
./scripts/validate-deployment.ps1 -EnvFile deploy/.env.prod
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml up -d --build --wait
# first platform operator (credentials from the environment), then onboard a workspace in the dashboard:
$env:AF_BOOTSTRAP_EMAIL='ops@example.com'; $env:AF_BOOTSTRAP_PASSWORD='<long password>'
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml run --rm --no-deps -e AF_BOOTSTRAP_EMAIL -e AF_BOOTSTRAP_PASSWORD api bootstrap-operator
```
Then open http://127.0.0.1:8080 (put TLS in front for anything beyond localhost).

## Development
.NET 10 (`global.json`), PostgreSQL 18 in Docker, React dashboard in `web/automataflow-web`. Project rules: CLAUDE.md.
Authoritative verification runs in containers: `./scripts/verify-in-container.ps1` (all .NET suites incl. real-browser
and process-supervision tests, dashboard checks, deployment-validation cases); browser journeys:
`./scripts/e2e-dashboard.ps1`; measurements: `./scripts/measure-release.ps1`.
