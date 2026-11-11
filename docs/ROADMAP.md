# AutomataFlow — Roadmap

Status: Draft v0.1. Estimate: ~16 working days, subject to actual complexity. No phase starts without explicit approval.

| Phase | Title | Est. days |
|---|---|---|
| 1 | Solution foundation and environment setup | 1.5 |
| 2 | Demo CRM | 2 |
| 3 | Playwright browser engine | 2 |
| 4 | Workflow engine and action registry | 3.5 |
| 5 | Background workers and job orchestration | 3 |
| 6 | React dashboard | 3 |
| 7 | Testing, security, Docker, documentation | 1 |
| | Total | 16 (realistic range 16–20) |

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
Detailed plan: `docs/PHASE3_PLAN.md` (proposed, awaiting approval; estimate revised to 6.5 days, range 5.5–7.5).
- `BrowserManager`/context factory, isolated contexts, tracing and screenshots, download handling.
- Full SSRF protection per ARCHITECTURE §9.1: guard on all requests (redirects, subresources, frames, popups, WebSockets), service workers blocked, DNS/IP policy, egress-proxy design with a hostile-fixture test suite.
- Playwright browser install procedure documented/scripted.
- Hard-coded smoke automation of Demo CRM (throwaway test, not product logic) proving the engine.
**Exit:** AutomationTests log in and register a customer in real Chromium; disallowed host is blocked; contexts isolated.

## Phase 4 — Workflow engine and action registry
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
**Exit:** `docker compose up` yields a working demo from a clean machine; docs verified by following them.

## After MVP (not scheduled)
Scheduling (cron), SSE live streaming, integrations (webhooks/email/storage), template library, multi-organization UX, per-tenant container isolation, usage metering + billing, white-label for agencies.
