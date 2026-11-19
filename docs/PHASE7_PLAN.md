# Phase 7 — Production infrastructure and security

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). Starts when Phase 6 meets its acceptance criteria.

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
