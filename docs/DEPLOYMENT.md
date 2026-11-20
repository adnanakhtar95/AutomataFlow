# AutomataFlow — Deployment guide

Single-host production deployment with Docker Compose (Phase 7). Every command below is run from the repository root
on the deployment host. Operating rules that the execution guarantees depend on are in `docs/RUNBOOK.md`; read it
before going live.

## 1. Requirements
| Item | Minimum | Notes |
|---|---|---|
| Host | Linux x86-64 with Docker Engine 25+ and Compose v2.24+ | the Worker's containment needs Linux namespaces (ADR-075); Docker Desktop works for evaluation |
| CPU / memory | 4 vCPU / 8 GB for one Worker with 2 parallel browser sessions | see "Capacity" |
| Disk | 20 GB + artifact growth | screenshots are the main growth (about 50–300 KB each) |
| Kernel | unprivileged user namespaces enabled | `sysctl kernel.unprivileged_userns_clone=1` on older Debian/Ubuntu kernels |
| Network | outbound HTTPS from the Worker to the target sites; inbound 443 to the TLS terminator | the database network is internal |
| DNS / TLS | a DNS name for the dashboard and a TLS certificate (or the bundled `tls` profile with automatic certificates) | the session cookie requires HTTPS |

PowerShell 7 (`pwsh`) runs the helper scripts on Linux; on Windows, Windows PowerShell 5.1 works too.

## 2. Components
| Service | Image | Role | Network |
|---|---|---|---|
| `postgres` | `postgres:18.6` (pinned digest) | all platform state; three roles (ADR-078) | `data` (internal) |
| `migrate` | Api image, `migrate` command | one-shot schema migration as the owner role | `data` |
| `api` | `deploy/api/Dockerfile` (chiseled, uid 10001, read-only root) | HTTP API; never runs a browser | `edge`, `data` |
| `worker` | `deploy/worker/Dockerfile` | one Worker incarnation under its supervisor; Chromium | `data`, `egress` |
| `web` | `deploy/web/Dockerfile` (unprivileged nginx) | dashboard + same-origin `/api` gateway, CSP | `edge`, published |
| `tls` (profile) | `caddy:2.10` | optional TLS terminator with automatic certificates | `edge`, 80/443 |

Volumes: `pgdata` (database), `keys` (Data Protection key ring, owner-only uid 10001), `artifacts` (screenshots and
downloads, owner-only uid 10001). Only the Api and the Worker mount `keys` and `artifacts`.

## 3. Install
```powershell
# 1. Secrets: database roles, JWT signing key, key ring certificate and its password (never overwrites).
./scripts/new-deployment-secrets.ps1            # creates deploy/.env.prod and deploy/secrets/keyring.pfx
# On Linux: only uid 10001 (Api and Worker) may read the certificate.
sudo chown 10001:10001 deploy/secrets/keyring.pfx && sudo chmod 0400 deploy/secrets/keyring.pfx

# 2. Review deploy/.env.prod: AF_DOMAIN / AF_ACME_EMAIL (tls profile) or your own TLS terminator, limits, concurrency.

# 3. Validate (refuses placeholders, weak keys, a drifted Worker posture, development settings, exposed data).
./scripts/validate-deployment.ps1 -EnvFile deploy/.env.prod                    # add -Profiles tls when used

# 4. Build the images (or set AF_*_IMAGE to your registry's digests) and start.
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml build
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml up -d --wait       # --profile tls for Caddy

# 5. Verify.
./scripts/verify-deployment.ps1 -EnvFile deploy/.env.prod
```
`up` runs, in order: `postgres` (its first start creates the roles and the empty database), `migrate` (applies the
schema as `automataflow_owner`), then `api` and `worker` (as `automataflow_app`) and `web`.

**Onboarding (Phase 8, ADR-080).** Create the first platform operator once, with credentials from the environment
(never arguments), then onboard every customer from the dashboard's Platform page:
```powershell
$env:AF_BOOTSTRAP_EMAIL = 'ops@your-company.example'
$env:AF_BOOTSTRAP_PASSWORD = '<a long unique password>'   # type it; do not keep it in shell history files
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml run --rm --no-deps `
  -e AF_BOOTSTRAP_EMAIL -e AF_BOOTSTRAP_PASSWORD api bootstrap-operator
Remove-Item Env:AF_BOOTSTRAP_PASSWORD
```
The operator signs in, opens **Platform**, creates a workspace with its Owner's e-mail address and sends the one-time
invitation link to the Owner (the platform sends no e-mail). The Owner invites their team from **Members**. Self-service
registration stays off (`AF_ALLOW_REGISTRATION=false`).

## 4. TLS and the network edge
- The gateway (`web`) speaks plain HTTP on `AF_HTTP_BIND:AF_HTTP_PORT` (default `127.0.0.1:8080`). Put a TLS terminator in
  front: the bundled `tls` profile (Caddy, automatic Let's Encrypt certificates for `AF_DOMAIN`, HSTS) or your own.
- `AF_PROXY_HOPS`: 1 when only the gateway adds `X-Forwarded-For`, 2 with the `tls` profile (or one external proxy in
  front of the gateway). The Api trusts forwarded addresses only from `AF_EDGE_SUBNET`.
- The gateway does not expose Swagger or the Api's health endpoints. Health is checked inside the containers.

## 5. Start, stop and the Worker's lifecycle
- `docker compose ... stop` / `down` stops the Worker gracefully: the supervisor (PID 1) forwards SIGTERM, waits for the
  Worker, then proves the unit empty and drained and fences unfinished attempts (ADR-075). `stop_grace_period` is 4
  minutes; do not shorten it.
- Never run the Worker with `init: true` / `--init`, extra capabilities, another seccomp profile, `privileged`, or host
  namespaces. The launcher refuses most of these, and `validate-deployment.ps1` refuses all of them.
- Scale Workers with `--scale worker=N` (one incarnation per container). Each needs its CPU/memory budget.
- After a crash or a fence the supervisor exits and Docker restarts the container as a NEW incarnation.

## 6. Upgrade and rollback
**Upgrade**
1. `./scripts/backup.ps1` (always, before any upgrade).
2. Update the source (or image digests), then `./scripts/validate-deployment.ps1`.
3. `docker compose ... build` (if building locally).
4. `docker compose ... up -d --wait`. Compose recreates changed services; `migrate` runs before the new Api and
   Workers. Workers are stopped gracefully (fences recorded) before their replacement starts.
5. `./scripts/verify-deployment.ps1`.

**Rollback**
- Without schema changes: redeploy the previous images (`AF_*_IMAGE` digests or the previous source revision).
- With schema changes: EF migrations are forward-only here. Restore the pre-upgrade backup into an EMPTY deployment
  (`scripts/restore.ps1`), with the previous images. Jobs created after the backup are lost; record which ones first.
- Never run an older Api against a newer schema.

## 7. Backup and restore
- `./scripts/backup.ps1` writes `backups/<UTC timestamp>/`: `database.dump` (pg_dump custom format, consistent while
  running), `keys.tar.gz`, `artifacts.tar.gz` and `SHA256SUMS`. Schedule it (cron/systemd timer) and copy the directory
  off the host.
- Store `deploy/.env.prod` and `deploy/secrets/keyring.pfx` SEPARATELY from the backups: the key ring in the backup is
  encrypted with that certificate; without it, workspace secrets and sensitive record inputs cannot be decrypted.
- `./scripts/restore.ps1 -Backup backups/<timestamp>` restores into an EMPTY deployment only (it refuses existing data),
  then runs `migrate` and starts everything.
- RUNBOOK rule: never restore a memory snapshot of a running Worker host, and treat a restored deployment's unfinished
  attempts through the AwaitingFence / NeedsReview procedure (docs/RUNBOOK.md).

## 8. Configuration reference
| Variable (deploy/.env.prod) | Used by | Meaning |
|---|---|---|
| `AF_DB_SUPERUSER_PASSWORD` | postgres | bootstrap superuser (init and backup only) |
| `AF_DB_OWNER_PASSWORD` | postgres, migrate | `automataflow_owner`: owns the schema; migrations only |
| `AF_DB_APP_PASSWORD` | postgres, api, worker | `automataflow_app`: data access only, no DDL |
| `AF_JWT_SIGNING_KEY` | api | HMAC key for access tokens (≥ 32 bytes); rotation signs everyone out |
| `AF_KEYRING_CERTIFICATE_FILE` / `_PASSWORD` | api, worker | certificate encrypting the Data Protection key ring at rest (ADR-079) |
| `AF_PLATFORM_MODE` | api, worker | `SelfHosted` or `Hosted` (hosted-mode limitations: docs/SECURITY.md) |
| `AF_ALLOW_REGISTRATION` | api | self-service sign-up (default false) |
| `AF_WORKER_CONCURRENCY` | worker | parallel browser sessions per Worker container |
| `AF_HTTP_BIND`, `AF_HTTP_PORT` | web | where the gateway listens |
| `AF_EDGE_SUBNET`, `AF_PROXY_HOPS` | api | forwarded-address trust |
| `AF_DOMAIN`, `AF_ACME_EMAIL` | tls | public name and ACME contact |
| `AF_*_CPUS`, `AF_*_MEMORY` | all | resource limits |
| `AF_API_IMAGE`, `AF_WORKER_IMAGE`, `AF_WEB_IMAGE` | all | registry images (pin by digest) |

Application settings can be overridden with standard ASP.NET Core environment variables (`Section__Key`) in an
override compose file; run `validate-deployment.ps1` with that file too (`-ComposeFile`).

## 9. Capacity
Measured figures and the measurement environment are in docs/PERFORMANCE.md (Phase 10). Until then: plan one vCPU and
about 1 GB of memory per parallel browser session, plus 1 GB for the Worker process and supervisor.
