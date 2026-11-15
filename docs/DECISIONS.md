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
**Status:** Accepted (user decision D1, 2026-11-12). Implemented in Phase 4 step 1 (2026-11-15): pure move, all 508 Phase 3 tests green, placement enforced by `NetworkPolicyPlacementTests`.
- **What moves** to `AutomataFlow.Application.Network`: `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy` and `NormalizedHost`. They contain definitions and parsers only, with no I/O and no packages. This lets publish-time validation (Api, via Application) use the exact runtime parser.
- **What stays in Automation:** the request guard, browser guards, egress proxy, fail-closed gating and Chromium hardening. Phase 3 security is preserved.
- **Gate:** all Phase 3 tests must pass unchanged, apart from namespaces.

## ADR-038 — Per-step timeouts never terminate the browser session
**Status:** Accepted (D2); amended by ADR-053 (review round 4; approved with Phase 4 on 2026-11-15).
- **Timeouts:** a step's timeout is passed to Playwright through `OperationOptions` as **one total deadline** for the whole step, including composite operations such as `SubmitAsync` (click plus load wait). The session survives. Only caller cancellation, the workflow timeout and the session deadline close the session.
- **Side-effect steps:** a timeout of a side-effect step ends the attempt `Indeterminate` (ADR-048, ADR-051).
- **`onError: continue`:** restricted by ADR-053.
- **Also accepted:**
  - **Uploads:** content-only (`IExecutionFileProvider`, in-memory payload, never a filesystem path).
  - **Safe page checks:** `CountAsync`, `IsVisibleAsync`, `CurrentUrl` and the bounded `QueryRowsAsync`, which never throw for missing elements.

## ADR-039 — Exactly one terminal outcome per attempt
**Status:** Accepted (D3); amended in review rounds 2 and 4 (approved with Phase 4 on 2026-11-15).
- **Exactly one terminal outcome** per attempt, set once through a single transition and never overwritten. Cleanup and journal-completion failures are diagnostics only.
- **Refusals are not attempts.** A call refused before admission is never journaled and has no terminal outcome of its own (ADR-049).
- **Explicit finalization:**
  - every path through `steps` ends in a terminal `captureResult`, which must be the last step of its sequence;
  - the `reconcile` section ends in exactly one top-level `reconcileMatch` (ADR-052) and contains no `captureResult`;
  - there is no implicit success.
- **Outcomes per section:** setup `captureResult` `failed` only; steps `succeeded` / `failed`; reconcile findings come only from `reconcileMatch`.
- **Result fields** are written at most once per path. This is enforced statically, with a run-time `ResultOverwrite` backstop.
- **After a side effect** only a declared permanent business outcome gives `Failed`; everything else gives `Indeterminate` (ADR-051).
- **Late cancellation:** a cancellation that arrives after the terminal outcome has been set cannot change it.
- **Accepted consequence:** after a side effect the engine cannot distinguish "failed before saving" from "saved but the response was lost". Without target-enforced idempotency or authoritative reconciliation evidence, such a record (even DemoCrm `+fail-N`, where nothing was stored) goes to `NeedsReview` instead of being repeated. That is cheaper than a duplicate business record.
- **Full table:** docs/PHASE4_PLAN.md section 16 D3.

## ADR-040 — Workflow sections: setup, then reconcile OR steps
**Status:** Accepted (D4); amended in review round 2 (no fall-through).
- **Order:** `setup` runs first in every attempt. Then **either** `reconcile` (a Reconcile attempt) **or** `steps` (a Normal attempt) runs, **never both in one attempt**.
- **No fall-through:** a reconcile attempt stops after its finding, whatever the finding is; it never continues into main steps.
- **Who decides:** Phase 5 decides when to run a reconcile attempt, using `RepeatSafetyEvaluator` (ADR-045).
- **Side effects:** neither `setup` nor `reconcile` may contain side-effect steps.
- This replaces the earlier wording "setup → reconcile → steps", which implied fall-through.

## ADR-041 — Workflow version lifecycle and deterministic content hashing
**Status:** Accepted (D5).
- **Lifecycle:** Draft → Published → Archived, with no way back. Published and Archived definitions are immutable.
- **Canonical form** (inspired by RFC 8785):
  - UTF-8 without BOM;
  - properties in ordinal UTF-16 order;
  - no insignificant whitespace;
  - minimal string escaping;
  - integer-only numbers;
  - duplicate keys rejected before canonicalization.
- **Hash:**
  - format `sha256:` + lowercase hex;
  - computed and stored at publish;
  - verified on load (`ValidatedWorkflow.Create`) and again at execution start (`WorkflowTampered` on mismatch);
  - recorded in every attempt snapshot (ADR-050);
  - golden-value tests pin it.

## ADR-042 — No loops and no regular expressions in workflow schemaVersion 1
**Status:** Accepted (D6). Both are rejected as unknown constructs. Adding either requires a new schemaVersion and an ADR.

## ADR-043 — Typed workflow parameters cannot bypass host restrictions
**Status:** Accepted (D7); amended in review round 2 (private network, ADR-047) and round 4 (canonicalization, ADR-046; approved with Phase 4 on 2026-11-15).
- **`url` parameters:**
  - origin-only, canonicalized at bind time (lowercase scheme, normalized host, default port and a single trailing `/` removed);
  - https-only outside Development and never private IP literals outside Development;
  - usable only as the whole prefix of a navigate URL, and only when the same parameter is listed in `allowedHosts`.
- **`string` parameters:** 1–200 characters; they can never appear in a URL scheme or authority.
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
- **Sensitive inputs and extracts** are accepted in `fill.value`, `select` and condition operands. They are rejected in `navigate.url`, `captureResult.fields`, `reconcileMatch.equals` and `reconcileMatch.fields`, selector values, `screenshot.label` and `upload.file`.
- **Where enforced:** by the validator, and again at run time.
- **Logs and journal entries** never contain sensitive values. Execution events contain no input or output values at all.
- **Screenshot masking** covers sensitive fills, sensitive-extract elements, the Phase 3 built-ins and declared `maskSelectors`. It does not claim to detect every secret a website displays.
- **Full matrix:** docs/PHASE4_PLAN.md section 5.

## Phase 4 review-round decisions (ADR-045 to ADR-060)
**Status of every ADR in this group:** accepted in direction by the user during review rounds 2–9 (2026-11-12 and 2026-11-15). **Final Phase 4 approval was granted by the user on 2026-11-15; all of them are binding.** ADR-054's scope (DemoCrm may change in Phase 4) was approved explicitly on 2026-11-15. The authoritative text is docs/PHASE4_PLAN.md section 16 (R1–R17; R16 and R17 amended in rounds 8 and 9, R14 in round 9).

## ADR-045 — Repeat authorization: deterministic evaluator and executor guard
- **No authorization from NotFound alone:** a reconcile `NotFound` finding never authorizes a repeated side effect by itself.
- **Pure evaluator:** `RepeatSafetyEvaluator` (Application, pure) evaluates an operation's journal history with **first-match precedence**:
  1. integrity, in flight, unfenced (`AwaitingFence`, ADR-056), empty, resolved;
  2. if an ambiguity is open: Found → reconcile needs review → permanent failure with open ambiguity → reconcile budget → ambiguity budget (`RepeatSafety:MaxAmbiguousAttempts`, default 3) → target-enforced idempotency → latest NotFound (advisory / key not sent / too early / expired / authoritative) → reconcile next → NeedsReview;
  3. otherwise: permanent failure, reconciled first, no side effect;
  4. a **mandatory default `NeedsReview`**.
- **Ambiguity first:** while an ambiguity is open, resolution and no-side-effect rules are never consulted.
- **`Allowed` needs evidence:** only `NoPriorAttempt`, `NoSideEffectStarted`, `TargetEnforcedIdempotency` or `AuthoritativeNotFound` can produce `Allowed`.
- **Executor guard:** the executor refuses a Normal attempt without `Allowed` before any browser session exists.
- **Rules table:** docs/PHASE4_PLAN.md section 10.5.

## ADR-046 — URL templates, canonical origins, encoding and glob semantics
- **Grammar:** URL templates follow a grammar with an ASCII literal character set; there are no literal backslashes, whitespace, dot segments or encoded `/`, `\` or `.`.
- **Placeholders:** allowed only in path segments and query values.
- **Origins:**
  - literal origins must be canonical;
  - `url` parameters are canonicalized at bind time;
  - the record never controls scheme, host or port.
- **Encoding:** values are encoded once per context. Dot-segment values, empty path values, control characters and lone surrogates are rejected.
- **Final URL:** validated on the exact `Uri.AbsoluteUri`, which must round-trip unchanged: same origin, segment-aware literal path prefix, no userinfo or fragment, ≤ 2,048 characters, matches `allowedHosts`.
- **Glob:** `urlMatches` / `wait.url` use a regex-free glob (`*`, `**`) identical to Playwright's for that subset.

## ADR-047 — Private-network access only from trusted server configuration
`WorkflowExecutionRequest` has no private-network flag. A rule may allow private-network destinations only in Development, and only when its origin exactly matches `Automation:Browser:DevelopmentPrivateNetworkOrigins`. That server configuration is validated at startup and rejected outside Development. No workflow, job, parameter, record, request or customer can grant private-network access.

## ADR-048 — Additional workflow validation
- **Side-effect steps:** `timeoutMs` ≥ 5,000; a timeout is ambiguous (`Indeterminate`).
- **Sensitive extracts:** registered for redaction and masking immediately; never in results, step records, journal entries or logs; `ExtractTooLong` instead of silent truncation.
- **Results:** at most 50 fields, 2,000 characters each, 32 KB total.
- **Uploads:**
  - content-only and organization-scoped;
  - size, extension subset, content type and signature checks;
  - all checks run before the side-effect checkpoint.
- **Nested conditions:** validated recursively with the full section rules.
- **Hash verification:** at publish, on load and at execution start.

## ADR-049 — Attempt journal, single in-flight attempt and durable side-effect checkpoint
- **Port:** `IAttemptJournal` (Application) is the **only source of attempt history**. Phase 5 implements it in PostgreSQL; Phase 4 tests use an in-memory implementation that exists only in test projects.
- **Reservation:** `ReserveAsync` atomically marks lease-expired Started attempts Abandoned, then refuses a new attempt for the same (organization, operation) while another is Reserved, Started or unfenced (ADR-056). All later calls are fenced by the reservation's lease, except a late `CompleteAsync`, which only adds outcome and fence information.
- **Checkpoint:**
  - before invoking any side-effect handler, the executor awaits `SideEffectStartingAsync`, which returns only after a durable write;
  - if it fails or is fenced, the handler is not invoked and the attempt ends `Failed`/`CheckpointFailed`;
  - if the write happened anyway (acknowledgement lost), the journal's checkpoint makes the attempt ambiguous.
- **Abandoned attempts:** judged by their checkpoint. No checkpoint means no side effect started (provable); a checkpoint means ambiguous and unfenced until fenced (ADR-056); unreadable state means `Unknown`, treated as started and unfenced.
- **Refusals:** released, never journaled, and they cannot influence later decisions.

## ADR-050 — Evidence bound to operation and definition snapshot; one clock; evidence age
- **Binding:**
  - every attempt summary carries its `OperationId` and the snapshot of the version that actually ran: hash, version, timeout, `TargetEnforced`, `OperationIdSent`, reconcile policy;
  - reconciliation evidence carries the policy snapshot of the version that ran the lookup;
  - a history entry for another operation makes the history invalid (`NeedsReview`).
- **No retroactive claims:** target-enforced idempotency is judged on each ambiguous attempt's own snapshot plus the current version. A newer version can never vouch for an older attempt. An operation-id lookup counts only if the ambiguous attempt sent the key.
- **Clock:** all attempt timestamps and the evaluator's `now` come from the journal clock. `ObservedAt` is recorded immediately before the reconcile section.
- **Authoritative NotFound authorizes a repeat only if** the ambiguous attempt is fenced (ADR-056) and:
  - `ObservedAt ≥ DispatchHorizon + maxCommitLatencySeconds + ClockSkewAllowance` (default 5 s; ADR-057, ADR-060), and
  - `now − ObservedAt ≤ maxEvidenceAgeSeconds` (default 300 s).
- **No assumed timeouts:** no time is derived from an assumed process timeout (round 5 removed the former `StartedAt + TimeoutSeconds` rule).

## ADR-051 — After a side effect, only declared permanent business outcomes are Failed
- **Failed:** once a Normal attempt has written its first side-effect checkpoint, `Failed` is possible only through a terminal `captureResult` `failed` whose resolved transience is false.
- **Succeeded:** only through a terminal `captureResult` `succeeded`.
- **Everything else is `Indeterminate`,** keeping the original code. That covers browser, engine, technical, policy, timeout and cancellation errors, transient business outcomes, and run-time failures in the terminal `captureResult`. A technical error says nothing about whether the target committed.

## ADR-052 — Exact-match reconciliation with reconcileMatch
- **Structure:** the `reconcile` section ends with exactly one top-level `reconcileMatch` step (the 13th action type).
- **Keys:**
  - `reconciliation.matchKey` is `operationId` (`equals` = `{{operation.id}}`, and the workflow must send the operation id before every side effect) or `businessKey` (exactly one required, non-sensitive record input, attested unique);
  - rows must be a superset of exact matches (author attestation).
- **Findings:**
  - exactly one exact match: `ReconciledFound`;
  - zero rows with the empty indicator visible, or rows but no exact match: `ReconciledNotFound`;
  - zero rows without the indicator: `Failed`/`LookupInconclusive` (transient);
  - several exact matches, or more rows than `maxRows`: `NeedsReview`.
- **Failures:**
  - permanent reconcile errors lead to `NeedsReview`;
  - transient failures, cancellations and abandoned reconciles count toward `RepeatSafety:MaxReconcileFailures` (default 3), after which the decision is `NeedsReview`;
  - so a reconcile can never loop indefinitely.
- **Superseded:** this replaces author-written `reconciled` / `notFound` / `needsReview` `captureResult` outcomes.

## ADR-053 — Total step deadlines, restricted continue, explicit side effects
- **Deadline:** `timeoutMs` is one total deadline per step and must not exceed `timeoutSeconds × 1000`.
- **`onError: continue`:** allowed only on `wait`, `extract`, `screenshot`, `fill`, `select` and `condition`. Navigating or transferring actions can still complete after their timeout, so continuing past them is forbidden.
- **`sideEffect`:** must be declared explicitly on every `click` and `upload` in `steps`; there is a warning for navigating clicks declared `false`.
- **Late commits:** a server-side commit after a client timeout is covered by `Indeterminate` plus the authoritative commit-latency wait measured from the dispatch horizon (ADR-057, ADR-060).

## ADR-054 — DemoCrm test-fixture extensions in Phase 4
**Status:** Scope approved by the user (2026-11-15); design approved with Phase 4 on 2026-11-15.
- **Extensions:**
  - exact `OperationId` search with `data-operation-id` on result rows;
  - a registration POST counter (`registrationSubmissions`) in the token-protected `GET /demo/state`, zeroed by `POST /demo/reset`;
  - a `+late-commit` scenario that commits after the client disconnects.
- **Boundaries:** DemoCrm stays independent (ADR-027). No production project may reference it, directly or transitively. The control token rules (ADR-028) are unchanged.
- **Test rule:** every duplicate-risk test asserts the POST count, so a resubmission is detected even when DemoCrm rejects it as a duplicate.

## ADR-055 — Centralized workflow limits with schema parity
- **Single source:** all workflow limits are constants in `WorkflowLimits` (Application). The validator, run-time checks and documentation use them.
- **Parity:** tests compare every numeric bound and enum in `docs/workflow-schema-v1.json` with the code.
- **Server limits:** `SessionSeconds`, `Uploads.MaxBytes`, `RepeatSafety:ClockSkewSeconds`, `RepeatSafety:MaxReconcileFailures`, `RepeatSafety:MaxAmbiguousAttempts` and the journal's `LeaseSafetyMarginSeconds` are validated at startup. A workflow can never exceed them; for example, `timeoutSeconds` above `SessionSeconds` is refused at bind time.

## ADR-056 — Fencing: no takeover while a previous attempt could still dispatch
- **The problem:** a Worker that wrote its side-effect checkpoint and then froze (or lost its lease while alive) can still dispatch the click when it resumes.
  - Its egress proxy is in-process and resumes with it.
  - No in-process check can close the check-then-act window.
  - Third-party targets cannot validate fencing tokens.
- **Fence state:** such an attempt is **unfenced** until a fence is established, by any of:
  - a **completed** egress revocation barrier recorded at completion (ADR-060; including a late completion after lease loss);
  - abandonment without a checkpoint;
  - positively confirmed process death of the Worker's whole containment unit (Phase 5; ADR-059);
  - confirmed isolation termination, or the revocation report of an out-of-process lease-bound egress proxy (Phase 7);
  - an audited human confirmation with verified evidence (ADR-059).
- **Fence time:** `FencedAt` is always the journal commit time of a validated confirmation, never a requested, predicted or lease-derived time.
- **While unfenced:**
  - the journal refuses every new reservation for the operation (Normal and Reconcile);
  - the evaluator returns `AwaitingFence` before any rule that can allow anything;
  - lease expiry alone never permits takeover after a checkpoint.
- **Fence time:** delays and evidence windows count from the dispatch horizon derived from the proven fence (ADR-060).
- **Checkpoint acceptance:** an atomic conditional write with fencing token and lease margin (remaining lease ≥ step timeout + `LeaseSafetyMarginSeconds`), so a Worker that lost its lease before checkpointing never invokes the handler.
- **Guarantees by phase:**
  - Phase 4/5: at most the original attempt dispatches; no second attempt while it could still dispatch.
  - Phase 7: can also stop the stale dispatch itself.

## ADR-057 — Authoritative reconciliation requires a target commit-latency contract
- **Contract block:** `evidence: "authoritative"` requires `reconciliation.authority` with `maxCommitLatencySeconds` and a `contractReference` (part of the hashed definition).
- **What the cited contract must guarantee:** bounded commit latency, read-your-writes lookup, visibility of pending records, and a unique key with superset search.
- **What the bound covers (round 9):** `maxCommitLatencySeconds` must cover everything after the dispatch horizon (ADR-060): **local buffering below our socket layer, network transit, target processing and query visibility**. An author reasoning from upstream write admission must also include local queueing time. It is a trusted attestation, not a TCP guarantee.
- **Where it comes from:** the target operator (documentation, written statement, or the organization's own system configuration).
- **Not a contract:** observed delays, empty searches and measured response times. A generic website is never authoritative merely because a search returned nothing after a delay.
- **No contract means advisory:** without a contract the workflow must be `advisory`, which never authorizes an automatic repeat.
- **Hosted mode:** authoritative attestations are admin-only.
- **Replaces** the former `minDelaySeconds`. The wait is `DispatchHorizon + maxCommitLatencySeconds + skew` (ADR-060).

## ADR-058 — Attempt journal implementation boundary; production fails closed
- **Phase 4:** validates the journal and execution contracts with test implementations only (in-memory and fault-injecting journals, `IsDurable = false`) through a shared conformance suite. Phase 4 has no customer execution path.
- **Phase 5:** the PostgreSQL journal must pass the same suite plus crash-recovery (with a really killed Worker process), fencing and concurrency tests before any customer execution.
- **No real customer execution may rely on an in-memory or non-durable journal.**
- **Fail closed:** a production Worker fails at startup, in every environment and without any override, unless:
  - a durable, fencing journal is registered;
  - its lease length covers the longest side-effect timeout plus the margin;
  - the process-death fence supervisor is configured and its containment handshake succeeds (ADR-059).

  An unreachable journal refuses every attempt.

## ADR-059 — Process-death supervisor and fence-confirmation contract
- **Identity:**
  - every Worker registers an incarnation (GUID, host id, boot id, PID and start time, containment unit) before reserving anything;
  - every attempt is bound to it;
  - the containment unit (a Linux cgroup v2 unit, or a Windows Job Object created by the supervisor with breakaway disallowed) holds the Worker, its in-process egress proxy, the Playwright driver and every Chromium process;
  - membership is inherited and cannot be left without privileges the Worker lacks, so orphans remain inside.
- **Positive confirmation:** the supervisor (same host, outside the unit) kills the **whole unit**. It confirms only when both hold:
  - the kernel or job reports the unit **empty** (`cgroup.events populated 0` / `ActiveProcesses == 0`);
  - a **socket drain** over the incarnation's connection ledger, using OS connection identity, shows every upstream connection in a state where TCP originates nothing more. Linux destroys transmit-capable sockets by exact cookie in the dedicated namespace; Windows never terminates and fails closed on ambiguity (ADR-060).

  A changed host boot id is also a confirmation, because no kernel sockets survive a reboot.
  - **Not confirmations:** kill requests, signals, expired leases, stale heartbeats, missing or reused PIDs, the Worker alone being gone, and timeouts.
  - **Orphans:** children that survive their parent stay in the unit and are killed and proven gone with it.
- **Credentials and connections:**
  - in-process proxy credentials exist only in the Worker's memory and are never persisted or shared, so they are unusable once that process no longer exists;
  - sockets closed by process exit **linger in the background** (`socket(7)`) and may keep transmitting buffered data, which is why the socket drain is required in addition to "unit empty";
  - a frozen or partitioned Worker keeps them usable, which is why it stays unfenced.
- **Human confirmation:**
  - platform operators only (never tenants);
  - a closed list of methods: host powered off or destroyed, host rebooted, or `DrainVerified` (unit empty **plus** a supervisor drain report). "Container removed" or "unit empty" without a drain report is not accepted;
  - a mandatory evidence reference and personal attestation;
  - fully audited.
- **Timestamps:** `FencedAt` is the journal commit time of a validated confirmation. Kill-request time, lease expiry and observation times are diagnostics only.
- **Validation and scope:** the journal rejects evidence whose incarnation or unit does not match, whose method is not listed, or that lacks a successful drain report (`ProcessDeathConfirmed`, `DrainVerified`). A valid confirmation fences every unfenced attempt of that incarnation.
- **Failure:** without proof, attempts stay `AwaitingFence`, become `NeedsReview` after `FenceWaitSeconds`, and are never retried automatically. A late fence requires an explicit operator requeue.
- **Topology and operations:**
  - valid only when browser and proxy run inside the Worker's unit; other topologies need the Phase 7 fence sources;
  - Worker hosts are never restored or cloned from memory snapshots.
- **Phase split:** Phase 4 defines the contract and proves journal-side validation with a fake supervisor (F9–F15). Phase 5 implements the supervisor and must pass P1–P13 on real processes.
- **A fence is not a failure:** a fence means nothing *further* can be dispatched; already-dispatched requests may have committed. A fenced attempt stays ambiguous, and its side-effect state and outcome are unchanged. Only the repeat-safety rules resolve it (ADR-045), with the commit-latency contract (ADR-057) and reconciliation rules (ADR-052) unchanged. After process death, evidence counts from the dispatch horizon `FencedAt`, which is valid only because the fence includes the socket drain (ADR-060).

## ADR-060 — Egress revocation barrier, upstream write gate, socket drain and dispatch horizon
**Amended in review rounds 8 and 9.**
- **Findings:**
  - the Phase 3 `EgressSession.Dispose` is not a barrier: it has an admission race, untracked upstream sockets, already-read requests and tunnel buffers still written upstream, graceful close, no wait for handlers, and an unsynchronized flag;
  - the round-7 "check token, then write" is not atomic: a write that passed its check can execute after revocation began.
- **Write gate:**
  - every upstream connection creation and payload send happens inside an admission from `UpstreamWriteGate.TryEnter`, taken under the **same lock** as session admission;
  - only `UpstreamChannel` may send upstream (source-scanned);
  - admitted sends are not cancelled, are at most 16 KB, and follow the connection's entry in the connection ledger.
- **Barrier `EgressSession.RevokeAsync`:**
  - **B1 (Lr):** under the lock, set revoked, so no admission or write can start;
  - **B2:** wait for every write admitted before Lr;
  - **B3 (Ta):** abortive close (linger 0) of all upstream sockets and close of all browser-side sockets, including keep-alive CONNECT tunnels;
  - **B4:** wait for all handlers;
  - **B5:** `Completed`, or `TimedOut` after `Egress:RevocationTimeoutSeconds` (default 10).

  No send syscall begins after Lr, and none is executing after Ta.
- **Upstream sockets are never closed gracefully,** including at a connection's normal end (Step 2 follow-up, defect D1). A graceful handler close could pre-empt B3 and let queued data drain after Ta.
- **Fencing:**
  - `EgressRevoked` and `ExecutionFenced` are true only for `Completed`;
  - the barrier runs before the browser context is closed, and `CompleteAsync` only after it;
  - `TimedOut` (e.g. a send blocked on a full buffer) leaves the attempt unfenced;
  - `Dispose()` never fences;
  - the Phase 7 proxy reports revocation only after the same barrier.
- **OS facts** (verified against OS documentation on 2026-11-15):
  - Linux `TCP_USER_TIMEOUT` (milliseconds) only bounds *unacknowledged* data or zero-window stalls, and does not change retransmission;
  - Windows `TCP_MAXRT` (seconds; -1 = infinite) is documented for retransmission only;
  - neither bounds a slowly acknowledging peer or a socket lingering after `_exit` ("always lingers in the background", `socket(7)`);
  - Linux zero-linger RST is **not** documented in `socket(7)` and is verified empirically (V7); Windows documents the abortive close ("unsent data is lost").
- **Consequences of the OS facts:**
  - the round-7 `FencedAt + UpstreamMaxRetransmitSeconds` horizon and the 256 KB send-buffer cap are **withdrawn**;
  - `Egress:UpstreamUserTimeoutSeconds` (default 30) remains as fail-fast only, never as a guarantee.
- **Connection ledger and identity (round 9):** before any payload, every upstream connection is fsync-appended to a per-incarnation ledger with its tuple **and OS identity**:
  - Linux: socket cookie `SO_COOKIE`;
  - Windows: `liCreateTimestamp` of the owner-module TCP row, read by matching tuple and own PID, exactly one row required.

  A connection whose identity cannot be read is aborted unused.
- **Socket drain for fences without a barrier:** `ProcessDeathConfirmed` additionally requires a drain.
  - **Linux:**
    - the incarnation runs in a **dedicated network namespace** (handshake-verified; the supervisor holds a handle), so after "unit empty" every socket in it is an orphan of that incarnation and no replacement from another process can appear;
    - all TCP sockets in the namespace are enumerated completely (`sock_diag`; interrupted or failed dumps are retried, then fail closed);
    - transmit-capable sockets are destroyed by `SOCK_DESTROY` carrying the **exact cookie**, so another socket is never destroyed (kernel behaviour verified by P11b);
    - drained only when every socket is safe.
  - **Windows:**
    - no per-process namespace exists and `SetTcpEntry` targets only by tuple, so the supervisor **never terminates any connection**;
    - each ledger entry must match by tuple **and** `liCreateTimestamp`;
    - a same-tuple row with a different or zero timestamp, or duplicate rows, is **ambiguous → no fence**;
    - lingering connections are polled until gone or safe, otherwise `NeedsReview`.

    The round-8 IPv4-only restriction is withdrawn.
  - **Fail closed:** any of these means no fence: unreadable or corrupt ledger (except a torn final record, which carried no payload by construction); lost or shared namespace; incomplete enumeration; `SOCK_DESTROY` unsupported (`EOPNOTSUPP`), denied (`EPERM`) or silently skipped; ambiguity; partial drain; supervisor restart mid-drain.
  - **Phase 7:** `IsolationTerminated` is not accepted until it specifies an equivalent proof.
- **Three events (round 9):**
  - **E1, write completion:** every admitted send returned; proven by B1/B2, or by "unit empty" after a process death;
  - **E2, connection destruction:** TCP originates no further segment or retransmission; proven by B3 (V7) or by the drain;
  - **E3, physical departure** of segments already handed below the socket layer (queueing, driver/NIC, veth/bridge/NAT, hypervisor): **not proven and never claimed**. An absent or terminal TCP entry says nothing about E3.
- **Dispatch horizon:** H = `FencedAt` (`AbandonedAt` for `NoCheckpoint`), recorded only after E1 and E2 are proven. It is a **conservative upper bound** on every write admission, every write completion and the end of TCP-originated transmission, **not** a physical-departure time.
- **Why it is safe:**
  - all bytes of every request were admitted, and transmitted by our TCP if at all, before H;
  - the **trusted** latency attestation (ADR-057) covers local below-socket buffering, transit, target processing and query visibility after H;
  - so an authoritative `NotFound` observed at ≥ H + L + skew cannot be followed by that request committing;
  - a wrong attestation (e.g. a paused or suspended host, or transit beyond L) breaks this. That is a documented limitation, Worker hosts must not be paused, and H4/P12 demonstrate the effect.
- **Not claimed:** E3; and that a dispatched side effect failed. The reconciliation rules are unchanged.
- **Tests:**
  - Phase 4: V1–V12 (V12 shows that socket destruction is not delivery), write gate WG1–WG6 (including deterministic pauses between the final admission check and the send), horizon races H1–H4 (including a negative control);
  - Phase 5: P7–P13 (lingering sockets, trickling peer, destroy capability, ledger problems, connection identity and port reuse P11a–P11f, below-socket queueing with `netem`, fail-closed matrix).

## ADR-061 — Workflow definition identity, loading and diagnostics (Phase 4 step 3 and its audit follow-up)
**Status:** Accepted (2026-11-15; implements ADR-041 and ADR-042; follow-up to the read-only audit of `8392cfd`).
- **Two entry points, no third:**
  - `ValidatedWorkflow.CreateForPublication(json)` validates a new definition and **computes** its hash;
  - `ValidatedWorkflow.Load(json, recordedHash)` is the only way to use a stored definition. The recorded hash is mandatory; the definition is re-parsed and re-validated, the hash recomputed and a mismatch refused (`WorkflowTampered`).
  - At execution start, `VerifyAgainst(recordedHash)` compares the canonical bytes with the hash recorded **outside** the object (the pinned version). `VerifyIntegrity()` is only a self-consistency check.
- **What the hash identifies:** the canonical form of the definition **as written**, after strict parsing. Formatting, property order and escaping do not change it; an explicit default (`"timeoutSeconds": 120`) is a different definition from an omitted one.
- **Frozen defaults:** because omitted properties take their meaning from code, the schemaVersion 1 defaults live in `SchemaV1Defaults`, separate from `WorkflowLimits`, pinned by literal-value tests. Changing a default requires a new schemaVersion.
- **Domain:** `WorkflowVersion` checks the hash **format** only (one definition, `DefinitionHashFormat`, shared with Application), that the declared schema version equals the definition's top-level `schemaVersion`, and that lifecycle timestamps never go backwards. A well-formed hash is never treated as proof; trust comes only from `Load`.
- **Parser limits** (in `WorkflowLimits`, ADR-055): JSON depth 32; any string 2,048 chars; any property name 64 chars; at most 100 reported errors (one extra `too-many-errors` entry marks truncation).
- **Linear cost:** JSON paths are kept as segment stacks and formatted only when an error is reported, so no per-value allocation grows with nesting depth (worst-case input is tested with an allocation bound).
- **Safe diagnostics:** property names that are not plain identifiers are written as `$['…']` with every character outside printable ASCII, quotes and backslashes escaped as `\uXXXX`. Paths and messages never contain control characters and are unambiguous.
- **Loops and regular expressions (ADR-042):** rejected as **constructs**, i.e. as property names in structural positions, matched case-insensitively. Keys of author-named data maps (`captureResult.fields`, `reconcileMatch.fields`) are data and may use any name; values inside those maps are still checked. From step 5, closed per-action config schemas reject every unknown structural key.
- **Enum names:** C# member names avoid type names (`Text`, `WholeNumber`, `DecimalNumber`); the JSON spelling is unchanged and defined once in `WorkflowJsonNames`.

## ADR-062 — Templates, URLs, glob, binding and records (Phase 4 step 4)
**Status:** Accepted (2026-11-15; implements ADR-043, ADR-044, ADR-046 and ADR-047).
- **Templates** compile once into literal and placeholder segments (`Template`); a static `TemplateChecker` checks references (declared input/parameter/secret; step exists, dominates and declares the output) and the §5.1 sink matrix, and computes taint. `TemplateResolver` repeats the sink check at run time (`SinkViolation`, a backstop that validation makes unreachable). `ResolvedValue.ToString()` never prints the value.
- **Parameters:** a `url` parameter is never an ordinary template value; it is legal only as the whole origin of a URL template or as an `allowedHosts` entry. `string` parameters are legal in every value sink except `upload.file`.
- **Allowed hosts at authoring time:** a canonical literal **https** origin (wildcards for subdomains only), never a private or reserved IP literal (including documentation ranges), or exactly `{{params.<url parameter>}}`. Development-only `http` and private origins can therefore be reached only through a url parameter **and** the trusted server list.
- **Binding** (`WorkflowBinder`): exactly the declared parameters (missing or unknown ones are refused); url parameters canonicalized, `https` only and no private IP literal outside Development; string parameters 1–200 chars without control characters; `timeoutSeconds` ≤ server `SessionSeconds`; private-network rules only in Development and only for an origin exactly equal to a trusted `DevelopmentPrivateNetworkOrigins` entry. Errors never contain values.
- **URL templates:** the §6.1 grammar; additionally an authoring-time round trip with sample values rejects literals that .NET URI parsing would rewrite, and an empty path renders as the root `/` (what URI parsing and browsers produce).
- **Glob (clarifies §8.2/R2):** the stated goal is identical semantics to Playwright's glob. In Playwright, `**` is deep only as a whole path element, and `**/` matches **zero or more whole segments** (so `/a/**/b` matches `/a/b`); any other `**` silently degrades to `*`. Our matcher reproduces exactly this and **rejects** non-segment `**`, runs of three or more `*`, and default ports in absolute patterns, so the two can never disagree. `*` is not domain-aware (`https://*.crm.test` matches `a.b.crm.test`). Matching is a dynamic program, O(tokens × length).
- **Records** (`RecordValidator`): fields that are not declared inputs are rejected; types are checked structurally (`email`, 64-bit `integer` in canonical form, invariant `decimal` without exponent, `true`/`false`, ISO `yyyy-MM-dd` date, `file` reference without surrounding whitespace); values never appear in errors.
- **Split with step 5:** step 4 delivers these components and wires `allowedHosts` and `maskSelectors`; step 5 applies them to every action config, including dominance across sections and condition branches.

## ADR-063 — Action catalog and workflow semantic rules (Phase 4 step 5)
**Status:** Accepted (2026-11-15; implements §2, §8.1, §9, §10.1, §10.3 and §16 D3/D4/D6, R4/R5/R9/R10).
- **Descriptors parse the hashed tree:** each `ActionDescriptor` parses the strictly parsed, immutable `JObject` (not a separate `JsonElement`), so validation, typing and hashing see the same document. One descriptor per `ActionType`; one sealed `ActionConfig` per action. Automation handlers and the `ActionRegistry` 1:1 check belong to step 7 (no Automation code in step 5).
- **Closed config schemas:** every config object rejects unknown keys; loop/regex names are `forbidden-construct` in structural positions (replacing the interim key scan of ADR-061). Shapes not fixed by Appendix A: `wait` = `{for: visible|hidden|attached|detached, selector}` | `{for: load}` | `{url}`; `select` = exactly one of `value`/`label` (templates) or `index` (0..1000); `elementCountAtLeast` = `{selector, count 1..1000}`; `isEmpty` = template string; value predicates = `{left, right, ignoreCase}`; `screenshot.label` 1..100 chars; attribute names `[A-Za-z_][A-Za-z0-9_.:-]*` ≤ 100; result field names `[A-Za-z][A-Za-z0-9_]*` ≤ 64.
- **captureResult:** `fields` optional (≤ 50), `outcome` optional, at least one present. `succeeded` takes no `code`/`transient`/`transientWhen`; `failed` requires `code` (`[A-Z][A-Za-z0-9]*`), and `transient` (frozen default `false`, i.e. a declared permanent business outcome) and `transientWhen` are mutually exclusive.
- **Frozen default step timeouts** (`SchemaV1Defaults`): navigate 30 s, click 15 s, fill/select/extract/condition 10 s, wait 30 s, screenshot 15 s, upload 30 s, download 60 s, delay 31 s, captureResult 5 s, reconcileMatch 30 s. The side-effect minimum (5 s) applies to the effective timeout; a delay must be shorter than its step timeout.
- **Side effects:** `sideEffect` is mandatory on click/upload in `steps` (including branches), may be absent or `false` in setup/reconcile, and is forbidden (even `false`) on other actions. Warning for a navigating click declared `false`.
- **`{{operation.id}}` is "sent"** only by a `fill`/`select` whose value is exactly that placeholder **and** whose `onError` is `fail` (a continued failure would not have sent it), dominating the side-effect step. `TargetEnforced` and `OperationIdSent` are computed once into `ValidatedWorkflow.Safety` (with `HasSideEffects`, `RequiresDownloads`).
- **Finalization:** every path through `steps` ends in a terminal `captureResult`; any step after a step that always ends the attempt (a terminal capture, or a condition whose both branches end it) is rejected as unreachable, in every section and branch. Setup may end only as `failed`; non-terminal setup captures are allowed.
- **Result fields:** written at most once on any path (setup fields count on both the reconcile and the steps path; a field written in either branch counts after the condition); at most 50 distinct names per workflow (conservative).
- **Dominance:** setup outputs are visible to reconcile and steps; branch outputs only later in the same branch; a condition's own `result` and every step's `failed` after it; steps outputs are never visible in reconcile.
- **Uploads:** executable and script extensions are rejected at authoring; the server extension list (default: the content-sniffable `.pdf .png .jpg .jpeg .docx .xlsx .csv .txt`) and `maxBytes` ≤ server limit are checked at binding.
- **Conditions:** element text is trimmed before `textEquals`/`textContains`; template operands are compared untrimmed; a missing element makes text/attribute predicates false; an unresolvable operand or selector value is an evaluation error, never `false`. The evaluator is pure Application code over the `IPageProbe` port.
- **Reconcile:** `reconcileMatch` is the single last top-level step of `reconcile`; a row key's child selector may not be XPath (it can escape the row).
---

## Resolved from the approval round (2026-11-11)
1. SDK: selected from inspection, not blindly pinned (ADR-004). 2. PostgreSQL: Docker `postgres:18`, port 5433, volume (ADR-013). 3. Git initialized; commit after each verified phase; no secrets or generated artifacts. 4. Identity + JWT (ADR-010). 5. Hand-written validators (ADR-016). 6. Razor Pages Demo CRM (ADR-011). 7. Folder layout `src/ tests/ web/ deploy/ docs/`, React in `web/automataflow-web`. 8. 16-day target, 16–20 realistic; quality over deadline. 9. Frontend libraries deferred (ADR-019). 10. Playwright Chromium download approved for Phase 3.

## Phase 4 final approval
**Status:** Approved by the user on 2026-11-15, after review round 9 of docs/PHASE4_PLAN.md.
- **What becomes binding:** ADR-045 to ADR-060 (as amended through round 9) and the round-4 amendments of ADR-038, ADR-039, ADR-043 and ADR-044.
- **Implementation:** authorized; it proceeds sequentially by the plan's steps, each one reviewed before the next.
- **Verification:** specification approval does not waive any implementation or platform-specific verification. A safety guarantee is claimed as implemented only once code and tests demonstrate it.

## Pending approval
None at this time. On 2026-11-15 the user authorized the Step 3 audit follow-up and Phase 4 steps 4, 5 and 6 to be completed consecutively, with one consolidated report after step 6.
