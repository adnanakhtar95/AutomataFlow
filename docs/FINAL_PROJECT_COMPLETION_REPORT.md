# AutomataFlow — Final project completion report

**Date:** 2026-11-20. **Scope:** Phases 1–10 (docs/ROADMAP.md). Every claim below names its evidence. Evidence levels:
*unit-tested*, *integration-tested* (real Api + PostgreSQL in process), *real-process-tested* (real Worker, Chromium,
supervisor processes, DemoCrm), *production-image-tested* (release images in Docker), *environment-limited*,
*not implemented*.

## 1. Verdict
**Ready to sell** as a self-hosted or managed **single-tenant** product (one organization, or customers who trust each
other, per deployment) for authorized browser automation of back-office web applications, delivered with onboarding
by us. **Not ready** as a shared multi-tenant SaaS for mutually untrusted customers: per-tenant browser isolation
(ADR-031) and the out-of-process egress proxy with lease binding (ADR-056) are not delivered (§9).

## 2. Final architecture
Modular monolith, unchanged dependency rule (architecture tests): Domain ← Application ← (Infrastructure, Automation)
← (Api, Worker); the Api never references Automation and never runs a browser.

| Component | Role |
|---|---|
| Api (ASP.NET Core, chiseled image, uid 10001, read-only) | REST API, auth (JWT + HttpOnly refresh cookie), use cases, `migrate`, `healthcheck`, `bootstrap-operator`, `purge-artifacts` commands |
| Worker (+ supervisor, launcher) | durable claiming (`SKIP LOCKED`), PostgreSQL attempt journal, real Chromium with sandbox, egress proxy, process-death supervision and fencing (ADR-075) |
| Dashboard (React 19, Vite, Tailwind, TanStack Query) + nginx gateway | customer and platform-operator UI, same-origin `/api`, strict CSP |
| PostgreSQL 18 | all state; owner role for migrations, data-only role for the platform (ADR-078) |
| DemoCrm (test fixture only) | fictional CRM + back office for verification; never a production dependency |

## 3. Phases
| Phase | Status | Commits |
|---|---|---|
| 1 Foundation | done | (history) |
| 2 DemoCrm | done | (history) |
| 3 Browser engine | done | (history) |
| 4 Workflow engine | done | (history) |
| 5 Workers and orchestration | done | … `d247a51` |
| 6 Customer dashboard | done | `2c44172`, fixes `c3d62e6` |
| 7 Production infrastructure and security | done | `c3c4a0d` |
| 8 Onboarding and workspaces | done | `b6a1f9a` |
| 9 Commercial automation capabilities | done | `f0a5b9f` |
| 10 Commercial readiness | done | final commit (this report) |

## 4. Features delivered (customer view)
Workspaces with roles and invitations; workflow templates (3 packages); guided and JSON workflow editor with server
validation; publication with explicit duplicate-safety attestation; CSV/JSON jobs with validation summary;
live progress; record investigation (attempts, steps, redacted logs, authorized screenshots, downloads); requeue for
reconciliation; results export without inputs; encrypted write-only secrets; workspace switching.
Platform operators: onboarding, suspension, operations view, audited fences, retention, backup/restore.

**API endpoints (52):** auth (register, login, refresh, logout, switch), me, overview, workflows (list/search, create,
get, versions, update draft, validate, publish, archive), workflow catalog, workflow templates (list, get,
instantiate), jobs (create, list, get, records, cancel, requeue, results.csv), attempts and logs, artifacts, secrets,
workspace (profile, rename, members, roles, removal, invitations, usage), invitations (preview, accept), platform
(incarnations, fence, workspaces, create, suspend, reactivate, operations), system info. Contract pinned by
`OpenApiContractTests`.

**Screens:** sign-in, invitation acceptance, overview, workflows (list, detail with versions and publication, editor
with templates), jobs (list, new job, detail with record panel), secrets, members, workspace, platform (workspaces and
operations).

**Workflow capabilities:** 13 actions (navigate, click, fill, select, wait, extract, screenshot, upload, download,
condition, delay, captureResult, reconcileMatch), parameters, secrets, typed inputs incl. sensitive, setup and
reconcile sections, side-effect declaration with target-enforced idempotency, transient/permanent outcomes.

## 5. Security controls (docs/SECURITY.md)
Tenant isolation in five layers with cross-tenant tests on hosts with and without the query-filter backstop; per-request
membership and suspension checks; PBKDF2 passwords, lockout, separate credential and session rate limits; rotating
hashed refresh tokens in HttpOnly SameSite=Strict cookies; no customer code execution; sandboxed, capability-free
Chromium behind a fail-closed egress proxy with SSRF controls on all traffic; encrypted secrets and key ring at rest;
hashed one-time invitation tokens delivered as URL fragments; strict CSP gateway; non-root, read-only release images;
data-only database role; configuration validation refusing posture drift; artifacts served only to their workspace.

## 6. Test evidence (final full run, 2026-11-20, `scripts/verify-in-container.ps1`)
| Suite | Tests | Level |
|---|---|---|
| AutomataFlow.UnitTests | 1,264 passed | unit, architecture, source scans |
| DemoCrm.Tests | 165 passed | fixture (in-memory web host) |
| AutomataFlow.IntegrationTests | 136 passed | integration (real Api, PostgreSQL), incl. cross-tenant, roles, key ring, DB roles, contract |
| AutomataFlow.AutomationTests | 252 passed | real-process (Chromium with sandbox, egress proxy, Worker processes, supervision container with production posture, DemoCrm) |
| Dashboard (Vitest) | 41 passed + lint, type check, build, API-type check | component/unit |
| Deployment validation | 18 cases passed | configuration |
| Browser journeys (`scripts/e2e-dashboard.ps1`) | 2 journeys passed on release images | production-image-tested |

Incidents recorded honestly in the phase logs: environment-timing failures under Docker Desktop load (re-run alone:
passed; never counted as passes of the original run), and four real defects found by the runs and fixed (Worker
heartbeat race, launcher id-map race, shared rate limit, gateway tmpfs ownership).

## 7. End-to-end and production-image evidence
- Dashboard journey: sign-in with reload, workflow from JSON and guided form, publication, secret, CSV upload, job
  monitoring, failure investigation with authorized screenshot, export, cross-workspace "not found", sign-out, zero CSP
  violations — release images.
- Onboarding journey: bootstrap operator → onboard business → Owner joins → colleague invited → role change → suspend
  → reactivate — release images.
- Production drill (Phase 7): production compose with roles and encrypted key ring; smoke job in SelfHosted mode
  against a public test site; backup → wipe → restore with a pre-backup encrypted job completing afterwards; graceful
  stop with an accepted fence.
- Packages (Phase 9): order entry and invoice approval with injected failures — exactly 5 orders and 5 approvals at the
  target (real-process).

## 8. Performance (docs/PERFORMANCE.md)
≈78 records/min with one Worker container (2 browser sessions) on a loaded developer laptop against a local fixture;
Worker peak ≈3.3 vCPU / 430 MiB; ≈52 KB artifacts per record; Api p50 10 ms / p95 14 ms. A 20 s database outage and an
Api restart mid-job each completed 40/40 records with no duplicate side effect. Not measured: multi-Worker throughput,
soak tests, real target latency.

## 9. Known limitations and outstanding risks
| # | Item | Impact | Status |
|---|---|---|---|
| 1 | Hosted isolation (per-tenant browsers, ADR-031) and out-of-process egress proxy with lease binding (ADR-056) | not suitable for untrusted multi-tenant SaaS | not implemented |
| 2 | Worker and supervisor share the data role; fence confirmation not separated | a compromised Worker could fence itself | documented (ADR-078) |
| 3 | No Windows supervisor | Linux containers only | not implemented |
| 4 | P12 below-socket queueing | one fence-proof scenario untested | environment-limited |
| 5 | No e-mail (invitations, password reset), no SSO/MFA | manual link delivery; operator-assisted resets | not implemented |
| 6 | Multi-host deployment, multi-Worker throughput, soak and storage-full tests | capacity beyond one host unproven | not run |
| 7 | Worker image on the SDK base (4.1 GB) | slower pulls | optimization pending |
| 8 | `tls` profile with a public domain | TLS path not exercised end to end | environment-limited |
| 9 | Rollback across schema changes is restore-based | data after the backup is lost on rollback | documented |
| 10 | Template packages target the DemoCrm fixture | each customer needs selector adaptation | by design (packages/README.md) |

## 10. Deployment instructions
docs/DEPLOYMENT.md (install, TLS, onboarding with `bootstrap-operator`, start/stop, upgrade, rollback, backup and
restore, configuration reference, capacity); docs/RUNBOOK.md (operating rules, fences, monitoring, alerts, retention,
backup verification).

## 11. Remaining commercial launch requirements
1. Legal: terms of service, data processing agreement, and customer responsibility for authorization to automate target
   systems (business decision; not drafted here).
2. A production host with a domain and TLS; first run of the `tls` profile; offsite backup target and schedule.
3. Pricing and the support model of docs/PRODUCT.md confirmed by the business.
4. For shared SaaS: limitations 1–2 above, then a security review by an independent party.
5. Recommended before scale: multi-Worker throughput and 24-hour soak measurements on the production host.
