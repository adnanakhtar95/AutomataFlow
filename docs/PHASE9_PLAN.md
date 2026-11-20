# Phase 9 — Commercial automation capabilities

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). **Complete (2026-11-20)**; evidence below.

## Objective
The engine serves several genuinely different business workflows without engine changes per customer, and the product
ships reusable, verified workflow packages.

## Approach
1. Inventory the 13 implemented actions against three concrete business scenarios; add a capability only when a
   scenario cannot be expressed or would be unsafe without it. Every addition stays declarative (no scripting, no
   regular expressions, ADR rules on the expression language).
2. Candidate additions, each justified by a scenario before it is built: workflow templates (import a package into a
   workspace), API-triggered jobs with service credentials, job-completion webhooks (allow-listed, signed, SSRF-checked),
   per-workspace execution quotas, usage reports.
3. Three workflow packages, each with a definition, documented inputs, credentials and allowed hosts, a controlled test
   target (DemoCrm extensions or new fictional fixtures), verified execution, failure and recovery behaviour, and known
   limitations:
   - CRM customer registration and updates;
   - e-commerce order processing;
   - invoice / administrative record processing.
   No healthcare workflow is shipped; no compliance is claimed.

## Acceptance criteria
Three packages run end to end against controlled targets through the release stack, including a failure-and-recovery
scenario each, with no engine change specific to one package.

## Capability inventory (step 1)
The 13 actions (navigate, click, fill, select, wait, extract, screenshot, upload, download, condition, delay,
captureResult, reconcileMatch) expressed all three scenarios with **no engine change**:
| Need | Covered by |
|---|---|
| sign in once per attempt | `setup` section with a secret |
| business validation before a side effect (amount check, not found, already approved) | `extract` + `condition` (`not`, `valueEquals`, `elementExists`) + terminal `captureResult` |
| duplicate-safe submission | `sideEffect` + `idempotency` (`targetEnforced`, operation id) |
| recovery after an uncertain submission | `reconcile` + `reconcileMatch` by operation id |
| transient vs permanent business errors | `captureResult.outcome.transientWhen` |
| evidence | `screenshot` |

Validator rules met while authoring (all caught by the Api before anything ran): `reconcileMatch` fields are objects;
condition nesting is limited to three levels (the invoice workflow uses flat early exits instead); a result field may
be written only once per path. These are kept: they bound complexity and make results unambiguous.

Capabilities considered and **not built** (no scenario needed them yet): API-triggered jobs beyond the existing upload
endpoint, webhooks, notifications, quotas. Built: **workflow templates** (packages served by the Api and copied into a
workspace as drafts; dashboard template picker), because they turn a package into something a customer can use.

## Delivered
- DemoCrm back office (test fixture): order desk (`/Orders/New`, confirmation, search by reference; stock; plus-tag
  failure scenarios) and invoice approval queue (`/Invoices`; one-time approval recording its operation id; INV-1006
  fails before commit, INV-1007 commits then fails); reset and state counters. 6 DemoCrm.Tests.
- Packages `crm-customer-registration`, `ecommerce-order-entry`, `invoice-approval` with manifests, samples and
  documentation (`packages/README.md`).
- Templates: `GET /api/v1/workflow-templates`, `GET /{id}`, `POST /{id}/instantiate` (Operator+); dashboard picker on
  the New workflow page. 3 integration tests (all packages validate; draft in the caller's workspace only; Viewer
  refused; CRM package equals the verified definition), 1 component test.
- `WorkflowPackageTests` (real Worker, PostgreSQL journal, Chromium, egress proxy, DemoCrm): both new packages with
  their own sample files, exact target-side counts (5 orders, 5 approvals).

## Acceptance
Three packages run against controlled targets with failure-and-recovery scenarios and no package-specific engine
change. Evidence level: real-process-tested (in-process real Worker composition against a separately running DemoCrm).