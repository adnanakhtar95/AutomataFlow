# CLAUDE.md — AutomataFlow Project Instructions

These instructions are permanent. Read them and `docs/` before any task. If a task conflicts with them, raise it before proceeding.

## What this is
AutomataFlow is a browser automation orchestration platform: businesses define configurable workflows, run them over CSV/JSON records via background workers using Playwright, and monitor results. We are building a maintainable commercial product, not a demo.

Strategy order: (1) reliable engine -> (2) paid custom client projects -> (3) reusable templates -> (4) hosted SaaS -> (5) agency/white-label. **Do not build billing, subscriptions or advanced SaaS features before the core engine works.**

Docs: `docs/PRODUCT_SPEC.md`, `docs/ARCHITECTURE.md`, `docs/ROADMAP.md`, `docs/DECISIONS.md`. Keep them current; record any significant decision in DECISIONS.md as a new ADR.

## Stack
C# / .NET 10 LTS, ASP.NET Core Web API, EF Core, PostgreSQL, Microsoft Playwright for .NET (Chromium), React + TypeScript + Vite + Tailwind CSS, Docker Compose, Serilog, Swagger/OpenAPI, xUnit.
SDK: `global.json` selects the .NET 10 SDK compatible with the installed Visual Studio 2026 (18.x) — currently 10.0.302, `rollForward: latestFeature`. The machine also has SDK 11 (preview) installed; never build with it.
Database: PostgreSQL 18 via Docker (`postgres:18`), host port **5433**, named Docker volume. The locally installed PostgreSQL service on 5432 is not used by this project.
Auth: ASP.NET Core Identity (password hashing) + JWT bearer. Validation: hand-written validators (no validation library). Demo CRM: ASP.NET Core Razor Pages, fully separate from the Api and the frontend.
Frontend library choices (TanStack Query, Monaco, openapi-typescript) are deferred to Phase 6 — do not install them earlier.

## Solution layout
```
src/   AutomataFlow.Domain | .Application | .Infrastructure | .Automation | .Api | .Worker
       DemoCrm/                 (fictional Razor Pages portal automated by the platform)
web/   automataflow-web/        (React dashboard)
tests/ AutomataFlow.UnitTests | .IntegrationTests | .AutomationTests | DemoCrm.Tests
deploy/ (docker compose, Dockerfiles, .env.example)      docs/
```
Dependency rule: Domain <- Application <- (Infrastructure, Automation) <- (Api, Worker). Domain references nothing. Application never references Infrastructure, Automation, Api or Worker. **Api must never reference Automation (directly or transitively); Infrastructure and Automation never reference each other.** An architecture test enforces this. See ARCHITECTURE.md for the full reference table.

## Mandatory development rules
1. Clean architecture; dependencies point inward.
2. Dependency injection everywhere; no static service locators.
3. No business logic in controllers — controllers map HTTP to use cases.
4. DTOs at all API boundaries; never expose EF entities.
5. async/await end to end; every I/O method accepts and honors `CancellationToken`.
6. Structured logging (Serilog message templates, never string interpolation in log calls).
7. Meaningful tests with every feature; tests must be able to fail for a real reason.
8. Readable, maintainable code; small focused classes.
9. No unnecessary abstractions — add an interface only for a real seam (testing, replaceable infra, plug-in point).
10. Modular monolith; no microservices.
11. No paid services or paid dependencies. Check licenses of every new package.
12. Do not use DotNetBrowser.
13. No paid APIs, no local Ollama.
14. No proprietary company code or data. Fictional data only.

## Security rules (non-negotiable)
- Never log plaintext credentials, tokens or customer PII; mask secrets in logs, results and screenshots metadata.
- Never commit secrets. Configuration via environment variables / user-secrets; `.env` is git-ignored, `.env.example` is committed.
- Never execute customer-supplied C# or JavaScript. Workflows are declarative, validated, versioned JSON only.
- Browser navigation is restricted to an allow-list of authorized hosts per workflow/organization.
- One isolated Playwright browser context per record attempt; no shared cookies/storage.
- Enforce upload limits (size, extension, content type) and sanitize file names.
- Tenant isolation is defense in depth. Every customer-owned row carries `OrganizationId`. **EF global query filters are a backstop, never the only control**: application services/use cases must take the organization from `ICurrentUser`, pass it explicitly to every repository call, and verify ownership of every referenced id. Every feature that touches customer data needs a cross-tenant access test. Artifacts are served only through authorized endpoints.
- SSRF protection covers ALL browser network traffic — redirects, subresources, frames, popups, WebSockets, service workers and DNS resolution — not just the initial navigation (ARCHITECTURE.md §9).
- Pre-checks alone do not prevent duplicate side effects: use stable operation identifiers, target-side duplicate detection and reconciliation (ARCHITECTURE.md §5.6).
- Untrusted hosted workloads require process/container isolation (see ARCHITECTURE.md).

## Architecture invariants
- The API NEVER runs browser automation inside an HTTP request. It enqueues; the Worker executes.
- Job state lives in PostgreSQL. Workers claim work durably (`FOR UPDATE SKIP LOCKED` + lease/heartbeat). In-memory queues may only be an optimization, never the source of truth.
- Every execution is traceable: JobId, WorkflowId+Version, RecordId, Attempt, timestamps, status, logs, errors, screenshots, artifacts, results.
- Actions implement one execution contract and are registered in an action registry; adding an action never requires changing the engine.

## Working agreements
- Do not start a roadmap phase without explicit user approval of that phase.
- Prefer small, verifiable increments: build + test green before moving on.
- Do not install packages that are not needed for the current phase. Pin versions (Central Package Management, `Directory.Packages.props`).
- Do not run destructive commands (deleting data, dropping databases, force operations) without asking.
- When uncertain about a product decision, ask; when uncertain about a technical detail, verify against the code or official docs rather than guessing.
- Windows host, PowerShell primary shell.
- Git: one meaningful commit (or a few) after each completed AND verified phase, with the build and tests green. Never commit secrets, local credentials (`.env`, user-secrets), build output, `node_modules`, Playwright downloads or other generated artifacts. Do not change the user's global git config.
- Scope discipline: Phase N work only. No billing/SaaS features and no visual (drag-and-drop) workflow editor in the MVP. Stop at the end of each phase, report build/test results and the list of files created, and wait for approval.

## Common commands
```
docker compose --env-file deploy/.env -f deploy/docker-compose.yml up -d postgres   # copy deploy/.env.example to deploy/.env first
dotnet tool restore            # local tools (dotnet-ef)
dotnet build AutomataFlow.slnx
dotnet test AutomataFlow.slnx  # database tests need AUTOMATAFLOW_TEST_DB (server connection string, db "postgres"); they FAIL, not skip, without it
dotnet run --project src/AutomataFlow.Api      # needs ConnectionStrings__Default
dotnet run --project src/AutomataFlow.Worker   # needs ConnectionStrings__Default
.\scripts\verify-in-container.ps1              # build + test in the official Linux SDK container (see below)
cd web/automataflow-web && npm run dev         # Phase 6
```
EF migrations: `dotnet ef migrations add <Name> --project src/AutomataFlow.Infrastructure --output-dir Persistence/Migrations` (set `ConnectionStrings__Default` first).

### Windows Smart App Control caveat
On the primary dev machine Smart App Control is in enforcement mode and blocks freshly built, unsigned assemblies/apphosts ("An Application Control policy has blocked this file"), so `dotnet test` / `dotnet run` on Windows can fail intermittently. This is a machine security policy: do not disable or work around it without the user's explicit decision. Authoritative verification is `scripts/verify-in-container.ps1` (Linux SDK container; source copied, working tree untouched). Test projects use xUnit v2 + VSTest because xUnit v3 requires a native apphost (ADR-021).
