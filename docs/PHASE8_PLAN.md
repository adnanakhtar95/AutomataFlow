# Phase 8 — Customer onboarding and workspace management

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). Starts when Phase 7 meets its acceptance criteria.

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
