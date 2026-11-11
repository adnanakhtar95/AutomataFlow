# AutomataFlow — Roadmap

Status: Draft v0.2. Estimates are indicative planning aids, not deadlines; quality, reliability and security come first (ADR-034). No phase starts without explicit approval.

| Phase | Title | Est. days | Status |
|---|---|---|---|
| 1 | Solution foundation and environment setup | 1.5 | done |
| 2 | Demo CRM | 2 | done |
| 3 | Playwright browser engine (incl. egress proxy, SSRF test matrix) | 6.5 | in progress |
| 4 | Workflow engine and action registry | 3.5 | |
| 5 | Background workers and job orchestration | 3 | |
| 6 | React dashboard | 3 | |
| 7 | Testing, security, Docker, hosted isolation, documentation | 2+ | |

Phase 7 is deliberately small because testing, security checks and docs are done **continuously** in every phase; Phase 7 is hardening and packaging. Risk: Phase 6 breadth; the first cut covers Jobs, Execution details and Workflow JSON editor, then the remaining screens.

## Phase 1 — Solution foundation and environment setup
Checklist (approved scope; nothing beyond it):
1. [x] Docs updated with approved decisions (CLAUDE.md, ARCHITECTURE, DECISIONS, ROADMAP).
2. [x] `git init`; `.gitignore`, `.gitattributes`, `.editorconfig`; no secrets/generated artifacts tracked.
3. [x] `global.json` (10.0.302, `rollForward: latestFeature`), `Directory.Build.props` (net10.0, nullable, implicit usings, warnings as errors, analyzers), `Directory.Packages.props` (central versions).
4. [x] 6 backend projects + 3 test projects in `src/` and `tests/` with the approved reference graph, added to `AutomataFlow.slnx`.
5. [x] Domain: `IOrganizationOwned`, `Organization`, `OrganizationMember` + `OrganizationRole`, status enums (job/record/attempt). Application: `ICurrentUser`, `Result`-style validation primitives. Nothing speculative.
6. [x] Infrastructure: `AutomataFlowDbContext` (Identity `ApplicationUser`, Organizations, OrganizationMembers), Npgsql config from environment, design-time factory, first EF migration, DI extension.
7. [x] Api: Serilog, Swagger/OpenAPI, `/health` (liveness) and `/health/ready` (DB), ProblemDetails; no business endpoints, no auth wiring yet.
8. [x] Worker: hosted-service skeleton with Serilog and graceful shutdown (no job logic yet).
9. [x] `deploy/docker-compose.yml` with `postgres:18`, host port 5433, named volume; `deploy/.env.example`; local dotnet-ef tool manifest.
10. [x] Tests: architecture/dependency-graph tests, domain unit tests, Api health integration tests (WebApplicationFactory), DB migration test against the Docker Postgres (xUnit v2, ADR-021).
11. [x] Build + test green on SDK 10; Api and Worker start; migration applied to Docker Postgres; results shown to the user.
12. [x] Commit(s) after verification; stop and wait for approval before Phase 2.

Not in Phase 1: auth endpoints/JWT wiring (Phase 5), Playwright (Phase 3), Demo CRM (Phase 2), React app (Phase 6), billing, visual workflow editor.
**Exit:** `dotnet build` and `dotnet test` green on SDK 10; Api and Worker start; Postgres reachable on 5433; migration applied; docs updated.

## Phase 2 — Demo CRM
Checklist (approved scope):
1. [x] DemoCrm Razor Pages project, fully independent of AutomataFlow projects (architecture-tested).
2. [x] Login with fictional credentials; customer registration, listing, search, details.
3. [x] Server-side validation, duplicate detection (email, phone), clear success and error messages.
4. [x] Predictable simulated failures (+fail-N, +commit-fail, +slow) and the idempotency contract (OperationId, replay, conflict).
5. [x] Deterministic seed data and safe reset (admin UI, token-protected control API).
6. [x] Tests: 139 DemoCrm tests plus architecture tests; build and all tests green in the container.
7. [x] Live demonstration, docs/DEMO_CRM.md, separate commit.

- ASP.NET Core Razor Pages demo app: login, registration, listing, search, details, duplicate detection, validation, predictable simulated temporary errors (email plus-tags).
- Stable `data-testid` attributes; fictional seed data; `samples/demo-customers.csv` (valid, invalid, duplicate and failure-scenario rows with expected outcomes, verified by a test).
- Idempotency contract (ARCHITECTURE §5.6): `operationId` accepted on registration with a unique index; replay returns the original customer/confirmation; lookup by operation id and by email for reconciliation; error simulation includes "commit then fail the response" and "slow response after commit".
**Exit:** Manual walkthrough works; endpoint tests for validation, duplicate detection, error simulation.

## Phase 3 — Playwright browser engine
Detailed plan: `docs/PHASE3_PLAN.md` (**approved**, ADR-029). Additional mandatory requirements: fail-closed egress and bypass prevention (ADR-030), policy vs network isolation (ADR-031), artifact security (ADR-032), reproducible verify image (ADR-033). Process: spike first (results in `docs/PHASE3_SPIKE.md`), stop and report if a security assumption fails, then implement.
Checklist (docs/PHASE3_PLAN.md acceptance list):
1. [x] Spike run and recorded (30/30); two safe corrections adopted (ADR-035, ADR-036).
2. [x] Playwright 1.64.1 only in Automation; Application gained only package-free ports; architecture and source tests green.
3. [x] Pinned verification image with Chromium 156 (build 1248), run non-root with sandbox seccomp profile; version-lock test.
4. [x] BrowserHost lifecycle: launch, reuse, recycle, crash replacement, per-session browsers, shutdown without orphans, launch timing.
5. [x] Fresh context per session; isolation tests (cookies, storage, service workers).
6. [x] Navigate/click/submit/fill/select/wait/getText/getAttribute with typed selectors, auto-wait and strict mode.
7. [x] Screenshots (on failure by default, pixel-verified masking) and downloads (generated names, limits, hashes, cleanup).
8. [x] Timeouts, session deadline and cancellation; classified error codes with IsTransient.
9. [x] Structured execution events; sensitive values, URL query values and proxy credentials never logged.
10. [x] Egress proxy: per-session credentials, rules, own DNS, IP policy, pinning, quotas, strict parsing.
11. [x] Chromium forced through the proxy incl. loopback; cannot resolve names; QUIC/WebRTC/service workers restricted.
12. [x] SSRF matrix (direct, encodings, IPv6, metadata, redirects, mixed DNS, rebinding, embeds, WebSockets, proxy self-target, WebRTC, non-web schemes) and fail-closed tests.
13. [x] No script-evaluation APIs (source scan); selectors are data.
14. [x] Validator enforces hosted rules (browser-per-session, sandbox, https-only, no private exceptions).
15. [x] DemoCrm end-to-end through real Chromium (separate process).
16. [x] 0 warnings; all tests green in the container (unit 223, integration 14, DemoCrm 150, browser/proxy 121).
17. [x] docs/BROWSER_ENGINE.md (incl. hosted container restrictions and image reproduction), ADRs, roadmap.
18. [x] Secrets audit, separate Phase 3 commit, clean tree.

## Phase 4 — Workflow engine and action registry
Detailed plan: `docs/PHASE4_PLAN.md` (proposed, awaiting approval; decisions D1–D7; estimate 5.0 days, range 4.5–6).
- Workflow definition model, JSON schema, validator, versioning rules.
- `IActionHandler` contract, registry, the 12 actions, variable substitution, secret masking.
- Executor with per-step timeouts, error classification (transient/permanent), result capture.
- Declarative Condition expressions.
**Exit:** Customer-registration workflow defined purely as JSON runs against Demo CRM; every action has unit/automation tests; invalid workflows rejected with precise errors.

## Phase 5 — Background workers and job orchestration
- Full EF model and migrations for the 13 entities; org-scoped query filters.
- Job creation API with CSV/JSON parsing and upload limits.
- Durable claiming (`SKIP LOCKED`, lease, heartbeat, recovery), concurrency, retries/backoff, cancellation, partial failure, job completion states, result export.
- Execution logs/artifacts persistence; ASP.NET Core Identity + JWT auth, refresh tokens, role policies.
- Tenant isolation per ARCHITECTURE §7.1: application-service scoping, write guard, cross-tenant tests (also with query filters disabled).
- OperationId, Indeterminate attempts, reconciliation and NeedsReview (ARCHITECTURE §5.6).
- Usage records written.
**Exit:** CSV demo end to end through API + Worker; worker-kill recovery test; cancellation test; retry of injected temporary errors; cross-tenant test.

## Phase 6 — React dashboard
- Scaffold Vite + TS + Tailwind; OpenAPI-generated client; auth flow.
- Cut 1: Overview, Jobs (create/upload, list, detail with records), Execution details (logs, screenshots, artifacts), Workflows (form + JSON editor with schema validation).
- Cut 2: Execution history, Data uploads, Monitoring, Integrations (stub), Settings.
- Live status via polling.
**Exit:** Operator can upload the demo CSV, watch the job progress, inspect failures, download results.

## Phase 7 — Testing, security, Docker, documentation
- Coverage gaps, security review against ARCHITECTURE §9, dependency vulnerability scan, license audit.
- Dockerfiles and full Compose stack (postgres, api, worker, demo-crm, web); non-root, resource limits.
- Operations guide, workflow authoring guide, README, updated ADRs.
- Hosted isolation (ADR-031): standalone egress-proxy host and container, browser on an `internal: true` Docker network reachable only to the proxy, proxy egress firewall, Chromium sandbox seccomp profile, resource limits; remote-browser (per-tenant browser server) implementation; detailed browser launch-time benchmarking (deferred from Phase 3, ADR-029).
**Exit:** `docker compose up` yields a working demo from a clean machine; docs verified by following them.

## After MVP (not scheduled)
Scheduling (cron), SSE live streaming, integrations (webhooks/email/storage), template library, multi-organization UX, per-tenant container isolation, usage metering + billing, white-label for agencies.
