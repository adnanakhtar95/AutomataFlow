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

**Amended by ADR-075 (2026-11-19):**
- **Worker containers (production) and the step 7 supervision container (verification)** start as root with `cap_drop: ALL` and `cap_add: NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT`, under the same seccomp profile.
- **Who holds what:** the root launcher uses `SETUID`, `SETGID` and `SETPCAP` only to create the unit and drop its own capabilities. The supervisor keeps `CAP_NET_ADMIN` alone. The Worker and Chromium hold **no** capability and run non-root (kernel uid 10001).
- **Why `SYS_CHROOT`:** Docker's profile allows `chroot` only when the container holds `SYS_CHROOT`, and the Chromium sandbox needs `chroot` in its own user namespace.
- **Still prohibited:** `--privileged`, `CAP_SYS_ADMIN` and `--no-sandbox`.
- **Unchanged:** the main test container keeps its non-root posture.

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
**Amended by ADR-075 (2026-11-19).**
- **Linux unit:** a Linux containment unit is no longer a cgroup v2 unit. It is a user namespace owned by the supervisor's uid plus a PID namespace, inside a container dedicated to one incarnation, whose network namespace is the dedicated one.
- **Proof of death:** "unit empty" is proven by reaping the unit wrapper plus an independent `/proc` scan, not by `cgroup.events`.
- **Unchanged:** the evidence contract, the human-confirmation rules and the "never proof" list.
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

## ADR-064 — Repeat safety, attempt journal contract and fence evidence (Phase 4 step 6)
**Status:** Accepted (2026-11-15; implements ADR-045, ADR-049, ADR-050, ADR-056–ADR-060 as specified in §7.2, §7.5 and §10.5).
- **Evaluator:** `RepeatSafetyEvaluator` (pure Application) applies S0–N4 exactly as §10.5, first match wins, mandatory default `NeedsReview`. `FenceTime` (= dispatch horizon) is the earliest of a fenced (late) completion's `FinishedAt`, `AbandonedAt` without checkpoint, and an external `FencedAt`; it is never derived from a timeout. A fence never changes an attempt's side-effect state or outcome. `Admits(intent, decision, hasReconcileSection)` is the executor guard.
- **History details:** a `Reserved` entry (visible only in a refusal's history) has no snapshot or start time, so `AttemptSummary.Snapshot`/`StartedAt` are nullable; any other entry without them, and a reconcile `Found`/`NotFound` without evidence, is `HistoryInvalid` (S0). An abandoned reconcile counts toward the A4 budget only while it has no (late) outcome.
- **Journal port additions:** `RegisterIncarnationAsync` (§7.5.1: an unregistered incarnation cannot reserve) and `RecordKillRequestAsync` (diagnostic only). A late completion keeps the entry `Abandoned` and only adds the outcome and the fence. A valid process-death or human confirmation also abandons the incarnation's still-`Started` entries before fencing them (the process is proven gone).
- **Fence evidence:** a closed `FenceMethod` list (`SupervisorUnitEmptyAndDrained`, `SupervisorHostRebooted`, `HostPoweredOffOrDestroyed`, `HostRebootedVerified`, `DrainVerified`). The journal accepts only `ProcessDeathConfirmed` and `HumanConfirmed` via `ConfirmFenceAsync`; `ExecutorEgressRevoked`/`NoCheckpoint` arise only from completion/abandonment, and `IsolationTerminated`/`EgressLeaseExpired` are refused until Phase 7 specifies their drain proof. A drain report proves "no further transmission" only if enumeration is complete, ownership unambiguous and every ledgered connection safe. Human confirmations need a platform-operator actor, a closed-list method, a 10..1,000-char reference and the personal attestation; `FencedAt` is always the journal commit time.
- **Test-only implementations:** `InMemoryAttemptJournal`, fault-injecting journals and `FakeSupervisor` live in `tests/Shared/AttemptJournalConformance/` with the shared conformance suite, compiled into the test project; an architecture test proves no production code implements `IAttemptJournal`.
- **Horizon races (H1–H4):** run through the real egress proxy and barrier with a delaying relay and a committing fake target, scaled to seconds (contract L = 2 s, skew 0). H3 runs **24 randomized real-socket races concurrently plus 1,000 randomized races on a virtual-time model** driving the real evaluator (instead of 1,000 real-time races, which would take about 30 minutes per run); the model also has a wrong-contract negative control. H4 proves the invariant checker detects a wrong attestation.
- **Deferred to the executor (step 7) and real-browser scenarios (step 9):** J1–J11 executor ordering, the intent guard wired into `ExecuteAsync`, F1–F6.

## ADR-065 — Workflow executor, handlers and registry (Phase 4 step 7)
**Status:** Accepted (2026-11-15; implements §2, §3, §7.1, §7.3, §9 and R8).
- **Handlers** (Automation, one per action, stateless) use only `IBrowserSession` (architecture-tested: no Playwright types, no script APIs, no regex). A side-effect handler finishes its pre-checks (selector and value resolution, upload inspection) and then calls `StepContext.BeforeSideEffectAsync`, which writes the durable checkpoint; only then does it dispatch. The executor additionally fails the step if a side-effect step returned without a checkpoint.
- **Registry:** `ActionRegistry` fails construction (and host startup) on a missing, duplicate, unknown or config-type-mismatched handler.
- **Request and refusals:** `WorkflowExecutionRequest` carries the pinned `WorkflowVersion` and its `RecordedDefinitionHash`; the executor refuses (nothing journaled, no browser) on a hash mismatch (`VerifyAgainst`), an invalid record, or a reconcile request without a reconcile section; refuses after reservation on `InFlight`/`AwaitingFence`/unknown incarnation; and releases the reservation on `RepeatNotAuthorized`/`HistoryInvalid` or a binding failure. The admission guard is `RepeatSafetyEvaluator.Admits` on the journal's own history.
- **Journal clock:** `StartAsync` and `CompleteAsync` return `StartedAt`/`FinishedAt`, so every attempt timestamp in the result comes from the journal clock (J10).
- **Outcomes:** exactly one terminal outcome per attempt (first set wins). After a side effect, only a terminal `captureResult` with status `failed` and resolved transience `false` gives `Failed`; every error, transient business outcome or cancellation gives `Indeterminate` keeping its code. Workflow `timeoutSeconds` expiry and the browser's `SessionTimeout` follow the cancellation rules. An unexpected engine exception ends the attempt as `EngineError` (permanent; `Indeterminate` after a side effect) and the session is still disposed and the entry completed.
- **Fencing:** `ExecutionFenced` = the session's `EgressRevoked` after disposal (a timed-out barrier leaves the attempt unfenced); an attempt that never created a session is fenced (nothing could dispatch). Completion failures are diagnostics only.
- **Server options → binding:** Development flag, the trusted `DevelopmentPrivateNetworkOrigins` (only in Development), `Timeouts.SessionSeconds`, `Uploads.MaxBytes` and the new `Uploads.AllowedExtensions` (validated at startup to be a subset of the content-sniffable types).
- **Details:** workflow `maskSelectors` are registered with the session as mask-only entries; a missing extract attribute is `ElementNotFound` (transient); `navigate` statuses ≥ 400 not in `acceptStatus` are `HttpError`; the Application condition evaluator's entry point is `ConditionEvaluator.CheckAsync` (the Phase 3 source scan bans the token `EvaluateAsync` in Automation, and that rule is kept as is).
- **Registration:** `AddWorkflowEngine` registers handlers, registry and executor but no `IAttemptJournal`, `ISecretProvider` or `WorkerIncarnation`; a host without them cannot resolve the executor (tested).

## ADR-066 — Real-browser verification, schema parity and Phase 4 completion (Phase 4 steps 8–10)
**Status:** Accepted (2026-11-15).
- **DemoCrm timing in tests:** the AutomationTests harness starts DemoCrm with `LateCommitDelayMs = 9000`, inside the documented 10 s commit-latency contract (`docs/DEMO_CRM.md`). Variant D's 5 s submit deadline therefore always expires before the late commit. The authoritative variants cite the 10 s contract, so the contract and the fixture cannot disagree.
- **Scenarios:** §12.2, A1–A4, B1–B6, C1–C4, D1, D2, K1–K3 and F1–F6 run in real Chromium against DemoCrm, with the shared in-memory journal and exact customer and POST counts. K scenarios simulate crashes by abandoning the session and journal entry, not by killing a process. Killed-Worker crash tests are a Phase 5 exit criterion against the PostgreSQL journal.
- **JSON Schema:** `docs/workflow-schema-v1.json` (draft 2020-12) is an editor aid; the hand-written validator stays authoritative. Parity tests prove three things: every numeric bound maps to and equals a `WorkflowLimits`/`SchemaV1Defaults` constant, and none is unmapped; every enum equals the code's list; and every declared property is known to the binder. The DemoCrm sample's golden hash was computed independently.
- **Glob and URL parity:** `UrlGlob` agrees with Playwright's own URL wait on real pages, and the URL Chromium reports keeps the origin and path segments of the validated URI (U8).
- **Fixture workflow tests (§13.3/§13.5):** run through the real executor, engine and proxy: upload/download round trips with hashes; upload signature rejection (no POST, no checkpoint); pixel tests for secret- and extract-masking; a leak test over results, step records, errors, journal entries, the event sink and both loggers; conditions; waits; step timeouts (continue, side-effect `Indeterminate` with a checkpoint, T4 ≈ 5 s); session-deadline `Cancelled`/`Indeterminate`; and SSRF via redirect and via a private-address url parameter (refused outside Development, never reaching the target in Development).
- **Platform verification:** all verification ran in the pinned Linux container (`automataflow-verify:pw-1.64.1`). The Windows connection-identity path (ADR-060) is implemented but **not verified on Windows**. Running the tests on the Windows host remains blocked by Smart App Control (ADR-021), which is left enabled.
- **Carried into Phase 5 (unchanged ROADMAP exit criteria):** the durable PostgreSQL journal passing the shared conformance suite; the process-death supervisor with P1–P13; database-level cross-tenant tests; killed-process K tests; hosted-mode restrictions on who may publish attestations.
---

## ADR-067 — Phase 5 persistence model and database-side tenant isolation (Phase 5 step 1)
**Status:** Accepted (2026-11-15; implements docs/ARCHITECTURE.md §7 and §7.1 layers 3, 4 and 6).
- **Entities:** Workflows, WorkflowVersions, WorkflowInputs, Jobs (options as a complex type), JobRecords, JobAttempts, ExecutionLogs, ExecutionArtifacts, UsageRecords and Integrations (schema only), plus `OrganizationSecrets`. Workflow secrets need organization-scoped storage that §7 did not list; values are stored only as Data Protection payloads.
- **Journal tables:** `AttemptJournal`, `WorkerIncarnations`, `WorkerKillRequests`, `FenceConfirmations` (an audit record of every confirmation, accepted or rejected). The journal writes them only with explicit SQL; EF maps them for the schema.
- **Exclusivity in the database:** a `Blocking` column (Reserved, Started, or a side effect that is not yet fenced) with the partial unique index `UX_AttemptJournal_OneBlocking` on (organization, operation). R6 is therefore enforced by PostgreSQL, not only by application logic. Fencing tokens come from an identity column.
- **Isolation layers:**
  - every foreign key between customer tables is composite (`OrganizationId`, parent id), so the database refuses a cross-organization reference even from a faulty application layer;
  - a scoped `TenantScope` (set once per unit of work, never switched) drives a global query filter on every organization-owned entity except the explicit-SQL journal. No scope means no rows;
  - a `SaveChanges` write guard refuses any organization-owned write outside the scope, or without a scope;
  - an internal test hook disables only the query filter, to prove the application layer on its own;
  - a source scan forbids `IgnoreQueryFilters` outside reviewed files (none yet) and any production use of the hook.
- **Statuses:** records add `Reconciled`, `Invalid` and `AwaitingFence`; attempts add `ReconciledFound`, `ReconciledNotFound`, `NeedsReview` and `Abandoned`. Statuses are stored as strings.
- **Migrations:** generated inside the pinned Linux container, because Smart App Control blocks loading the freshly built assembly for `dotnet ef` on the Windows host.
## ADR-068 — Authentication, sessions and per-request membership verification (Phase 5 step 2)
**Status:** Accepted (2026-11-15; implements docs/ARCHITECTURE.md §7.1 layer 1 and §8).
- **Identity:** `AddIdentityCore` with the Identity PBKDF2 hasher; passwords of at least 12 characters; lockout after 5 failures for 15 minutes; e-mail addresses unique. There are no cookie or UI endpoints. An unknown e-mail costs the same password-hash verification as a wrong password, and both produce the same 401 response.
- **Access tokens:**
  - JWT signed with HMAC-SHA256, valid for 15 minutes (configurable 1–60), with a 30 s clock skew;
  - claims: `sub`, `org` and `jti` only, so a role is never taken from a token;
  - the signing key (at least 256 bits) comes only from configuration (`Auth__Jwt__SigningKey`); the Api refuses to start without one (`ValidateOnStart`), and tests generate a random key per host.
- **Refresh tokens:**
  - 256 random bits, stored only as a SHA-256 hash, valid for 14 days (configurable);
  - rotated on every use with a conditional update, so of two concurrent refreshes exactly one wins and the loser counts as reuse;
  - presenting a rotated or revoked token revokes its whole family;
  - a refresh re-verifies the membership;
  - `RefreshTokens` holds user-session records: `ActiveOrganizationId` points to the membership in use, and the table holds no organization-owned data.
- **Per-request membership:** after JWT validation, middleware reads the caller's membership in the token's organization from the database, sets `ICurrentUser` (with `Role`) and binds the request's `TenantScope`. A removed membership gives 403 immediately, even with a valid access token.
- **Authorization:** policies require a minimum organization role (Viewer < Operator < Admin < Owner), based on the verified membership. Platform operator is a separate Identity role (`PlatformOperator`) checked in the database per request; it is never an organization role (§7.5.6).
- **Registration:** self-service registration (a new user plus a new organization with Owner membership) is **off unless `Auth:AllowRegistration` is set**. Auth endpoints are rate-limited per client IP (fixed window, `Auth:PermitsPerMinute`, default 20).
- **Reviewed `IgnoreQueryFilters` use:** sign-in lists the authenticated user's own memberships across organizations, always filtered by user id. This is the first entry on the source-scan allow-list.
- **Package:** `Microsoft.AspNetCore.Authentication.JwtBearer` 10.0.15 (MIT).
## ADR-069 — Workflow publication, hosted-mode attestation restriction and encrypted secrets (Phase 5 step 3)
**Status:** Accepted (2026-11-15).
- **Where use cases live:** platform use cases that orchestrate persistence (`WorkflowService`, `SecretService`, `AccountService`) are in Infrastructure, beside EF. Application keeps the pure policy they call (`ValidatedWorkflow`, `PublicationPolicy`, the evaluator, the executor contracts). Dependencies still point inward, and adding a repository interface per aggregate would be an abstraction without a seam (rule 9). Every use case takes the organization from `ICurrentUser` and puts it in every query; a foreign id gives NotFound.
- **Definitions:**
  - every draft must validate completely (`CreateForPublication`);
  - it is stored exactly as received, with its canonical hash;
  - publication re-loads it with `ValidatedWorkflow.Load(json, hash)`, and a mismatch is refused (`WorkflowTampered`, 409);
  - published and archived versions are immutable; edits create a new version;
  - publication denormalizes the declared inputs into `WorkflowInputs`.
- **Who may publish:** Admin or Owner (`Policies.Admin`); Operators author drafts and Viewers read. **Attestations** are `targetEnforced` idempotency, `authoritative` evidence and `businessKey` matching (docs/WORKFLOWS.md §9, limitation 1):
  - publishing them always requires an explicit `acknowledgeAttestations`, recorded with the version together with `PublishedBy`;
  - in `Platform:Mode = Hosted` only an Owner may publish them (`attestation-requires-owner`);
  - `Platform:Mode` (Development / SelfHosted / Hosted) mirrors the engine's mode, because the Api cannot reference Automation.
- **Secrets:** stored only as ASP.NET Core Data Protection payloads whose purpose includes the organization id, so a payload copied into another organization cannot be decrypted there. The API is write-only for values (list names, set, delete; Admin+). The Worker decrypts a secret only inside the attempt (`OrganizationSecretProvider`, the engine's `ISecretProvider`). Keys live in `DataProtection:KeysDirectory`, a shared, access-restricted volume read by both the Api and the Worker. **Package:** `Microsoft.AspNetCore.DataProtection` 10.0.15 (MIT).
- **Proof of application-layer scoping:** the cross-tenant workflow and secret tests run twice: against the normal Api, and against an Api whose DbContext has the query-filter backstop disabled. A sanity test proves that the second host really runs with the filters off.
## ADR-070 — Record files and idempotent job creation (Phase 5 step 4)
**Status:** Accepted (2026-11-15).
- **Readers:** `RecordImport` (Application, `RecordFiles`; deliberately outside the pure workflow-definition code, whose source rule forbids `JsonSerializer`) streams rows one at a time:
  - **CSV:** a strict RFC 4180 state machine with a required header (no duplicate, blank or space-padded names), CRLF/LF/CR line ends, quoted fields with doubled quotes, a rejected stray quote, and an optional UTF-8 BOM;
  - **JSON:** an array of flat objects whose scalars are converted to text, read with `DeserializeAsyncEnumerable`;
  - **encoding:** strict UTF-8; invalid bytes are refused;
  - **limits:** bytes are counted while reading (the declared size is not trusted); records, columns and cell length are checked per row. Server-configurable under `Imports:*` and validated on start;
  - **errors:** a code and a position, never a cell value.
- **Upload checks (Api):** `.csv` or `.json` only, with a matching content type (415 otherwise); request and multipart size limits; sanitized file names, kept for display only; the file is parsed from the request buffer and never stored.
- **Columns:** CSV headers must be declared inputs, otherwise the whole file is rejected (`column-unknown`). The opt-in `ignoreUnknownColumns` drops undeclared columns, for example the test-expectation column of `samples/demo-customers.csv`.
- **Job creation:**
  - only a **published** version can be used, and its hash is re-verified;
  - parameters are exactly the declared ones, and url parameters must be canonicalizable origins (the Worker binds them again);
  - each row is validated by `RecordValidator`: valid rows become Pending records with a fresh OperationId, invalid ones become `Invalid` records holding error paths and codes only;
  - inputs are stored as Data Protection payloads when the workflow declares a sensitive input;
  - rows are saved in batches of 500 with the change tracker cleared after each batch (bounded memory), all **in one transaction**, so a malformed file creates nothing;
  - a usage record (`records.imported`) is written.
- **Idempotency:** `Idempotency-Key` is unique per organization. The job stores a SHA-256 request fingerprint (version, format, sorted parameters, options, column mode and file content):
  - a replay with the same fingerprint returns the original job (200, `replayed`);
  - a different request with the same key gets 409;
  - concurrent requests with the same key are serialized by the unique index, and the losers answer as replays (tested with 6 concurrent uploads: one job, 50 records).
- **Reads:** job views count records by status. Record listings return row number, OperationId, status, attempts, error code, result and validation codes, and **never input values**.
## ADR-071 — The PostgreSQL attempt journal (Phase 5 step 5)
**Status:** Accepted (2026-11-15; implements ADR-049, ADR-056, ADR-058 and ADR-059 as specified in PHASE4_PLAN §7.2 and §7.4).
- **One transaction per operation:**
  - `SET LOCAL synchronous_commit = on`, so the commit is durable whatever the server default; the Worker additionally checks `fsync = on` at startup;
  - a transaction-scoped advisory lock on (organization, operation), and **only then** the clock is read (this is what F8 needs);
  - a lease sweep (expired Reserved entries deleted, expired Started entries → Abandoned);
  - the decision, then the write.
  
  Every write after `ReserveAsync` is fenced on the fencing token, the expected state and the lease, by the journal clock.
- **Clock:** the database's `clock_timestamp()`, taken inside the committing transaction after all checks. That is the latest point before commit that can be observed; the commit itself follows immediately. A `TimeProvider` override exists only through an internal constructor used by the conformance suite.
- **Invariants enforced by PostgreSQL:**
  - a trigger derives `Blocking` from the row (Reserved, Started, or a side effect that is neither executor-fenced nor externally fenced), and `UX_AttemptJournal_OneBlocking` allows one such entry per operation;
  - a guard trigger makes history append-only: only never-started Reserved rows can be deleted; identity is immutable; states only move forward; a recorded start, checkpoint, observation, abandonment, finish, outcome or fence never changes; `ExecutionFenced` never goes back to false (SQLSTATE `AF001`).
- **Fence confirmation:** it locks every operation of the incarnation in a fixed order. Then, in one transaction, it validates the evidence (`FenceEvidenceValidator`), deletes Reserved entries, abandons Started ones and fences every unfenced entry at the commit-time clock, and audits **every** call, accepted or rejected, in `FenceConfirmations`.
- **Ambiguous commits:** any failure during COMMIT surfaces as `JournalUnavailable` ("outcome unknown") and is never treated as success or failure. Callers stay conservative: the executor does not dispatch without a confirmed checkpoint; a commit that did land leaves a checkpoint, which keeps the attempt blocked until it is fenced.
- **Port additions (additive, implemented by the in-memory journal and covered by the shared suite):**
  - `RenewLeaseAsync`: fenced; it never revives a lost lease;
  - `HeartbeatAsync`: incarnation liveness for the supervisor; it fences nothing;
  - `JournalCapabilities.LeaseLength` and `LeaseSafetyMargin`, for the Worker's startup check.

  Defaults: a 180 s lease and a 10 s margin (`Journal:*`).
- **Architecture rule:** the Phase 4 test "no production journal" becomes, as §7.4 specifies, "the only production journal is `PostgresAttemptJournal` in Infrastructure, durable and fencing". Application and Automation contain none.
- **Proof:** the shared conformance suite runs **unchanged** against PostgreSQL, including F8 with 1,000 real concurrent races, plus two new shared tests for renewal and capabilities. PostgreSQL-specific tests cover: 12 connections racing over 25 operations (exactly one admitted each time); the database clock; fencing across two journal instances; append-only refusals; a derived `Blocking` that cannot be overwritten; a connection killed just before COMMIT (reported unavailable, and here provably not recorded).
## ADR-072 — Worker execution platform (Phase 5 step 6)
**Status:** Accepted (2026-11-15; implements docs/ARCHITECTURE.md §5 with the safety rules of ADR-045 to ADR-060).
- **Claiming:**
  - each candidate job row is locked `FOR UPDATE SKIP LOCKED`, so per-job `MaxConcurrency` holds across Workers (tested with two Workers: never more than one Running);
  - records are claimed with `SKIP LOCKED` and a claim lease, renewed by the heartbeat;
  - expired claims are returned to Pending by a recovery loop;
  - these are the Worker's reviewed cross-organization queries (explicit SQL; they move queue state only and never read values), so all processing runs inside the record's own organization scope;
  - `Worker:MaxConcurrentAttempts` bounds browser sessions per Worker.
- **Processing:**
  - the pinned version is re-loaded with hash verification before **every** attempt, and a tampered definition fails the record without executing;
  - the real executor runs the attempt, and its admission guard re-evaluates the journal;
  - the Worker then reads the journal's fresh history (`ReadHistoryAsync`, a new read-only port call) and asks the `RepeatSafetyEvaluator` for the next step;
  - **`RecordScheduler`** (Application, pure) maps the result and decision to the record's next state:
    - resolutions end the record;
    - `Allowed` retries with exponential backoff up to `MaxAttempts`; exhausted attempts with an open ambiguity give **NeedsReview, never Failed**;
    - `ReconcileRequired` / `ReconcileTooEarly` schedule reconcile attempts;
    - `AwaitingFence` is re-checked and becomes **NeedsReview after `FenceWaitSeconds`**;
    - an unreadable history waits instead of deciding;
    - a cancelled job never hides an ambiguity.
  - The scheduler never authorizes anything itself; the executor's guard stays authoritative.
- **Attempt lease renewal:** the executor renews its own reservation's journal lease every third of the lease length while the attempt runs. A failed renewal cancels the attempt (Cancelled, or Indeterminate after a side effect).
- **Persistence after an attempt:** one transaction, conditional on this Worker still holding the claim, stores:
  - the record transition;
  - `JobAttempt` (status, codes, step records without values);
  - the buffered `ExecutionLogs` (already redacted; at most 2,000 per attempt, overflow counted);
  - `ExecutionArtifacts`;
  - usage (`attempts.started`);
  - job finalization (Completed / CompletedWithErrors / Failed / Cancelled, race-safe).
  
  A Worker that lost its claim stores nothing; the journal keeps the truth.
- **Artifacts:** a file store with keys generated only by the platform (`org/job/record/attempt/random-id.ext`), validated by shape and root on read. They are served only by an authorized endpoint as `application/octet-stream` attachments with `nosniff`.
- **Cancellation:** a Pending record is cancelled directly only if its operation has **no journal history**. Any record with history (possibly an unresolved side effect of a crashed Worker) is resolved by the Worker through the evaluator. Running attempts observe cancellation through the heartbeat.
- **Exports:** results CSV (row, operation id, status, error code, result fields; never inputs) with spreadsheet-formula neutralization.
- **Fail-closed startup (ADR-058 rule 2):** the Worker refuses to execute unless:
  - the journal is durable and fencing, and its lease is at least 120 s + margin;
  - the server has `fsync` on;
  - `DataProtection:KeysDirectory` and `Artifacts:RootDirectory` are configured;
  - a containment handshake (step 7) succeeds;
  - the incarnation is registered.
  
  The executor is resolved lazily, so nothing can execute before these checks pass.
- **Test layering:** AutomationTests may now also reference Infrastructure and the Worker, to run the real Worker composition end to end against DemoCrm. It still never references the Api. A separate end-to-end project could not reference DemoCrm (ADR-027).
- **DI split:** Api use cases that need an authenticated caller are registered by `AddPlatformServices` (Api only); the Worker composition is `AddWorkerServices`.
## ADR-073 — Operator fencing, explicit requeue and separate-process verification (Phase 5 step 8a)
**Status:** Accepted (2026-11-17). Covers the Phase 5 work that does not depend on the process-death supervisor. The supervisor (step 7) is blocked on an environment decision (docs/PHASE5_PLAN.md, "Feasibility spike").
- **Human fence confirmation (§7.5.6):**
  - `GET /api/v1/platform/incarnations` and `POST /api/v1/platform/incarnations/{id}/fence` are restricted to the **platform-operator** Identity role, never a tenant role. The reads span organizations by design but return incarnation identity and counts only;
  - the service builds the evidence from the incarnation's recorded unit and the authenticated operator, with `ActorRole.PlatformOperator`;
  - the journal validates it (closed method list, 10–1,000-character reference, personal attestation, drain report for `DrainVerified`) and audits every call, accepted or rejected;
  - the Api registers the journal for this purpose only: it never executes, and still never references Automation.
- **Explicit requeue (§7.5.7):** `POST /api/v1/jobs/{job}/records/{record}/requeue` (Admin+) moves a `NeedsReview` record back to the queue and reopens its job. A fence never restarts automation by itself. After a requeue, the Worker re-evaluates the journal and the executor's guard still refuses any unauthorized repeat, so a requeue leads to reconciliation, never to a blind repeat.
- **Test-only Worker host:** `tests/AutomataFlow.WorkerTestHost` is the real `AddWorkerServices` composition plus a test containment handshake, so tests can run Workers as separate OS processes and kill them. It is never referenced by any project, and production code contains no handshake implementation yet (both architecture-tested). The production Worker still refuses to start without a real handshake.
- **Separate-process verification (AutomationTests, real Api process, real Worker processes, real Chromium, DemoCrm, PostgreSQL):**
  - **Demo CSV end to end:** `samples/demo-customers.csv` through the Api with two Worker processes. Every row matches its `expected` column; exactly the 7 `created` rows exist at the target; the results export carries no inputs.
  - **K2 (SIGKILL of the whole Worker tree before the checkpoint):** the abandoned attempt has no checkpoint, so it is fenced by `NoCheckpoint` and retried automatically: one POST, one customer.
  - **K1 (SIGKILL after the checkpoint and the POST; the target commits late):**
    - the attempt is unfenced, so the record waits (`AwaitingFence`), then becomes `NeedsReview` after `FenceWaitSeconds` and is **never retried**;
    - an audited operator fence through the Api plus an explicit requeue leads to a reconcile that **finds** the late-committed customer (`Reconciled`);
    - totals: one POST and one customer for the record.
  - These are real process kills, but **without the process-death supervisor**: automatic `ProcessDeathConfirmed` fencing (P1–P13) remains step 7.
- **Leak checks:** neither the CRM password nor the record's e-mail appears in the stored execution logs or the Worker's own logs.
## ADR-074 — Job-finalization race fix, completion retry and the operations runbook (Phase 5 step 8b)
**Status:** Accepted (2026-11-17).
- **Completion retry:** the executor retries `CompleteAsync` up to 3 times with a short backoff. A completion only adds information, so retrying is safe.
  - An `InvalidState` answer on a retry *may* mean the earlier, unacknowledged commit landed. That is never assumed (ADR-071): the executor re-reads the history and records `JournalCompletionAmbiguousButRecorded` only if the entry shows this attempt's outcome, error code and a finish time; otherwise `JournalCompletionFailed`.
  - A `Fenced` refusal, or `InvalidState` on the **first** call, is a refusal, not a transient failure, and stops the retries.
  - Persistent failure keeps the J5 behaviour (diagnostic `JournalCompletionFailed`; the entry is judged at lease expiry).
  
  Without retries, one transient journal error left an attempt `Started` until its lease expired, blocking the operation for up to the lease length (and leaving it unfenced if it had checkpointed).
- **The Step 6 stall, root cause found and fixed:** it recurred once with diagnostics. Both records were `Succeeded`, yet the job stayed `Running`. Cause: a **job-finalization race**. Two records finished in two concurrent transactions; under read committed, each "are all records done?" check saw the other record still `Running`, so neither finalized the job.
  - **Fix:** every transaction that can finish a job (record completion, cancellation, requeue) first takes the **job-row lock** (`JobFinalizer.LockAsync`). This is the same lock order as claiming (job, then records), so finalization is serialized and the second check sees the first commit.
  - **Defense in depth:** the Worker's recovery loop also finalizes any running job with nothing left to run (`FinalizeIdleJobsAsync`). Its candidate query is only a hint: each job is locked in its own transaction (`SKIP LOCKED`) and re-checked by the shared `JobFinalizer.FinalizeSql` in a **later statement**. Under read committed a statement's snapshot predates its own lock wait, so a single "lock and check" statement could finalize a job whose record was requeued in between, stranding a `Pending` record. One finalization statement serves every writer.
  - **Regression tests:** 40 iterations of two concurrent completions against PostgreSQL, shown to **fail without the lock** (job stays `Running`) and to pass with it; and a requeue injected between the sweep's candidate choice and its lock, which must leave the job `Running` with the record `Pending`.
  - The completion retry above is an independent hardening, not this cause.
- **Runbook:** `docs/RUNBOOK.md` covers:
  - the operating rules the guarantees depend on: no memory-snapshot restore or clone, no pause or live migration during execution, `fsync = on`;
  - required configuration and fail-closed startup;
  - handling `AwaitingFence` and `NeedsReview` records;
  - the audited operator fence procedure;
  - cancellation.
## ADR-075 — Process-death containment: one Worker per container, nested user and PID namespaces, a supervisor with a scoped CAP_NET_ADMIN (Phase 5 step 7)
**Status:** Accepted (user decision, Option 1, 2026-11-19). Amends ADR-059 (containment unit) and ADR-035 (container posture). It is **not** an unprivileged design: the supervisor holds one capability, `CAP_NET_ADMIN`, scoped to its own container's network namespace.
- **Why:** ADR-059 specified a cgroup v2 unit. The approved environment mounts `/sys/fs/cgroup` read-only and must stay non-privileged (spike, docs/PHASE5_PLAN.md).
  - Containment with unprivileged user and PID namespaces works.
  - A *dedicated* network namespace created without privileges has no route anywhere. Connecting it needs `CAP_NET_ADMIN` in the container's own namespace.
  - The step 7 spike measured both (2026-11-18/19).
- **Rejected alternatives:**
  - **Userspace forwarders (pasta, slirp4netns, relays):** they end the target's TCP connection outside the unit and can keep sending buffered bytes after the fence, which breaks E2 (ADR-060; P7, P8).
  - **`setns` in the seccomp profile with a lab topology:** it verifies mechanisms only, and leaves the production Worker unable to reach targets.
  - **cgroup v2 unverified:** it violates "claimed only when run".
- **Topology (Linux, the hosted platform):**
  - **One Worker incarnation per container.** Docker creates the container's network namespace and its kernel-level networking (veth/bridge). That namespace is the incarnation's dedicated network namespace. No userspace relay carries any traffic.
  - **PID 1 is the supervisor.** The launcher starts as PID 1 and `exec`s into the supervisor, so Docker delivers `SIGTERM` straight to it and it reaps its only child, the unit wrapper. **Docker's `--init` (tini) must not be used.** tini runs as root without `CAP_KILL` (every capability is dropped), so it cannot forward `SIGTERM` to the supervisor's uid. It dies with "Operation not permitted" and the container is torn down without a fence. The production-image smoke test found this.
  - **Root launcher** (`deploy/worker/af-worker-launch.sh`): it runs once at container start and creates the unit. It then `exec`s into the supervisor, so the supervisor is the unit wrapper's parent and PID 1.
  - **Unit:** a user namespace **owned by the supervisor's uid** (10002) that maps inner uid 1000 to the Worker's kernel uid 10001, plus a PID namespace.
    - The unit's init is a small reaping shell that forwards `SIGTERM`. It runs the Worker with **zero capabilities** (permitted, effective, inheritable, ambient and bounding sets all empty) and `no_new_privs`.
    - Chromium's own sandbox namespaces nest inside the unit. The Chromium sandbox stays on.
  - **Supervisor:** uid 10002, its own uid, with `CAP_NET_ADMIN` only (effective, permitted, ambient; the bounding set holds `NET_ADMIN` alone) and `no_new_privs`. It is non-dumpable.
    - It shares the container's network namespace with the unit, so it can `SOCK_DESTROY` there without `setns`.
    - Through the user-namespace owner rule it holds every capability **inside the unit's user namespace only**: it can signal, inspect and kill unit tasks, and nothing else.
  - **Container capabilities:** `cap_drop: ALL`, then `cap_add: NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT`.
    - `SETUID`, `SETGID` and `SETPCAP` are used by the root launcher only: writing the unit's uid and gid maps, and dropping its own capabilities.
    - `SYS_CHROOT` is needed only because Docker's seccomp profile allows `chroot` only when the container holds it, and Chromium's sandbox `chroot`s inside its own user namespace. The launcher clears it from every process it starts.
    - Never `--privileged`, never `CAP_SYS_ADMIN`, and never `CAP_DAC_OVERRIDE`, `CAP_CHOWN` or `CAP_KILL`.
    - The launcher **refuses to start** if the bounding set contains anything outside these five, or if seccomp is not in filter mode.
    - **Filter mode alone does not prove the profile.** Docker Desktop attaches a filter even with `seccomp=unconfined`. The supervisor therefore checks **behaviourally** that a profile derived from Docker's default is enforced: `keyctl`, which such profiles deny because the keyring is not namespaced, must fail with EPERM. Otherwise startup is refused and the Worker never starts.
      - The probe cannot tell the approved profile from Docker's default. The default would break the Chromium sandbox, so the Worker could not run browsers under it.
    - **Checked manually, not by the automated suite** (2026-11-19, against the built production image `deploy/worker/Dockerfile`):
      - Docker's default capability set, `--privileged` and an added `CAP_SYS_ADMIN` are each refused by the launcher;
      - `seccomp=unconfined` is refused by the supervisor;
      - the approved posture passes both checks.
  - **Volume permissions:** the supervisor is in the Worker's group for the ledger and handshake directories **only**. The Data Protection key ring, the artifacts and the Worker's home are owner-only (0700, uid 10001). Without that, the supervisor's database credentials plus a readable key ring could decrypt tenant secrets. The Api must run as uid 10001 against these volumes (Phase 7 full stack).
- **Proof of death (`ProcessDeathConfirmed`, method `SupervisorUnitEmptyAndDrained`):**
  1. Record the kill request.
  2. `SIGKILL` every unit task and the wrapper.
  3. Reap the wrapper. `unshare --fork` exits only after the namespace init, and the init's exit completes only after the kernel killed and reaped every task of the PID namespace and its descendants.
  4. Scan `/proc` independently. Proof requires **no** task with the Worker's kernel uid, **no** task in the unit's user or PID namespace, and only PID 1 and the supervisor's own ancestors besides the supervisor itself. That allowed set is derived at run time and is never configurable.
  5. Run the socket drain. A fence is reported only when steps 3 to 5 all hold. PID 1 exiting, a kill request, an expired lease, a stale heartbeat or a vanished PID is never proof.
- **Socket drain in the shared container namespace (ADR-060 rules otherwise unchanged):**
  - A complete `sock_diag` dump (IPv4 and IPv6, all states; truncation, errors or `NLM_F_DUMP_INTR` fail closed).
  - **Ownership by inode:**
    - sockets whose inode is in the supervisor's own fd table are the supervisor's and are excluded;
    - inode 0 marks an orphan; after the proven-empty unit, orphans can only be the dead incarnation's (or the supervisor's own closed sockets);
    - any other non-zero inode belongs to a live foreign process, which is ambiguous ownership. The drain waits and, at the deadline, reports **no fence**.
    - **One exception: a listening socket.** A listener carries no payload of any connection, so it is never ambiguous. The case that matters is Docker's embedded DNS resolver, which the daemon injects on `127.0.0.11` into every container on a user-defined network (found by the step 7 tests). Connections still queued on such a listener are not yet accepted, have inode 0, and are drained like every other orphan.
  - The supervisor runs the drain with `Pooling=false` and no database I/O, so its own TCP set is empty.
  - Transmit-capable orphans are destroyed by **exact cookie**. The kernel answers `ENOENT` both for "gone" and for a cookie mismatch (observed), so a destroy result is never trusted: only a re-enumeration showing every socket safe drains.
  - The ledger cross-check (same tuple, other cookie means no fence) is unchanged. The ledger file is the one recorded in the incarnation, inside the unit's ledger directory, which must contain nothing else.
- **Handshake (fail-closed Worker start, ADR-058):**
  - **Supervisor at startup:**
    - checks its own credentials (only `NET_ADMIN`, its own uid, non-dumpable);
    - proves the approved seccomp profile is enforced (the `keyctl` probe above);
    - proves exact-cookie destroy with a live probe: a same-tuple replacement survives a stale-cookie destroy, and the exact cookie destroys (P11b at every start);
    - checks the unit (wrapper is its child, unit user namespace owned by its uid, same network namespace, no foreign processes).
  - **Worker:**
    - checks its own credentials (no capabilities, `no_new_privs`, nested PID namespace);
    - connects to the supervisor's Unix socket. Its directory is created by the supervisor's uid before the unit can run, and only the Worker's group may connect.
    - The supervisor authenticates the peer by `SO_PEERCRED`: Worker uid, unit PID and user namespace, zero capabilities.
    - It answers with the unit (`LinuxNamespaces`) and the ledger directory.
    - The supervisor reads with plain `recv` and never installs passed file descriptors (`SCM_RIGHTS`), so the Worker cannot plant a socket in its fd table.
- **Graceful stop:** `docker stop` sends `SIGTERM` to the supervisor (PID 1). Verified on the production image: exit 0, `Worker stopped`, then an accepted `ProcessDeathConfirmed`.
  - The supervisor forwards it to the unit init and waits for the Worker's own shutdown (attempts cancel with barrier-fenced completions).
  - It then kills, proves and drains as above, and fences whatever is still unfenced.
  - `stop_grace_period` must exceed Worker shutdown plus the drain deadline.
- **Lifecycle:** after a fence (or a failed proof) the supervisor exits, so the container exits and Docker's restart policy restarts it with **fresh** PID and network namespaces and a new incarnation. The writable layer survives a restart, but every launch uses new random directories for its ledger, handshake socket and home.
  - If the supervisor (PID 1) dies, the container stops and the kernel kills the unit, with no fence recorded (P6).
  - Recovery of that incarnation's records then follows ADR-059: `AwaitingFence`, then `NeedsReview`, then an operator fence.
- **Threat model and its limits:**
  - **Proven against the OS:**
    - the Worker and its descendants hold no capability;
    - they cannot address the supervisor (separate PID namespace);
    - they cannot signal or ptrace it (different uid, no capability in its user namespace; non-dumpable);
    - they cannot leave the unit (PID and user namespace membership cannot be dropped; setuid binaries are inert under `no_new_privs` and unmapped owners).
  - **Cross-container:** the supervisor's `CAP_NET_ADMIN` is scoped to its own container's network namespace. It cannot enter another (`setns` is denied) and sees only its own PID namespace.
  - **Not claimed:** the Worker holds the same database credentials as the supervisor and could call `ConfirmFenceAsync` itself. The Worker process is trusted platform code; the untrusted party is web content inside the sandboxed browser. Separate database roles for fence confirmation are future hardening.
  - **The root launcher** runs with five capabilities for the launch step only. That is narrower than Docker's defaults, but it is a root process at start.
  - **The unit sees the container's `/proc`**: mount namespaces are denied by seccomp. Process identity inside the Worker is read from `/proc/self`, never by namespace-relative PID.
- **Host-reboot proof (P5):** no automatic `SupervisorHostRebooted`. `HostId` is not unique across hosts in containers (`/etc/machine-id` comes from the image), so an automatic boot-id fence could fence a live Worker on another host. The boot-id proof stays at journal-validation level and in the operator's `HostRebootedVerified`.
- **Windows:** no Job Object supervisor is delivered. A Windows Worker refuses to start (no handshake). The read-only owner-module table code exists but has not been run (Smart App Control).
- **Verification:**
  - A second container with the production posture runs the real launcher, the supervisor and the real Worker. Tests drive it through a test-only root agent over a Unix socket on a shared volume.
    - One difference: that container uses `--init`, because the agent, not the supervisor, is its long-lived process; the supervisor under test is therefore not PID 1.
    - The supervisor-as-PID-1 path is verified on the built production image: graceful `docker stop` gives exit 0, `Worker stopped` and an accepted fence row.
  - P12 is unavailable: `sch_netem` cannot be loaded and the image has no `tc`.
## ADR-076 — Dashboard stack and the OpenAPI contract (Phase 6 step 1)
**Status:** Accepted (2026-11-20). Resolves the libraries deferred by ADR-019.
- **Libraries (all pinned exactly in `web/automataflow-web/package.json`, lockfile committed):** React 19, react-router 8, TanStack Query 5, openapi-fetch, Tailwind CSS 4 (Vite plugin); dev: Vite 8, TypeScript 5.9, Vitest, Testing Library, ESLint, openapi-typescript, `@playwright/test` (same Playwright version as the .NET pin). Licence audit of the full lockfile (269 packages): MIT, Apache-2.0, ISC, BSD-2/3, 0BSD, BlueOak-1.0.0, MPL-2.0 (lightningcss, used unmodified), CC-BY-4.0 (caniuse-lite data), Python-2.0 (argparse). No paid or restrictive licence.
- **Monaco is not used.** The JSON editor is a plain text area with server-side validation (`POST /api/v1/workflows/validate`). Monaco would add several megabytes and a worker setup for little gain over the guided form editor, which is the primary authoring path.
- **Contract:** the typed client is generated from a committed snapshot of the Api's OpenAPI document (`web/automataflow-web/openapi/automataflow-api.json`). `OpenApiContractTests` fails when the Api's contract drifts from the snapshot; `scripts/update-openapi.ps1` regenerates the snapshot and `src/api/schema.d.ts` inside the pinned containers. `npm run check:api` fails when `schema.d.ts` is stale against the snapshot.
- **Same origin only:** the Api has no CORS. In development Vite proxies `/api`; in deployments a reverse proxy does the same (Phase 7).
- **Rendering rules (lint-enforced):** API text is rendered as text, never as HTML (`dangerouslySetInnerHTML` forbidden); no web storage (`localStorage`/`sessionStorage` forbidden). Artifacts and exports are fetched with the bearer header and handed to the user as object URLs; a token never appears in a URL.
- **Toolchain:** Node 24 in the pinned image `.node-image`. The verification script runs lint, type check, unit tests, build and the API-type check in that image; the host may also run them (Smart App Control did not block the native Vite/Tailwind modules on 2026-11-20).

## ADR-077 — Browser sessions: HttpOnly refresh cookie and dashboard read endpoints (Phase 6 step 2)
**Status:** Accepted (2026-11-20). Amends ADR-068 ("no cookie endpoints") for browser clients only and supersedes the memory-only refresh token first planned for the dashboard.
- **Why:** session restoration after a reload is a Phase 6 requirement, and refresh tokens must never be in web storage or readable by page script. Keeping the refresh token in JavaScript memory makes every reload a sign-out; keeping it in web storage exposes a 14-day credential to any injected script.
- **Transport:** a request with `X-AutomataFlow-Session: browser` on `login`, `register`, `refresh` or `logout` uses the browser transport:
  - the refresh token is set only as the cookie `__Secure-af_refresh` (`HttpOnly`, `Secure`, `SameSite=Strict`, `Path=/api/v1/auth`, expiry = token expiry) and the body's `refreshToken` is `null`;
  - `refresh` and `logout` read the token from the cookie **only when the header is present**. A cross-site form or image request cannot add a custom header, and the Api has no CORS, so a cross-origin script cannot either. `SameSite=Strict` is a second, independent guard;
  - a refused refresh (invalid, reused, revoked, membership gone) and `logout` delete the cookie;
  - token responses carry `Cache-Control: no-store`.

  API clients without the header keep the body transport unchanged; rotation, reuse detection and family revocation (ADR-068) apply to both.
- **Access token:** stays in JavaScript memory only (15 minutes), sent only in the `Authorization` header.
- **Tabs:** the cookie is shared between tabs, so refreshes are serialized across tabs with a Web Lock (`navigator.locks`); the second tab then presents the already-rotated cookie instead of tripping reuse detection. A sign-out is broadcast to the other tabs (`BroadcastChannel`).
- **Dashboard reads (all organization-scoped through `ICurrentUser`, cross-tenant tested on hosts with and without the query-filter backstop):**
  - `GET /api/v1/me` returns e-mail, active workspace name and slug, role, platform-operator flag and the caller's own memberships (the reviewed membership read of ADR-068, constrained to the user id);
  - `GET /api/v1/overview`: job and record counts by status, workflow totals, five recent jobs and workflows, all counted in the database;
  - `GET /api/v1/workflows?search=` (case-insensitive, LIKE wildcards escaped) and each summary's newest published version;
  - `GET /api/v1/jobs?status=`; job views add workflow name, version number and invalid-record count, and record counts are read in one grouped query instead of one query per job;
  - `GET /api/v1/workflow-catalog`: the 13 implemented actions (sections, outputs, side-effect and continue flags, default timeouts) and the closed value sets, from Application metadata (the Api still never references Automation);
  - `POST /api/v1/workflows/validate` (Operator+): the draft validation rules without storing anything, plus the attestations publication will require.
- **OpenAPI:** non-nullable properties are marked `required` (`RequiredPropertiesSchemaFilter`, nullable reference types honoured), so generated TypeScript types are exact. The platform fence response is now a typed record.

## ADR-078 — Production deployment: release images, database roles and configuration validation (Phase 7)
**Status:** Accepted (2026-11-20).
- **Images:** Api on the chiseled ASP.NET 10.0.15 runtime (no shell or package manager, uid 10001, read-only root, all capabilities dropped; `migrate` and `healthcheck` commands); dashboard gateway on unprivileged nginx (CSP and security headers, `/swagger` and `/health*` not exposed); Worker image unchanged (ADR-075). Every base image pinned by digest. DemoCrm is never a production image (`deploy/e2e/democrm.Dockerfile` is a test fixture).
- **Compose (`deploy/docker-compose.prod.yml`, project `automataflow-prod`):** networks `edge` (gateway, Api, optional TLS proxy), `data` (internal: database), `egress` (Worker to target sites); only the gateway (and the optional Caddy TLS profile) publish ports; resource limits and log rotation on every service.
- **Database roles:** the init script creates `automataflow_owner` (owns the database and schema; used only by the one-shot `migrate` service) and `automataflow_app` (data access only), with default privileges so every table a migration creates is granted to the app role. `SchemaMigrator` revokes the app role's write access to `__EFMigrationsHistory` after each migration. The bootstrap superuser is used only for initialization and backups. Tested by applying the real init script, migrating as the owner and running the Api through the app role (which is refused DDL, history changes and role creation).
- **Not separated (documented):** the Worker and its supervisor still share the app role, so the Worker could call fence confirmation itself (ADR-075 "not claimed"). Separating it needs column-level grants on journal tables shared by the Worker's normal writes and the fence path, plus a third role for the platform-operator fence endpoint; that is a design change of the journal's permission model, deferred to hosted-mode hardening. The Worker is trusted platform code; the untrusted party is web content inside the sandboxed browser.
- **Explicit migration:** the Api never migrates on start; `migrate` runs once per release before the new Api and Workers.
- **Configuration validation:** `scripts/validate-deployment.ps1` checks the resolved compose model (Worker posture incl. no `init`, exact capability set, seccomp profile, no host namespaces, grace period; no development browser mode or private-network exception; Production Api; registration off unless explicitly allowed; trusted proxy networks; no placeholder or weak secrets; roles per service; no published database; key ring and artifacts mounted only by the Api and the Worker). `scripts/test-deployment-validation.ps1` (run by the verification script) proves each refusal with an injected drift.
- **Forwarded headers:** honoured only from `Proxy:TrustedNetworks` with `Proxy:ForwardLimit` hops, so per-client rate limiting survives the gateway.

## ADR-079 — Data Protection key ring encrypted at rest (Phase 7)
**Status:** Accepted (2026-11-20). Resolves the Phase 5 limitation "Data Protection keys are unencrypted at rest".
- `DataProtection:CertificatePath` / `CertificatePassword` (a PKCS#12 file, mounted as a Compose secret, readable only by uid 10001 on Linux hosts) make every new key ring entry encrypted with the certificate (`ProtectKeysWithCertificate`). A copy of the keys volume alone (backup, disk, another container) no longer decrypts workspace secrets or sensitive record inputs.
- Rotation: the new certificate becomes `CertificatePath`, the old one `PreviousCertificatePath` until keys encrypted with it have expired; both are accepted for decryption.
- A configured certificate that cannot be loaded stops startup (never a silent fallback to unencrypted keys); the error never contains the password. Keys written before the certificate existed stay readable and age out through normal key rotation.
- `scripts/new-deployment-secrets.ps1` creates an RSA-3072 certificate (encryption only, never TLS). Backups contain the encrypted key ring but never the certificate; operators store `deploy/.env.prod` and the certificate separately.
- Tested through the real Infrastructure wiring: keys on disk carry `encryptedSecret` and no plaintext `<value>`; a holder of the certificate decrypts, a copy of the key directory alone cannot; a rotated certificate still reads keys of the previous one; a wrong password stops startup without echoing it.

## ADR-080 — Workspace onboarding, invitations and suspension (Phase 8)
**Status:** Accepted (2026-11-20).
- **Roles:** platform operator (Identity role, infrastructure staff) vs workspace roles Owner > Admin > Operator > Viewer. Admins manage Viewers, Operators and Admins; only an Owner grants, changes or removes the Owner role; the last Owner can never be removed or demoted. Removing a member revokes their refresh tokens for that workspace; the per-request membership check (ADR-068) already ends access immediately.
- **Invitations:** bound to one e-mail address and a role; a 256-bit token shown ONCE to the inviting admin and stored only as a SHA-256 hash; valid 7 days; single use (a conditional update makes concurrent acceptance safe); a new invitation for the same address revokes the open one; revocable. The platform sends no e-mail (no paid or external service): the admin delivers the link. The link carries the token in the **URL fragment** (`/invite#token`), which browsers never send to servers, so it never reaches access logs; preview and acceptance send it in a POST body. The page removes it from the address bar after acceptance.
- **Acceptance:** an existing account must prove its password (lockout rules apply); otherwise the account is created with the invited address and the password policy. Unknown, expired, revoked and used tokens are indistinguishable (404). Preview and acceptance are rate-limited like sign-in.
- **Reviewed cross-organization reads (source-scan allow-list):** `InvitationService` (constrained by the token hash) and `PlatformWorkspaceService` (platform operators; identity and counts only, never customer data). Writes into a newly created workspace use a fresh DI scope whose tenant is that workspace; a request scope still never switches organizations.
- **Workspace switching:** `POST /api/v1/auth/switch` issues a session for another workspace of the same user and revokes the previous refresh token.
- **Suspension:** a platform operator suspends or reactivates a workspace (never their own). A suspended workspace's members cannot sign in, refresh or call the Api (403 "The workspace is suspended."), and Workers do not claim its records; attempts already running finish normally (no interruption of a side effect). Data is kept.
- **Bootstrap:** `AutomataFlow.Api bootstrap-operator` creates the first platform operator and an operations workspace it owns, with credentials from `AF_BOOTSTRAP_EMAIL` / `AF_BOOTSTRAP_PASSWORD` (environment only, never arguments, never a default); it refuses an existing address. Self-service registration stays off in production.
- **Migration:** `WorkspaceOnboarding` adds `Organizations.Status` (default `Active`) and `SuspendedAt`, and the `OrganizationInvitations` table; additive, no data change.

## Phases 6–10 authorization
**Status:** Authorized by the user on 2026-11-20 ("Master project completion directive").
- Phases 6, 7, 8, 9 and 10 are executed consecutively without asking for approval between phases. This supersedes, for these phases only, the working agreement to stop and wait for approval at the end of each phase (CLAUDE.md). Commits and pushes are made per verified step.
- A phase starts only when the previous one meets its acceptance criteria or documents its remaining limitations explicitly.
- **Stop conditions remain:** a security guarantee that cannot be preserved; an architecture decision that materially changes the approved design; a destructive data migration; a real customer secret, credential or production resource; a paid service; a legal or regulatory business decision; an irreversible production operation; a risk of destroying user work.
- `docs/ROADMAP.md` stays the single roadmap; the per-phase plans are `docs/PHASE6_PLAN.md` to `docs/PHASE10_PLAN.md`.

## Resolved from the approval round (2026-11-11)
1. SDK: selected from inspection, not blindly pinned (ADR-004). 2. PostgreSQL: Docker `postgres:18`, port 5433, volume (ADR-013). 3. Git initialized; commit after each verified phase; no secrets or generated artifacts. 4. Identity + JWT (ADR-010). 5. Hand-written validators (ADR-016). 6. Razor Pages Demo CRM (ADR-011). 7. Folder layout `src/ tests/ web/ deploy/ docs/`, React in `web/automataflow-web`. 8. 16-day target, 16–20 realistic; quality over deadline. 9. Frontend libraries deferred (ADR-019). 10. Playwright Chromium download approved for Phase 3.

## Phase 4 final approval
**Status:** Approved by the user on 2026-11-15, after review round 9 of docs/PHASE4_PLAN.md.
- **What becomes binding:** ADR-045 to ADR-060 (as amended through round 9) and the round-4 amendments of ADR-038, ADR-039, ADR-043 and ADR-044.
- **Implementation:** authorized; it proceeds sequentially by the plan's steps, each one reviewed before the next.
- **Verification:** specification approval does not waive any implementation or platform-specific verification. A safety guarantee is claimed as implemented only once code and tests demonstrate it.

## Pending approval
None at this time. On 2026-11-15 the user authorized the Step 3 audit follow-up and Phase 4 steps 4, 5 and 6 to be completed consecutively, with one consolidated report after step 6. After accepting them, the user authorized steps 7, 8, 9 and 10 to be completed consecutively. Phase 4 is complete with step 10. On 2026-11-15 the user authorized Phase 5 in full, to be completed consecutively (docs/PHASE5_PLAN.md).
