# Phase 7 — Production infrastructure and security

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). **Complete (2026-11-20)**; evidence and remaining limitations below.

## Objective
A reproducible, documented production deployment of the release-built Api, Worker and dashboard that a clean machine
can bring up, migrate, verify, back up, upgrade and roll back, with the Phase 5 security limitations resolved or
formally documented. No weakening of the Worker/supervisor security model (ADR-075).

## Scope
1. **Images:** Api (`deploy/api/Dockerfile`, chiseled ASP.NET, uid 10001, `migrate` and `healthcheck` commands), Worker
   (`deploy/worker/Dockerfile`; slimmer runtime base evaluated), dashboard gateway (`deploy/web/Dockerfile`, unprivileged
   nginx, CSP and security headers). All bases pinned by digest. DemoCrm is never a production image.
2. **Production compose** (`deploy/docker-compose.prod.yml`): postgres (pinned), one-shot `migrate`, api, worker (the
   ADR-075 posture, `init: false`), web; health checks, restart policies that preserve supervision, resource limits,
   named volumes, internal network for the database, only the gateway published.
3. **Configuration:** `deploy/.env.prod.example`, every variable documented; production refuses development settings.
4. **Configuration validation:** `scripts/validate-deployment.ps1` (static checks of the compose file and environment:
   no `init` on the Worker, exact capability set, seccomp profile, no `privileged`, no development browser mode, required
   secrets present, key and artifact volumes shared by Api and Worker only), run by the test suite against the shipped
   compose files.
5. **Security hardening (Phase 5 limitations):**
   - separate PostgreSQL roles: a migration owner, an application role for the Api/Worker without DDL, and the
     fence-confirmation path reviewed (ADR to record what is and is not separable without changing ADR-075);
   - Data Protection key ring protected at rest (key encryption with a certificate or key supplied by the operator,
     never committed), or the residual risk documented with compensating controls;
   - database credentials and application secrets only from the environment / Docker secrets;
   - Docker `--init` and posture drift detected by the validation script and by the launcher (already refuses);
   - artifact access and redaction review.
6. **Deployment procedures** (`docs/DEPLOYMENT.md`): install, migrate, start/stop order, TLS termination, backup and
   restore (verified), upgrade and rollback, verification script (`scripts/verify-deployment.ps1`).
7. **Production-image verification:** the release Api image (auth, database, key-volume permissions, migrations, health,
   tenant isolation) and the full stack running a real job.
8. **Carried from the former Phase 7:** ADR-056 out-of-process egress proxy with lease binding and ADR-031 hosted
   isolation are evaluated; whatever is not delivered is listed as a hosted-mode limitation (self-hosted single-tenant
   deployments do not depend on them).

## Acceptance criteria
A clean environment deploys with the documented procedure, migrates, passes the deployment verification, and runs a
real job through the release-built Api and Worker images; backup and restore are exercised; every Phase 5 limitation is
resolved or documented with its risk.

## Delivered
| Item | Where | Evidence |
|---|---|---|
| Api release image (chiseled, uid 10001, read-only root, `migrate`/`healthcheck`) | `deploy/api/Dockerfile` | production drill; dashboard e2e |
| Gateway image (unprivileged nginx, CSP, Swagger/health hidden) | `deploy/web/Dockerfile`, `nginx.conf.template` | production drill; e2e asserts zero CSP violations |
| Worker image | `deploy/worker/Dockerfile` (unchanged posture) | production drill: handshake, smoke job, graceful stop with accepted `ProcessDeathConfirmed` fence |
| Production compose (networks, roles, secrets, limits, log rotation, optional Caddy TLS) | `deploy/docker-compose.prod.yml`, `deploy/.env.prod.example`, `deploy/tls/Caddyfile` | production drill |
| Database roles (ADR-078) | `deploy/postgres/init/10-automataflow-roles.sh`, `SchemaMigrator` | `DatabaseRolesTests` (real init SQL; Api through the app role; DDL refused); drill check |
| Key ring encrypted at rest (ADR-079) | `KeyRingProtection`, `scripts/new-deployment-secrets.ps1` | `KeyRingProtectionTests`; drill: key files carry `encryptedSecret`, no `<value>` |
| Configuration validation | `scripts/validate-deployment.ps1` | `scripts/test-deployment-validation.ps1`: 18 cases (baseline valid, template refused, 15 injected drifts refused, explicit registration accepted) |
| Deployment verification | `scripts/verify-deployment.ps1` | 30 checks passed on the drill, before and after restore |
| Backup and restore | `scripts/backup.ps1`, `scripts/restore.ps1` | drill below |
| Deployment and security documentation | `docs/DEPLOYMENT.md`, `docs/SECURITY.md` | followed during the drill |

## Production drill (2026-11-20, this machine, Docker Desktop / WSL2)
1. `new-deployment-secrets.ps1` → `validate-deployment.ps1` (refused until `-AllowRegistration` for the first owner) →
   `docker compose build` → `up -d --wait`.
2. Two defects found and fixed during the drill: the gateway's tmpfs mounts were root-owned, so unprivileged nginx
   started without its configuration (its health check caught it; mounts now owned by uid 101); the smoke workflow's
   original target (example.com) no longer has a heading and asks not to be used for testing, so the smoke workflow is
   now page-independent (HTTP status + screenshot) and the target must be a site the operator chooses.
3. `verify-deployment.ps1` with a smoke job against https://httpbin.org in **SelfHosted** mode (no development
   exceptions): 30/30 checks, job Completed, HTTP 200 captured, screenshot stored and served.
4. Backup/restore: Worker stopped, a job with a sensitive (encrypted) input queued, `backup.ps1`, `down -v` (all
   deployment volumes deleted), `restore.ps1` (checksums, database as the owner role, volumes with ownership 10001/0700,
   migrate, start). The job queued before the backup then **completed**, which required decrypting its input with the
   restored, certificate-encrypted key ring. `verify-deployment.ps1` passed again.
5. Graceful stop: `docker compose stop worker` → exit 0 in about 1 s, `Worker stopped`, accepted
   `ProcessDeathConfirmed` fence (`SupervisorUnitEmptyAndDrained`).

Evidence level: **production-image-tested** on a single Docker Desktop host. Not run: a clean Linux server install, the
`tls` profile with a public DNS name (needs a real domain), more than one Worker container.

## Remaining limitations (documented, not resolved)
- Fence confirmation still uses the application role (ADR-078 "Not separated").
- Hosted-mode isolation (ADR-031) and the out-of-process egress proxy with lease binding (ADR-056) are not delivered:
  supported deployments are SelfHosted (docs/SECURITY.md §8).
- The Worker image is built on the .NET SDK image (4.1 GB); a runtime-based image is an optimization, not a security
  requirement (the supervisor and launcher constrain the processes, not the base image).
- Rollback across schema changes is restore-based (EF migrations are forward-only here).