# AutomataFlow — Architecture Decision Records

Format: ID, status (Accepted / Proposed / Superseded), context, decision, consequences. Add new ADRs at the bottom; never rewrite history — supersede instead.

---

## Environment snapshot (inspected 2026-11-11)

| Tool | Result |
|---|---|
| .NET SDKs | 8.0.423, **10.0.302**, 11.0.100 (default, because it is the highest) |
| .NET runtimes | 8.0.29, 10.0.10, 10.0.15, 11.0.0 (ASP.NET Core + NETCore + WindowsDesktop) |
| Visual Studio | Community **2026 (18.12.12405.243)**; also VS Build Tools 2022 (17.14) and SSMS 22 |
| Node.js / npm | v24.19.0 / 11.17.0 |
| Git | 2.55.0.windows.5 (global user identity already configured) |
| Docker | 29.8.1, Compose v5.5.1, daemon running (Docker Desktop) |
| Local PostgreSQL | Service `postgresql-x64-18` on 5432 — **not used by this project** (ADR-013) |
| PowerShell | Windows PowerShell 5.1 (`pwsh` 7 not installed) |
| NuGet sources | nuget.org, Visual Studio offline packages |

---

## ADR-001 — Modular monolith with separate Api and Worker processes
**Status:** Accepted. Long-running browser work must not run in HTTP requests; Api and Worker scale and fail independently while sharing code and one database. No microservices.

## ADR-002 — PostgreSQL as the durable job queue
**Status:** Accepted. `FOR UPDATE SKIP LOCKED` + leases + heartbeat. No broker (RabbitMQ/Kafka) in MVP; reconsider only on measured need.

## ADR-003 — Declarative versioned JSON workflows; no custom code
**Status:** Accepted. Conditions use a fixed declarative operator set; no scripting. Published versions immutable.

## ADR-004 — SDK selection and `global.json`
**Status:** Accepted (user decision, 2026-11-11). Inspected: Visual Studio 2026 18.12 is installed; the only .NET 10 SDK present is 10.0.302 (alongside 8.0.423 and the 11.0.100 preview, which is the machine default). `global.json` selects `10.0.302` with `rollForward: latestFeature` and `allowPrerelease: false`, so builds never use SDK 11 but a future VS-delivered 10.0.x SDK is accepted. Revisit when VS updates ship a newer 10.0.x band.

## ADR-005 — Central Package Management and strict build settings
**Status:** Accepted. `Directory.Packages.props` for pinned versions; `Directory.Build.props` with `Nullable`, `ImplicitUsings`, `TreatWarningsAsErrors`, `AnalysisLevel=latest-recommended`. CA1848 (LoggerMessage delegates) is disabled in `.editorconfig`: we log with plain structured templates for readability; revisit if a hot path needs the source generator.

## ADR-006 — Multi-tenant-ready schema from day one, single-org UX in MVP
**Status:** Accepted. `OrganizationId` on every customer-owned table. Tenant security is **layered** — see ADR-014; EF global query filters alone are insufficient.

## ADR-007 — Avoid commercially licensed libraries
**Status:** Accepted. Rule 11 (no paid dependencies). Do not use MediatR, AutoMapper, MassTransit, FluentAssertions v8+ (commercial licence) or similar; use plain handler classes, manual mapping and xUnit's built-in assertions. Verify the licence of each package before adding.

## ADR-008 — Artifact storage behind `IArtifactStore`, local disk in MVP
**Status:** Accepted. Files on a shared volume, metadata in `ExecutionArtifacts`. Served only through authorized endpoints. S3-compatible implementation later.

## ADR-009 — Live status by polling first
**Status:** Accepted. Dashboard polls; Server-Sent Events after MVP. Avoids SignalR complexity early.

## ADR-010 — Authentication: ASP.NET Core Identity + JWT bearer
**Status:** Accepted (user decision). Identity (`IdentityUser<Guid>`) for users and secure password hashing; JWT access tokens with `sub`/`org`/`role` claims; hashed, rotated refresh tokens; role policies plus application-layer ownership checks. No Identity UI, no external IdP in MVP. Delivered in Phase 5; Phase 1 only creates the Identity-based schema. Details: ARCHITECTURE §8.

## ADR-011 — Demo CRM: ASP.NET Core Razor Pages
**Status:** Accepted (user decision). Separate application (`src/DemoCrm`), no reference to AutomataFlow projects, own store, stable `data-testid` hooks. Implements the idempotency contract of ADR-015.

## ADR-012 — Network-level SSRF protection for all browser traffic
**Status:** Accepted (strengthened by user, 2026-11-11). Protection covers redirects, subresources, frames, popups, WebSockets, service workers and DNS resolution — not just the initial navigation. In hosted mode, traffic goes through an egress proxy that validates and pins resolved IPs; in-process checks alone cannot close DNS rebinding. Details and test plan: ARCHITECTURE §9.1.

## ADR-013 — PostgreSQL via Docker only: `postgres:18`, host port 5433, named volume
**Status:** Accepted (user decision). Compose service `postgres` using the official `postgres:18` image, host port 5433 (container 5432), named volume `automataflow_pgdata`. Credentials only from git-ignored `deploy/.env`; `deploy/.env.example` has placeholders. The locally installed PostgreSQL 18 service on 5432 is neither used nor modified.

## ADR-014 — Tenant isolation is defense in depth, not EF query filters
**Status:** Accepted (user requirement). Application services/use cases enforce organization scoping explicitly (`ICurrentUser`, explicit organization arguments on repositories, ownership verification of every client-supplied id, 404 on foreign data). EF global filters are a backstop; a `SaveChanges` guard rejects cross-organization writes; cross-tenant tests run with filters both on and off. Details: ARCHITECTURE §7.1.

## ADR-015 — Duplicate side-effect prevention: OperationId, target-side detection, reconciliation
**Status:** Accepted (user requirement). Pre-checks alone are racy. Each JobRecord has a stable `OperationId`; the target enforces uniqueness on it (Demo CRM does); ambiguous outcomes produce Indeterminate attempts that are reconciled by lookup before any repeat, with `NeedsReview` as the human fallback. Details: ARCHITECTURE §5.6.

## ADR-016 — Hand-written validators
**Status:** Accepted (user decision). No validation library. Revisit only if validator code becomes a measurable maintenance burden.

## ADR-017 — Api independent of Automation
**Status:** Accepted (user requirement). No direct or transitive reference from Api to Automation; enforced by an architecture test. Infrastructure and Automation are also mutually independent.

## ADR-018 — Identity types live in Infrastructure; Domain stays package-free
**Status:** Accepted. `ApplicationUser` (Identity) is an Infrastructure type. Domain refers to users by `Guid UserId` only (e.g. `OrganizationMember.UserId`). Keeps Domain free of framework dependencies.

## ADR-019 — Frontend library choices deferred to Phase 6
**Status:** Accepted (user decision). TanStack Query, Monaco and openapi-typescript are not installed until Phase 6, when they will be re-evaluated. Frontend lives in `web/automataflow-web`.

## ADR-020 — Phase 1 package set
**Status:** Accepted. Verified stable on nuget.org (2026-11-11): Microsoft.EntityFrameworkCore(.Design) / Identity.EntityFrameworkCore / Mvc.Testing / Hosting 10.0.15; Npgsql.EntityFrameworkCore.PostgreSQL 10.0.3; Serilog.AspNetCore 10.0.0 (+ Sinks.Console 6.1.1); Swashbuckle.AspNetCore 10.3.1; xunit.v3 4.0.3 with xunit.runner.visualstudio and Microsoft.NET.Test.Sdk. Playwright (1.64.1 at time of writing) is added in Phase 3 only; dotnet-ef is a local tool.

---

## ADR-021 — xUnit v2 + VSTest (not xUnit v3) because of Windows Smart App Control
**Status:** Accepted (2026-11-11, found during Phase 1). The primary dev machine runs Windows Smart App Control in enforcement mode, which blocks freshly built unsigned native apphosts and, intermittently, unsigned assemblies. xUnit v3 test projects *require* a native apphost, so they could not run at all. xUnit v2 (2.9.3, runner 3.1.5, Microsoft.NET.Test.Sdk 18.11.1) runs under the Microsoft-signed VSTest host; it is still xUnit and still supported. Even so, some assemblies are blocked intermittently on the host, so authoritative verification is `scripts/verify-in-container.ps1`, which builds and tests in the official `mcr.microsoft.com/dotnet/sdk:10.0` Linux container against the compose PostgreSQL. We did **not** disable Smart App Control (a machine-wide, effectively irreversible security setting); that remains the user's decision. Database tests fail loudly (no silent skips) when `AUTOMATAFLOW_TEST_DB` is not set. Revisit xUnit v3 if the machine policy changes (e.g. a Dev Drive or signing arrangement).

## ADR-022 — Phase 1 scope boundary for authentication
**Status:** Accepted. Identity + JWT (ADR-010) is the approved design. Phase 1 ships only the Identity-based schema/migration; registration, login, refresh, JWT wiring and policies land in Phase 5 together with the first business endpoints so nothing ships unprotected. The Api currently exposes only anonymous health, Swagger (Development only) and `GET /api/v1/system/info` (name and version).

## ADR-023 — Package-set correction: xUnit v2
**Status:** Accepted. Supersedes the xunit.v3 entry in ADR-020: the test packages are xunit 2.9.3, xunit.runner.visualstudio 3.1.5 and Microsoft.NET.Test.Sdk 18.11.1. Other ADR-020 versions are unchanged. Serilog.Extensions.Hosting 10.0.0 and Serilog.Settings.Configuration 10.0.1 were added for the Worker.

---

## ADR-024 — DemoCrm uses a thread-safe in-memory store
**Status:** Accepted (2026-11-11, Phase 2). The in-memory store lives behind ICustomerStore and uses one lock so registration (idempotency check, duplicate checks, insert) is atomic. Chosen over SQLite because SQLite ships an unsigned native library that Windows Smart App Control is likely to block, and because a restart or reset gives a byte-identical seed, which makes demos and tests reproducible. Trade-off: data does not survive a DemoCrm restart, which is acceptable for a demo target. Persistence can be added behind the same interface if ever needed.

## ADR-025 — Phase 2 changes to Phase 1 files
**Status:** Accepted. Two minimal, necessary edits to completed Phase 1 files: (1) tests/AutomataFlow.UnitTests/Architecture/DependencyGraphTests.cs asserted that src/ contains exactly the six AutomataFlow projects; DemoCrm is now an approved seventh project with zero references, and new tests assert that DemoCrm references nothing from AutomataFlow, that no AutomataFlow source project references DemoCrm, and that DemoCrm.Tests references only DemoCrm. (2) .editorconfig also disables CA1873 (logging-argument evaluation), the same family as the already disabled CA1848 (ADR-005): we log with plain structured message templates. The solution file gained DemoCrm and DemoCrm.Tests. No other Phase 1 file changed.

## ADR-026 — DemoCrm failure simulation is keyed on e-mail plus-tags
**Status:** Accepted. Failures must be predictable for automated tests, so the scenario is selected by the plus-tag of the submitted email (+fail-N, +commit-fail, +slow) and attempts are counted per address. Validation runs first and does not consume attempts. The commit-fail scenario stores the customer and then returns 503, which is the exact lost-response case that ADR-015 reconciliation must handle. Documented in docs/DEMO_CRM.md.

## ADR-027 — Test project layout and the DemoCrm dependency boundary
**Status:** Accepted, amended 2026-11-11 by the user (Phase 2 approval). DemoCrm has its own test project tests/DemoCrm.Tests (xUnit v2, WebApplicationFactory) that references only DemoCrm, so the independence rule stays checkable. Exactly ONE additional edge is permitted: **AutomataFlow.AutomationTests -> DemoCrm**, used exclusively to build and launch DemoCrm as a real-browser automation test target. Everything else stays forbidden: no production AutomataFlow project (Domain, Application, Infrastructure, Automation, Api, Worker), directly or transitively, may depend on DemoCrm, and neither UnitTests nor IntegrationTests may. The allow-list is enforced by architecture tests (Only_DemoCrm_Tests_and_AutomationTests_may_reference_DemoCrm, No_production_project_depends_on_DemoCrm_directly_or_transitively, Unit_and_integration_tests_never_reference_DemoCrm, AutomationTests_may_reference_only_Automation_and_DemoCrm). The edge itself is added in Phase 3 when the first browser test needs it; until then the tests merely permit it.

## ADR-028 — DemoCrm control token comes only from the environment
**Status:** Accepted (user decision, 2026-11-11). The reset/state control token is read from the environment variable DemoCrm__ControlApi__Token (standard .NET mapping of double underscore to the colon-separated key DemoCrm:ControlApi:Token). appsettings.Development.json, which held a placeholder token, was removed; every committed appsettings file keeps the token empty and a test fails if one does not. Behaviour: token missing or empty -> control endpoints return 404 (disabled); missing or wrong header -> 401 (constant-time comparison). The admin UI reset is independent of the token and requires a signed-in user plus antiforgery validation (both covered by tests). scripts/run-demo-crm.ps1 uses the variable from the shell or generates a random per-run value. History note: the placeholder value used in commit 6a9a1a1 is a fictional development constant, was never a real secret, and is retired; commit 6a9a1a1 is intentionally left intact (no history rewrite).

## ADR-029 — Phase 3 plan approved (docs/PHASE3_PLAN.md)
**Status:** Accepted (user decision, 2026-11-11). All seven plan decisions are approved:
1. A mandatory egress proxy in every execution mode, running in-process (Worker) for now.
2. Shared browser for Development/SelfHosted; one browser process per session enforced for HostedMultiTenant.
3. Our own pinned verification image (ADR-033).
4. DemoCrm launched as a real child process for browser tests.
5. Logging and artifact-storage ports in Application, implemented by Infrastructure in Phase 5.
6. Logging defaults: URL query strings redacted, tracing off, screenshots only on failure, credentials and sensitive input values never logged.
7. The 6.5-day estimate is accepted. The remote-browser implementation and detailed launch-time benchmarking move to Phase 7; basic lifecycle timing measurements stay in Phase 3.

## ADR-030 — Fail-closed egress and proxy-bypass prevention
**Status:** Accepted (user requirements A and B).
- **Fail-closed:** browser execution fails when the egress proxy is unavailable. Chromium must never fall back to direct network access.
  - The browser is launched with a fixed proxy and no DIRECT fallback.
  - The launch-level proxy is a closed placeholder endpoint, so a context created without its per-session proxy cannot reach anything.
  - A session is not created unless the proxy is listening.
  - Loss of the proxy mid-session makes requests fail; they are never retried directly.
- **Bypass prevention (each case verified by tests):**
  - Redirects, subresources, frames, WebSockets and IPv4/IPv6 literals all go through the proxy, including loopback (`<-loopback>` bypass rule).
  - DNS: Chromium cannot resolve names itself (`--host-resolver-rules`); the proxy resolves them.
  - QUIC disabled; WebRTC restricted to proxied traffic (`disable_non_proxied_udp`); service workers blocked; background networking disabled.
- **Stop rule:** if any of these cannot be demonstrated, implementation stops and the issue is reported instead of being worked around.

## ADR-031 — Policy enforcement (dev/self-hosted) versus network isolation (hosted)
**Status:** Accepted (user requirement D).
- **In-process egress proxy (Development/SelfHosted):** this is *policy enforcement and auditing*. It is **not** a hard network boundary, because a fully compromised browser process sharing the Worker's network namespace could open sockets directly.
- **Hard boundary (HostedMultiTenant, built in Phase 7):** container-level network isolation:
  - The browser runs on a Docker network with `internal: true` whose only reachable peer is the egress proxy.
  - The database and other services are not on that network.
  - The proxy container has its own egress firewall that drops private, loopback, link-local and metadata destinations.
  - Chromium sandbox on, non-root user, seccomp profile, resource limits.
- The exact required restrictions are documented in docs/BROWSER_ENGINE.md.

## ADR-032 — Artifact security
**Status:** Accepted (user requirement E).
- **Treat as sensitive:** screenshots, downloads, traces and execution artifacts are customer data. They are stored only through `IArtifactStore` under organization-scoped keys, never served statically, never opened or executed.
- **Masking:**
  - Values filled as sensitive are never logged.
  - Their elements, password fields, card fields and declared selectors are masked in screenshots.
  - Screenshots are taken on failure only by default; tracing is off by default and, if enabled, its output is classified as sensitive.
- **Limit:** the platform does **not** assume every secret shown by a website can be detected automatically. Workflows must declare additional mask selectors, and this limitation is documented.

## ADR-033 — Reproducible verification image
**Status:** Accepted (user decision 3).
- **Recipe:** `deploy/verify/Dockerfile` builds `automataflow-verify:pw-<version>`:
  - Base: `mcr.microsoft.com/dotnet/sdk:10.0.404-noble`, pinned by digest `sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb` (Ubuntu 24.04, .NET SDK 10.0.404).
  - Chromium is installed with the driver bundled in the pinned Microsoft.Playwright package. The Playwright version is read from Directory.Packages.props, the single source of truth, and Chromium's exact build follows from that version.
  - Tests run as a non-root user.
- **Version lock:** the image records the Playwright and Chromium versions, and a test fails on mismatch.
- **Reproduction:** documented in docs/BROWSER_ENGINE.md.

## ADR-035 — Chromium sandbox via a derived seccomp profile, never --no-sandbox
**Status:** Accepted (spike C1, 2026-11-11). Under Docker's default seccomp profile the Chromium sandbox cannot start, because unprivileged user namespaces are denied. `deploy/verify/chromium-seccomp.json` is Docker's default profile (moby/profiles, Apache-2.0) plus one rule allowing `clone`/`unshare` without `CAP_SYS_ADMIN`. The browser then runs **with** its sandbox as a non-root user, and privileged operations stay denied. The verification container uses it, and hosted deployments must use it (Phase 7). `ChromiumSandbox=false` is accepted only in Development mode, with a warning, and the validator rejects it elsewhere.

## ADR-036 — Network control details proven by the spike
**Status:** Accepted (spike, 2026-11-11).
1. **Resolver rule:** Chromium gets `--host-resolver-rules="MAP * ~NOTFOUND, EXCLUDE 127.0.0.1"`. The exclusion is required because the rule also matches IP literals, including the proxy address. Chromium can therefore reach only the local egress proxy.
2. **Launch-level proxy:** the browser's launch-level proxy is the egress proxy itself, without credentials. A context created without per-session credentials gets 407 for every request, so it fails closed. A dead proxy gives `ERR_PROXY_CONNECTION_FAILED`, never a direct connection. Loopback is proxied (`<-loopback>`).
3. **Route guard is not authoritative:** route interception does not see redirect hops, so the egress proxy is authoritative. Redirect chains are additionally inspected through `Request.RedirectedFrom`, and a navigation whose chain contains a denied hop fails with `NavigationBlocked`.
4. **IPv4-mapped IPv6:** the proxy unwraps IPv4-mapped IPv6 hosts (Chromium sends `[::ffff:7f00:1]`).

## ADR-034 — Estimates are indicative; quality first
**Status:** Accepted (user decision, 2026-11-11). The 16-day target is dropped. Phase estimates are planning aids, not deadlines; quality, reliability and security take priority. ROADMAP shows revised estimates.

## Phase 3 formal approval
**Status:** Approved by the user on 2026-11-12, on the basis of the reported verification: 508 passing tests across two consecutive complete green runs, commit `fd942c2`.

## ADR-037 — Pure host and IP policy types move to Application; runtime enforcement stays in Automation
**Status:** Accepted (user decision D1, 2026-11-12; implemented in Phase 4).
- **What moves** to `AutomataFlow.Application.Network`: `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy` and `NormalizedHost`. They contain definitions and parsers only, with no I/O and no packages. This lets publish-time validation (Api, via Application) use the exact runtime parser.
- **What stays in Automation:** the request guard, browser guards, egress proxy, fail-closed gating and Chromium hardening. Phase 3 security is preserved.
- **Gate:** all Phase 3 tests must pass unchanged, apart from namespaces.

## ADR-038 — Per-step timeouts never terminate the browser session
**Status:** Accepted (D2). A step's timeout is passed to Playwright as that call's own timeout, so the session survives and `onError: continue` works. Only caller cancellation, the workflow timeout and the session deadline close the session. Also accepted:
- **Uploads:** content-only (`IExecutionFileProvider`, in-memory payload, never a filesystem path).
- **Safe page checks:** `CountAsync`, `IsVisibleAsync` and `CurrentUrl`, which are immediate and never throw for missing elements.

## ADR-039 — CaptureResult is the terminal outcome mechanism
**Status:** Accepted (D3).
- **Exactly one terminal outcome** per run, set once through a single transition and never overwritten.
- **Terminal `captureResult`** must be the last step of its sequence.
- **Outcomes per section:** setup `failed`; reconcile `reconciled` / `notFound` / `needsReview` (mandatory on every path); steps `succeeded` / `failed`.
- **Result fields** are written at most once per path. This is enforced statically, with a run-time `ResultOverwrite` backstop.
- **Reconcile fields** are discarded on `notFound`.
- **Transient failure after a side effect** is reported as `Indeterminate`, with the declared code kept.
- **Late cancellation:** a cancellation that arrives after the terminal outcome has been set cannot change it; it only shortens how long session cleanup waits.
- **Accepted consequence:** after a side effect the engine cannot distinguish "failed before saving" from "saved but the response was lost". Failures that happened before anything was saved (e.g. DemoCrm `+fail-N`) therefore also cost one reconciliation pass before the next attempt. That is cheaper than a duplicate business record.
- **Full table:** docs/PHASE4_PLAN.md section 16.

## ADR-040 — Workflow sections: setup, reconcile, steps
**Status:** Accepted (D4). The order is `setup` → `reconcile` (only when the request asks for it; Phase 5 decides when) → `steps`. Neither `setup` nor `reconcile` may contain side-effect steps.

## ADR-041 — Workflow version lifecycle and deterministic content hashing
**Status:** Accepted (D5).
- **Lifecycle:** Draft → Published → Archived, with no way back. Published and Archived definitions are immutable.
- **Canonical form** (inspired by RFC 8785):
  - UTF-8;
  - properties in ordinal order;
  - no whitespace;
  - minimal string escaping;
  - integer-only numbers;
  - duplicate keys rejected before canonicalization.
- **Hash:** SHA-256, lowercase hex, recomputed before execution (`WorkflowTampered` on mismatch). Golden-value tests pin it.

## ADR-042 — No loops and no regular expressions in workflow schemaVersion 1
**Status:** Accepted (D6). Both are rejected as unknown constructs. Adding either requires a new schemaVersion and an ADR.

## ADR-043 — Typed workflow parameters cannot bypass host restrictions
**Status:** Accepted (D7).
- **Types:** `url` parameters are origin-only, https-only outside Development and never private IP literals outside Development. They are usable only as the whole prefix of a navigate URL, and only when the same parameter is listed in `allowedHosts`. `string` parameters can never appear in a URL scheme or authority.
- **Source:** values come from job configuration, never from records.
- **Enforcement:** bound URL parameters become `EgressPolicy` rules, and all Phase 3 runtime enforcement then applies unchanged.

## ADR-044 — Sink rules: secrets versus sensitive values
**Status:** Accepted (user decision during the Phase 4 plan review, 2026-11-12).
- **Terminology:**
  - A *secret* is a value from `ISecretProvider` (`{{secret.x}}`).
  - A *sensitive input* is a record field marked `sensitive: true`.
  - A *sensitive extract* is an `extract` output marked `sensitive: true`.
  - A *sensitive value* is any of these, or any resolved template containing one (taint propagation).
- **Secrets** are accepted **only** in `fill.value`. They are rejected in every other sink, including `select`, condition operands and selectors, because those would allow probing a secret through page behaviour.
- **Sensitive inputs and extracts** are accepted in `fill.value`, `select` and condition operands. They are rejected in `navigate.url`, `captureResult.fields`, selector values, `screenshot.label` and `upload.file`.
- **Where enforced:** by the validator, and again at run time.
- **Logs** never contain sensitive values. Execution events contain no input or output values at all.
- **Screenshot masking** covers sensitive fills, the Phase 3 built-ins and declared `maskSelectors`. It does not claim to detect every secret a website displays.
- **Full matrix:** docs/PHASE4_PLAN.md section 5.

---

## Resolved from the approval round (2026-11-11)
1. SDK: selected from inspection, not blindly pinned (ADR-004). 2. PostgreSQL: Docker `postgres:18`, port 5433, volume (ADR-013). 3. Git initialized; commit after each verified phase; no secrets or generated artifacts. 4. Identity + JWT (ADR-010). 5. Hand-written validators (ADR-016). 6. Razor Pages Demo CRM (ADR-011). 7. Folder layout `src/ tests/ web/ deploy/ docs/`, React in `web/automataflow-web`. 8. 16-day target, 16–20 realistic; quality over deadline. 9. Frontend libraries deferred (ADR-019). 10. Playwright Chromium download approved for Phase 3.

## Pending approval
None at this time.
