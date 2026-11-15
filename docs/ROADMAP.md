# AutomataFlow — Roadmap

Status: Draft v0.2. Estimates are indicative planning aids, not deadlines; quality, reliability and security come first (ADR-034). No phase starts without explicit approval.

| Phase | Title | Est. days | Status |
|---|---|---|---|
| 1 | Solution foundation and environment setup | 1.5 | done |
| 2 | Demo CRM | 2 | done |
| 3 | Playwright browser engine (incl. egress proxy, SSRF test matrix) | 6.5 | done (approved 2026-11-12) |
| 4 | Workflow engine and action registry (incl. repeat safety, attempt journal port, DemoCrm fixture extensions) | 11.0 | approved 2026-11-15; in progress |
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
Detailed plan: `docs/PHASE4_PLAN.md`. Status:
- decisions D1–D7 approved 2026-11-12 (ADR-037..043); sink rules ADR-044;
- review rounds 2–9 decisions ADR-045..060 binding;
- DemoCrm fixture scope approved 2026-11-15 (ADR-054);
- final approval granted by the user on 2026-11-15;
- **complete (2026-11-15):** steps 1–10 implemented and verified in the pinned Linux container (ADR-061..066); authoring guide `docs/WORKFLOWS.md`; schema `docs/workflow-schema-v1.json`;
- **not verified on Windows:** the Windows connection-identity path (Smart App Control blocks local test runs; ADR-021, ADR-066);
- **carried to Phase 5 as exit criteria:** PostgreSQL journal + conformance suite, process-death supervisor (P1–P13), killed-process K tests, database cross-tenant tests, hosted-mode publication restrictions.

Estimate 11.0 days (range 10.0–13.0).

Scope:
- Workflow definition model, JSON schema, validator, versioning rules, centralized `WorkflowLimits` (ADR-055).
- `IActionHandler` contract, registry, the 13 actions (incl. `reconcileMatch`), variable substitution, secret masking, URL canonicalization.
- Executor with total per-step deadlines, error classification (only declared business outcomes are `Failed` after a side effect), result capture.
- Repeat safety:
  - `RepeatSafetyEvaluator` (first-match rules, default NeedsReview);
  - `IAttemptJournal` port with a single in-flight attempt, durable conditional side-effect checkpoint and fencing (no takeover while an attempt is unfenced, ADR-056);
  - authoritative reconciliation only with a target commit-latency contract (ADR-057);
  - test implementations and a shared conformance suite only; no customer execution in Phase 4 (ADR-058);
  - process-death supervisor and fence-confirmation contract, with journal-side evidence validation tested against a fake supervisor (ADR-059);
  - egress revocation barrier in the Phase 3 proxy: atomic admission; upstream write gate (no send begins after revocation; admitted sends drained before the abort); abortive close incl. keep-alive tunnels; handler completion;
  - connection ledger with OS connection identity; dispatch horizon = conservative fence time after write completion and connection destruction (not physical departure); latency attestation covers local buffering, transit, processing and visibility (ADR-057, ADR-060).
- Declarative Condition expressions.
- DemoCrm test-fixture extensions: exact OperationId search, registration POST counter, `+late-commit` scenario.

**Exit:** Customer-registration workflow defined purely as JSON runs against Demo CRM; every action has unit/automation tests; invalid workflows rejected with precise errors; all ambiguity, crash and concurrency scenarios pass with exact customer **and** POST counts.

## Phase 5 — Background workers and job orchestration
- Full EF model and migrations for the 13 entities; org-scoped query filters.
- Job creation API with CSV/JSON parsing and upload limits.
- Durable claiming (`SKIP LOCKED`, lease, heartbeat, recovery), concurrency, retries/backoff, cancellation, partial failure, job completion states, result export.
- Execution logs/artifacts persistence; ASP.NET Core Identity + JWT auth, refresh tokens, role policies.
- Tenant isolation per ARCHITECTURE §7.1: application-service scoping, write guard, cross-tenant tests (also with query filters disabled).
- OperationId, Indeterminate attempts, reconciliation and NeedsReview (ARCHITECTURE §5.6):
  - PostgreSQL `IAttemptJournal` (unique in-flight/unfenced constraint, conditional checkpoint, fencing tokens, lease renewal, database clock, late completion, audited `ConfirmFenceAsync`);
  - it must pass the shared Phase 4 conformance suite plus the crash (really killed Worker), fencing and concurrency tests before any customer execution;
  - process-death fence supervisor (ADR-059):
    - Worker incarnation registration;
    - cgroup v2 / Job Object containment with a startup handshake;
    - whole-unit kill with positive "unit empty" or boot-id proof;
    - supervisor socket drain over the connection ledger (Linux: dedicated network namespace, destroy by exact cookie; Windows: tuple + creation-timestamp identity, never terminates, ambiguity fails closed);
    - tests P1–P13 on real processes (incl. port-reuse P11a–P11f and below-socket queueing P12);
    - platform-operator human confirmation with audited evidence;
    - runbook rule: no memory-snapshot restore of Worker hosts;
  - `AwaitingFence` → NeedsReview after `FenceWaitSeconds`;
  - Worker fails closed without a durable, fencing journal (ADR-058);
  - scheduling from `RepeatSafetyEvaluator` decisions.
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
- Execution fencing (ADR-056): out-of-process egress proxy that binds each session credential to the attempt lease and refuses requests after lease expiry (`EgressLeaseExpired`), and orchestrator-confirmed container termination (`IsolationTerminated`), enabling automatic takeover after a frozen Worker.
- Hosted isolation (ADR-031): standalone egress-proxy host and container, browser on an `internal: true` Docker network reachable only to the proxy, proxy egress firewall, Chromium sandbox seccomp profile, resource limits; remote-browser (per-tenant browser server) implementation; detailed browser launch-time benchmarking (deferred from Phase 3, ADR-029).
**Exit:** `docker compose up` yields a working demo from a clean machine; docs verified by following them.

## After MVP (not scheduled)
Scheduling (cron), SSE live streaming, integrations (webhooks/email/storage), template library, multi-organization UX, per-tenant container isolation, usage metering + billing, white-label for agencies.
