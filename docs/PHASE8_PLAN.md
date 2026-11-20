# Phase 8 — Customer onboarding and workspace management

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). **Complete (2026-11-20)**; evidence below.

## Objective
A new business is onboarded and managed entirely through the product: no SQL, no direct database edits. Workspaces
stay isolated in every customer-facing operation.

## Scope
- **Roles:** platform operator (infrastructure staff, Identity role, never a tenant role), workspace Owner/Admin
  (manage members, secrets, publication), Operator (author drafts, run jobs), Viewer (read).
- **Workspace lifecycle:** creation by a platform operator (invite-only onboarding) or self-service registration when
  enabled; profile (name); suspension that blocks sign-in and job execution without deleting data.
- **Members:** invitations by e-mail address with a single-use, expiring, hashed token (no e-mail service is bundled:
  the invite link is shown once to the inviting admin to deliver out of band); acceptance creates or links the account;
  role changes and removal (the last Owner cannot be removed or demoted).
- **Workspace switching** for users in several workspaces.
- **Bootstrap:** the first platform operator is created by an explicit CLI command (`AutomataFlow.Api bootstrap-operator`),
  never by a default credential.
- **Usage visibility:** per-workspace usage from `UsageRecords` (records imported, attempts started).
- **Dashboard:** members page, invitation acceptance page, workspace settings, workspace switcher; a separate platform
  area for operators (workspaces list, create, suspend) that tenant users never see.
- **Tests:** cross-tenant tests for every new endpoint on both hosts (with and without the query-filter backstop),
  role tests, last-owner protection, invitation expiry/reuse, and an end-to-end onboarding journey.

## Acceptance criteria
A platform operator onboards a new business through the UI; its owner invites a colleague who signs up through the
link; both work in their workspace; two workspaces remain isolated (verified end to end and by the cross-tenant suite).

## Delivered (ADR-080)
| Capability | Backend | Dashboard | Evidence |
|---|---|---|---|
| Workspace profile, rename (Owner), usage (Admin+) | `WorkspaceService`, `GET/PUT /api/v1/workspace`, `/usage` | Workspace page | `OnboardingTests` |
| Members: list, role changes, removal with last-Owner protection and session revocation | `/api/v1/workspace/members` | Members page | `OnboardingTests` (role rules, removal), component tests |
| Invitations: one-time hashed token, 7 days, single use, replace/revoke, fragment links | `/api/v1/workspace/invitations`, `/api/v1/invitations/preview|accept` | Members page, `/invite` page | `OnboardingTests`, component tests, e2e |
| Workspace switching | `POST /api/v1/auth/switch` | user menu | `OnboardingTests` |
| Platform onboarding: list, create with Owner invitation, suspend, reactivate | `/api/v1/platform/workspaces` (platform-operator policy) | Platform page (operators only) | `PlatformOnboardingTests`, e2e |
| Suspension: no sign-in, refresh, Api access or record claiming | middleware, `AccountService`, `RecordDispatcher` | error message on sign-in | `PlatformOnboardingTests` (incl. the real claim query), e2e |
| First platform operator without default credentials | `bootstrap-operator` command | — | `PlatformOnboardingTests`, e2e bootstrap step |
| Cross-tenant isolation of every new endpoint | — | — | `WorkspaceCrossTenantWithBackstop` / `WithoutBackstop` |

Found and fixed during the end-to-end run: session refreshes shared the credential rate limit (20/min per address), so a
user reloading several tabs could lock out password sign-in. Credential endpoints (sign-in, registration, invitations)
and token-protected session endpoints (refresh, logout, switch; `Auth:SessionPermitsPerMinute`, default 120) now have
separate limits (test: ten refreshes never consume sign-in permits).

## Acceptance
A platform operator, created by `bootstrap-operator`, onboards a business through the UI; its Owner signs up through the
one-time link; the Owner invites a colleague who joins; a role change takes effect; suspension locks the team out and
reactivation restores it; customers never see the platform area. All without database access
(`e2e/onboarding.spec.ts`, release images). Two workspaces stay isolated (cross-tenant suites on both hosts, e2e).

Known limitations: no e-mail delivery (links are handed over by the inviting admin); no password reset by e-mail (a
platform-operator-assisted reset is a post-release item); no SSO/MFA.
## Verification log
- Full run a (before the rate-limit fix): UnitTests 1264, DemoCrm.Tests 157, IntegrationTests 130, AutomationTests 250,
  dashboard (40 Vitest tests, lint, type check, build, API types), deployment validation (18 cases): all passed.
- End-to-end (release images): dashboard journey passed; onboarding journey found the shared rate limit (fixed), then a
  wrong test assumption (after reactivation the kept session cookie restores the session; the spec now asserts that).
  Final run: **both journeys passed**.
- Full run b (after the fix): DemoCrm.Tests 157, AutomationTests 250 passed; UnitTests 1263/1264 and IntegrationTests
  130/131. The two failures (`LeaseRenewalTests` attempt cancelled before its lease renewed; readiness check with an
  Npgsql read timeout) are in code Phase 8 does not touch, occurred while the host VM was stalling (Integration took
  4 m 55 s instead of 1 m 38 s, Automation 19 min instead of 12), and both passed when re-run alone. Dashboard checks on
  the host: lint, type check, 40/40 tests, build, API types. Recorded as environment-timing incidents.
