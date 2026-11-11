# AutomataFlow — Architecture

Status: Draft v0.1 (pre-implementation)

## 1. Style
Modular monolith, one codebase, two deployable processes (**Api** and **Worker**) sharing the same libraries and one PostgreSQL database. Clean architecture: dependencies point toward Domain.

```
 React SPA ──HTTP──> Api ──writes──> PostgreSQL <──claims── Worker ──drives──> Chromium (Playwright)
                                                                       └──────> Target web apps (DemoCrm, client portals)
```

## 2. Solution structure

```
AutomataFlow/
├─ global.json                 SDK 10.0.302 (compatible with VS 2026 18.x), rollForward latestFeature
├─ Directory.Build.props       TargetFramework, nullable, analyzers, warnings-as-errors
├─ Directory.Packages.props    Central Package Management
├─ AutomataFlow.slnx
├─ CLAUDE.md
├─ docs/
├─ deploy/                     docker-compose.yml, Dockerfiles, .env.example
├─ src/
│  ├─ AutomataFlow.Domain/
│  ├─ AutomataFlow.Application/
│  ├─ AutomataFlow.Infrastructure/
│  ├─ AutomataFlow.Automation/
│  ├─ AutomataFlow.Api/
│  ├─ AutomataFlow.Worker/
│  └─ DemoCrm/                 ASP.NET Core Razor Pages app; fully separate from Api and web; own store (SQLite or in-memory); no reference to any AutomataFlow project
├─ web/
│  └─ automataflow-web/        React + TS + Vite + Tailwind
└─ tests/
   ├─ AutomataFlow.UnitTests/
   ├─ AutomataFlow.IntegrationTests/
   └─ AutomataFlow.AutomationTests/
```

Note: the existing `AutomataFlow.slnx` is empty; projects will be added in Phase 1. `.slnx` is the supported .NET 10 solution format.

## 3. Backend projects: responsibilities and references

| Project | Responsibility | Project references | Notable packages |
|---|---|---|---|
| **Domain** | Entities, value objects, enums, domain rules (status transitions, retry-policy value objects, workflow definition model), repository/port interfaces that are pure domain concepts | none | none |
| **Application** | Use cases (commands/queries and handlers), validation (workflow, job options, uploads), application services (JobPlanner, input parsing, result export), DTOs for use-case I/O, ports it needs (`IJobStore`, `IArtifactStore`, `IClock`, `ICurrentUser`, `IWorkflowValidator`) | Domain | none (hand-written validators) |
| **Infrastructure** | EF Core `DbContext`, migrations, repositories, PostgreSQL job claiming, artifact file storage, password hashing, JWT issuing, secret protection, Serilog sinks | Domain, Application | Microsoft.EntityFrameworkCore, Npgsql.EntityFrameworkCore.PostgreSQL, Microsoft.AspNetCore.Identity.EntityFrameworkCore, Serilog.* |
| **Automation** | Playwright browser pool/context factory, workflow executor, action registry + handlers, variable substitution, navigation allow-list, workflow structural validation implementation | Domain, Application | Microsoft.Playwright |
| **Api** | Composition root for HTTP: controllers, request/response DTOs, auth, authorization policies, middleware (error → ProblemDetails, correlation ID), Swagger | Application, Infrastructure (DI registration only) | Swashbuckle or built-in OpenAPI, Serilog.AspNetCore |
| **Worker** | Composition root for background processing: hosted services for claiming, executing, retry scheduling, cancellation, stale-lease recovery | Application, Infrastructure, Automation | Microsoft.Extensions.Hosting |

Reference rules:
- Domain references nothing. Application references only Domain.
- Infrastructure and Automation are siblings and **must not reference each other**; the Worker wires them together through Application ports (e.g. Automation depends on `IArtifactStore` from Application, implemented in Infrastructure).
- Api does **not** reference Automation (it cannot run browsers). Enforced by project references and an architecture test.
- Api and Worker are the only projects that reference Infrastructure.

Test projects: UnitTests → Domain, Application, Automation (pure logic). IntegrationTests → Api, Infrastructure (Testcontainers is Apache-2.0/MIT and free; requires Docker, which is available — or a dedicated test database). AutomationTests → Automation + DemoCrm (real Chromium against the demo app).

## 4. Workflow engine (Automation + Domain)

### 4.1 Definition (versioned JSON)
```json
{
  "schemaVersion": 1,
  "name": "register-customer",
  "allowedHosts": ["localhost:5080"],
  "inputs": [{ "name": "email", "type": "string", "required": true }],
  "steps": [
    { "id": "go",    "type": "navigate", "config": { "url": "{{baseUrl}}/login" } },
    { "id": "email", "type": "fill",     "config": { "selector": "#email", "value": "{{record.email}}" }, "timeoutMs": 5000 },
    { "id": "save",  "type": "click",    "config": { "selector": "button[type=submit]" } },
    { "id": "conf",  "type": "extract",  "config": { "selector": "#confirmation", "as": "confirmationId" } },
    { "id": "out",   "type": "captureResult", "config": { "fields": { "confirmationId": "{{steps.conf.value}}" } } }
  ]
}
```
`schemaVersion` versions the *format*; `WorkflowVersions.Version` versions a customer's *workflow*. Published versions are immutable.

### 4.2 Action contract
```csharp
public interface IActionHandler
{
    string Type { get; }                                   // "fill", "click", ...
    ValidationResult Validate(JsonElement config);         // static validation at save/publish
    Task<ActionResult> ExecuteAsync(ActionContext ctx, JsonElement config, CancellationToken ct);
}
```
- `ActionContext`: Playwright page, resolved variables, logger scoped to attempt, artifact sink, navigation guard, per-step timeout.
- `ActionResult`: status, optional output values (usable as `{{steps.<id>.<key>}}`), artifacts produced, error (code, message, `IsTransient`).
- `ActionRegistry`: maps type → handler via DI (`IEnumerable<IActionHandler>`); duplicate types fail at startup.
- Per-step timeout: step `timeoutMs` ?? action default ?? workflow default, enforced with linked `CancellationTokenSource`.
- Error handling: handlers throw/return typed errors; the executor classifies them (transient vs permanent) which drives retry. Step-level `onError`: `fail` (default) | `continue`.
- Condition: evaluates a **declarative** expression tree (equals, contains, exists, and/or/not) over variables or page state (element visible/text) and runs `then`/`else` step lists. No scripting language.
- Variable substitution: `{{record.x}}`, `{{steps.id.key}}`, `{{env.NAME}}` (allow-listed secrets only), `{{job.id}}`. Missing variables fail validation or execution with a clear error. Secrets are masked in logs.

### 4.3 Execution flow per record attempt
1. Worker claims record → creates JobAttempt (Running).
2. New isolated browser context (own storage/cookies), tracing/screenshot-on-failure enabled.
3. Executor walks steps; logs each step start/finish to ExecutionLogs; stores artifacts via `IArtifactStore`.
4. Result recorded; attempt finalized; record state updated; retry scheduled if transient and attempts remain.
5. Context disposed always (finally).

## 5. Job orchestration (Worker + Infrastructure)

### 5.1 Durable claiming
Postgres is the queue. A claim query uses a transaction with `SELECT ... FOR UPDATE SKIP LOCKED` on `JobRecords` where `Status = Pending AND NextAttemptAt <= now()`, ordered by job priority/created time, limited to available concurrency slots. Claim sets `Status = Running`, `LockedBy = <workerId>`, `LeaseExpiresAt = now() + lease`. Running attempts heartbeat to extend the lease. A recovery loop returns records with expired leases to Pending (attempt marked Failed/Abandoned, counts toward retry limit). This gives at-least-once execution and restart recovery without an in-memory queue as source of truth. Optional `LISTEN/NOTIFY` later to cut polling latency.

### 5.2 Concurrency
Configured at three levels: worker-wide max browser contexts, per-job concurrency, (later) per-organization cap. Enforced at claim time.

### 5.3 Cancellation
Job `CancelRequested` flag persisted. Worker stops claiming that job's records, and running attempts observe cancellation by polling the flag on heartbeat and tripping a per-attempt `CancellationTokenSource`. Host shutdown uses the host `CancellationToken`: running attempts are released back to Pending (not failed) when possible.

### 5.4 Retry
Policy on Job (copied from workflow defaults): `maxAttempts`, backoff (fixed/exponential, jitter), retry-on classes (timeout, navigation error, transient app error). Validation and business failures (duplicate customer) are **not** retried. Backoff implemented via `NextAttemptAt`.

### 5.5 Completion
When no records are Pending/Running: Completed (all succeeded), CompletedWithErrors (mixed), Failed (all failed or fatal), Cancelled. Results exportable as CSV/JSON of successful (and optionally failed) records.

### 5.6 Duplicate side effects: operation identifiers, duplicate detection, reconciliation
Execution is at-least-once: a worker can crash, or a request can time out, **after** the target system committed. A pre-check step ("search, then create if missing") is racy and cannot close that window on its own. The platform therefore provides three cooperating mechanisms:

1. **Stable operation identifier.** Every JobRecord has an `OperationId` (a GUID generated once at record creation, persisted, identical across all attempts and retries). It is exposed to workflows as `{{operation.id}}`. Never regenerated on retry.
2. **Target-side duplicate detection (the real guard).** Where the target system can cooperate, the operation id is submitted with the action and the target enforces uniqueness on it (and on business keys such as email). Replaying the same operation id returns the original result instead of creating a second record. The Demo CRM implements this contract: hidden `operationId` field / `Idempotency-Key` header, a unique index on it, and a distinct "already processed" confirmation response that returns the original customer id. For third-party targets that cannot cooperate, the workflow declares a business-key reconciliation lookup instead (below) and the platform marks the operation as *at risk* in docs/UI.
3. **Reconciliation after ambiguous outcomes.** A step flagged `sideEffect: true` (e.g. the final submit click) that ends in timeout, crash, lost lease or navigation error leaves the attempt **Indeterminate** (not Failed). The next attempt for that record does not blindly repeat the action: it first runs the workflow's `reconcile` steps (search by operation id or business key, extract the confirmation). Outcomes: *found* → record Succeeded with the reconciled result, attempt flagged `Reconciled`; *not found* → safe to run the normal steps again; *inconclusive* → record moves to `NeedsReview` for a human instead of retrying. Lease-expiry recovery (§5.1) applies the same rule: an attempt that died after any side-effect step becomes Indeterminate.

Retry classification is therefore: transient + no side effect started → plain retry; transient after a side-effect step → reconcile first; permanent (validation, duplicate business key) → fail, no retry. Job/record counters and result exports distinguish `Reconciled` and `NeedsReview`. Demo CRM acceptance tests inject "commit then fail response" errors to prove no duplicate customers are created.

## 6. Monitoring
- `ExecutionLogs`: per attempt, sequence number, timestamp, level, step id, message template, structured properties (JSON). Written in batches by the worker.
- `ExecutionArtifacts`: type (Screenshot, Download, Trace, ResultFile), storage key, size, content type, checksum. Binary content on disk (local volume MVP; `IArtifactStore` allows S3-compatible later). Served only via an authorized API endpoint — never static files.
- Serilog JSON logs to console (+ file in dev); correlation properties JobId/RecordId/AttemptId/OrganizationId pushed with `LogContext`.
- Live status: dashboard polls job/record summaries in MVP; Server-Sent Events later (post-MVP). Avoid SignalR until necessary.

## 7. Data model

All customer-owned tables have `OrganizationId` (indexed, first column in composite indexes) and a global EF query filter. PKs are GUIDs (UUIDv7 where available) to be non-enumerable.

| Entity | Key fields | Notes |
|---|---|---|
| Organizations | Id, Name, Slug, CreatedAt | tenant root |
| Users (Identity `AspNetUsers`) | Id, Email (unique), PasswordHash, lockout fields, CreatedAt | global identity; `ApplicationUser : IdentityUser<Guid>` in Infrastructure |
| OrganizationMembers | OrganizationId, UserId, Role | Owner/Admin/Operator/Viewer |
| Workflows | Id, OrganizationId, Name, Description, LatestVersion | logical workflow |
| WorkflowVersions | Id, WorkflowId, OrganizationId, Version, DefinitionJson (jsonb), SchemaVersion, Status (Draft/Published/Archived), PublishedAt | immutable once Published |
| WorkflowInputs | Id, WorkflowVersionId, OrganizationId, Name, Type, Required, DefaultValue | declared input schema (also in JSON; denormalized for UI/validation) |
| Jobs | Id, OrganizationId, WorkflowVersionId, Status, OptionsJson (concurrency/retry/timeouts), CancelRequested, CreatedBy, CreatedAt, StartedAt, FinishedAt, counters | |
| JobRecords | Id, JobId, OrganizationId, RowNumber, **OperationId (stable across attempts)**, InputJson, Status (adds NeedsReview), AttemptCount, NextAttemptAt, LockedBy, LeaseExpiresAt, ResultJson, ErrorCode, ErrorMessage | unit of work claimed by workers |
| JobAttempts | Id, JobRecordId, OrganizationId, AttemptNumber, Status, WorkerId, StartedAt, FinishedAt, ErrorJson | |
| ExecutionLogs | Id (bigint), JobAttemptId, OrganizationId, Seq, Timestamp, Level, StepId, Message, PropertiesJson | high volume; consider partitioning/retention later |
| ExecutionArtifacts | Id, JobAttemptId, OrganizationId, Type, StorageKey, FileName, ContentType, SizeBytes, Sha256, CreatedAt | |
| Integrations | Id, OrganizationId, Type, Name, ConfigJson (secrets encrypted), CreatedAt | webhooks etc.; schema only in MVP |
| UsageRecords | Id, OrganizationId, JobId, Metric, Quantity, RecordedAt | written from day one (cheap), billed later |

Secrets in workflow/integration config are stored encrypted using ASP.NET Core Data Protection (keys outside the DB or protected) or referenced by name from environment variables; never plain in `DefinitionJson`.

### 7.1 Tenant isolation: defense in depth
EF Core global query filters are only the **last** line of defense. Layers, all required:
1. **Authentication → identity.** JWT carries user id and active organization id; `ICurrentUser` (Application port, implemented in Api/Worker) exposes them. Organization membership and role are verified server-side per request, not trusted from the token alone.
2. **Application services.** Every use case receives `ICurrentUser`, authorizes the action against the membership role, and passes `OrganizationId` explicitly to every repository/store method (no repository method that fetches customer data without an organization argument). Every id arriving from a client (workflow id, job id, artifact id, ...) is loaded **together with** the organization id; a miss returns 404, never 403 on foreign data.
3. **Repositories/EF.** Global query filter on every `IOrganizationOwned` entity as a backstop; `IgnoreQueryFilters()` is forbidden outside reviewed, named cases (worker claim queries, which are scoped by job).
4. **Write-side guard.** `SaveChanges` rejects adding/modifying an `IOrganizationOwned` entity whose `OrganizationId` differs from the current organization (worker contexts set it from the job being processed).
5. **Storage.** Artifact paths and keys include the organization id; artifact endpoints re-check ownership.
6. **Tests.** Every customer-data feature ships with a cross-tenant test (org B requests org A's resource by id → 404, and lists never leak). Tests also run with query filters disabled to prove the application layer enforces scoping on its own. An architecture/reflection test asserts every entity with an `OrganizationId` implements `IOrganizationOwned`.
7. **Later (hosted SaaS):** evaluate PostgreSQL Row-Level Security as an additional database-enforced layer.

## 8. API
REST + OpenAPI. Versioned route prefix `/api/v1`. Errors as RFC 7807 ProblemDetails. Uploads: multipart with size/extension/content-type limits; parsed and validated into records on the server. Pagination on all list endpoints.

**The Api is independent of the Automation project**: no direct or transitive reference. It never launches a browser, and workflow semantic validation that needs action handlers goes through an Application-level `IWorkflowValidator`; the structural JSON-schema/definition validation lives in Application/Domain (hand-written), while the Worker's Automation layer re-validates action-specific configuration before execution. (If the action catalogue must be shown in the UI, it is exposed as static metadata in Application, not by referencing Automation.)

### Authentication and authorization (MVP)
- ASP.NET Core Identity (`ApplicationUser : IdentityUser<Guid>` and `IdentityRole<Guid>` in Infrastructure) for user storage, password hashing (Identity's default PBKDF2-HMAC-SHA512 hasher with a high iteration count), lockout and password policy. Registration/login/refresh endpoints are ours (`AddIdentityCore`, no cookie UI, no Identity UI/Razor pages).
- JWT bearer: short-lived access token (e.g. 15 min), signing key from configuration (environment variable / user-secrets, min 256-bit, never committed), claims `sub`, `org` (active organization), `role`. Refresh tokens: random, stored hashed, rotated on use, revocable. Validate issuer, audience, lifetime, signing key; clock skew small.
- Authorization policies by organization role (Owner/Admin/Operator/Viewer) plus the application-layer ownership checks in §7.1. Rate limiting on login. No external IdP in MVP.
- Phasing: Phase 1 lays the foundations only (Identity-based schema and first migration). Register/login/refresh, JWT wiring and policies are delivered in Phase 5 together with the job API, so that every business endpoint ships protected. Until then the Api exposes only health and Swagger.

## 9. Security architecture
| Concern | Control |
|---|---|
| Credentials/secrets | Env vars/user-secrets; masked in logs; encrypted at rest when stored; `.env` git-ignored |
| Untrusted workflow content | Declarative JSON only; schema + semantic validation; no script actions; selectors are data, not code |
| Navigation / SSRF | Layered, see §9.1. Allow-list enforced on **every** browser request (navigations, redirect hops, subresources, frames, popups, WebSockets), plus DNS-resolution checks and an egress proxy in hosted mode. Private/loopback/link-local/metadata ranges blocked unless explicitly allowed (dev/demo only) |
| Browser isolation | New context per attempt; no persistent profiles; contexts disposed in `finally`; downloads to per-attempt temp dir |
| Uploads | Size limits, extension allow-list, content sniffing, generated storage names, never executed |
| Tenant isolation | OrganizationId on all rows, EF global filters, authorization checks at use-case level, cross-tenant integration tests |
| Artifacts | Authorized endpoint only; short-lived signed URLs if needed |
| Hosted untrusted workloads | Worker runs Chromium in a container with non-root user, read-only FS where possible, CPU/memory limits, no access to DB network beyond what the worker needs; later: one container per tenant/job |
| Auth | Argon2id/PBKDF2 hashing via ASP.NET Identity hasher, lockout, rate limiting on login |
| Supply chain | Central package management, pinned versions, `dotnet list package --vulnerable` in CI |

### 9.1 SSRF protection design (implemented in Phase 3, tested with hostile fixtures)
Checking only the first `Navigate` URL is insufficient: redirects, iframes, `fetch`/XHR/images/scripts, popups, WebSockets and DNS tricks all reach the network. Controls, from the browser outward:
1. **Context-wide request interception** (`context.RouteAsync("**/*")`): every request — document, subresource, frame, redirect hop (each hop is a new request), popup/new page — is evaluated by the `NavigationGuard`: scheme must be `https`/`http` (plus `about:blank`, and `data:`/`blob:` only for same-origin subresources if proven necessary); host and port must match the workflow's `allowedHosts`; userinfo in URLs rejected; IP-literal hosts (including decimal/octal/hex/IPv6-mapped forms) normalized and checked against the IP policy. Violations are aborted and logged as security events; a blocked top-level navigation fails the attempt.
2. **WebSockets and other channels:** WebSocket routing is intercepted with the same guard; service workers blocked (`ServiceWorkers = Block`); Chromium flags/policies disable non-HTTP protocols, extensions, WebRTC IP leakage, and `file://` access; downloads go to a per-attempt directory.
3. **DNS resolution and rebinding.** Hostnames are resolved by our guard and every returned address must pass the IP policy (deny loopback, private RFC 1918, CGNAT, link-local incl. `169.254.169.254`, unique-local/IPv6 ULA, multicast, unspecified, IPv4-mapped IPv6 of those). Because Chromium performs its own resolution, a check-then-connect gap (DNS rebinding) remains for in-process checks alone. Therefore, **in hosted mode all browser traffic goes through an egress proxy** (Playwright `proxy` option) that resolves, validates and connects to the validated IP itself (pinning), and the worker container's network policy allows egress only to that proxy. Chromium `--host-resolver-rules` is used to prevent direct resolution where possible. For local/dev/demo mode, the guard allows explicitly configured loopback hosts (Demo CRM) only.
4. **Response-side:** redirects to a non-allowed host are blocked at the redirect hop; HSTS/HTTPS-only option for hosted mode.
5. **Defense beyond the browser:** the worker has no credentials to internal services in its environment; DB access limited to what it needs; cloud-metadata endpoint unreachable by network policy; screenshots/artifacts never include secrets (masked fields).
6. **Tests:** automation tests with a hostile local fixture server covering: redirect to a disallowed host, redirect chain ending on a private IP, iframe/subresource to a disallowed host, `fetch` from page script, popup, WebSocket, `data:`/`file:` URLs, IP encodings, DNS name resolving to a private IP, and a rebinding fixture (documented limitation test for non-proxy mode, enforced in proxy mode).

## 10. Frontend
React + TypeScript + Vite + Tailwind. Structure by feature (`features/jobs`, `features/workflows`, ...). API client generated from OpenAPI (e.g. `openapi-typescript`, MIT) to keep types in sync. Server state via TanStack Query (MIT). JSON editor: Monaco (MIT) with JSON Schema from the backend for validation. No drag-and-drop editor in MVP.

## 11. Deployment (Docker Compose)
Services: `postgres`, `api`, `worker`, `demo-crm`, `web` (static, via nginx or Vite preview in dev). Worker image based on the official Playwright .NET image (includes browsers/deps). Shared volume for artifacts (api read, worker write). Configuration via `.env`. Local dev: PostgreSQL runs only in Docker (`postgres:18`, host port 5433, named volume `automataflow_pgdata`); the locally installed PostgreSQL service on 5432 is not used. Credentials come from `deploy/.env` (git-ignored; `.env.example` is committed with placeholders).

## 12. Technical risks

| # | Risk | Impact | Mitigation |
|---|---|---|---|
| 1 | Default SDK on the machine is 11.0.100 (preview), not 10 | Wrong TFM / non-reproducible builds | `global.json` selects the VS-2026-compatible .NET 10 SDK (10.0.302), `rollForward: latestFeature` |
| 2 | At-least-once execution duplicates side effects in target systems | Duplicate customers, orders | Stable OperationId, target-side uniqueness, reconciliation of indeterminate attempts, NeedsReview state (§5.6) |
| 3 | Chromium is memory/CPU-heavy; concurrency easily exhausts a machine | Flaky runs, OOM | Worker-wide context cap, per-job limit, resource limits in Docker, backpressure at claim time |
| 4 | Flaky selectors / timing on third-party UIs | Low success rate, support cost | Auto-wait locators (role/text/test-id), explicit waits, screenshots+traces on failure, retry on transient only |
| 5 | SSRF / data exfiltration through customer workflows (redirects, subresources, frames, DNS rebinding) | Severe in hosted SaaS | Context-wide request guard, DNS/IP policy, egress proxy with IP pinning + network policy in hosted mode (§9.1) |
| 6 | Cross-tenant data leakage | Severe, reputational | Layered scoping (§7.1): application-service checks, explicit org parameters, EF filters as backstop only, write guard, cross-tenant tests with filters disabled |
| 7 | Playwright browser install in CI/Docker | Build failures, large images | Use official Playwright image; cache browsers; `playwright install` step documented |
| 8 | Log/artifact volume growth | Cost, DB bloat | Batched writes, retention policy, artifact size caps, possible log partitioning |
| 9 | Condition/expression language creep toward a scripting language | Security, complexity | Fixed declarative operator set; resist adding eval-like features (ADR) |
| 10 | Workflow definition schema evolution | Breaks old versions | `schemaVersion`, immutable published versions, upgrader chain tested |
| 11 | Claim query contention/long leases | Stuck or double-run records | `SKIP LOCKED`, short leases + heartbeat, recovery loop, integration tests with killed worker |
| 12 | Windows dev vs Linux containers (paths, line endings, Playwright deps) | "Works on my machine" | Develop with Linux containers parity, `.gitattributes`, avoid OS-specific paths |
| 13 | Scope creep toward SaaS before engine proven | Schedule slip | Roadmap gates; billing deferred; UsageRecords only written, not billed |
| 14 | 16-day estimate optimism (Playwright edge cases, React dashboard breadth) | Overrun | Phases have exit criteria; Phase 6 scoped to essentials first (see ROADMAP) |
| 15 | PostgreSQL port/credentials conflict between installed service and Docker container | Confusing failures | Compose maps container to non-default host port (e.g. 5433) or dev uses one source consistently |
| 16 | Package licensing (paid/restrictive) | Violates rule 11 | License review per package; prefer MIT/Apache-2.0; avoid MediatR/AutoMapper/MassTransit-type commercial-licensed libs |
