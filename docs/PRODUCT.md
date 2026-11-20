# AutomataFlow — Product overview

## What it is
AutomataFlow runs repetitive web-based business tasks for you: registering customers in a CRM, entering orders,
approving invoices, and similar work done by hand in browser screens. You describe the task once as a declarative
workflow, upload a spreadsheet of records, and AutomataFlow works through them with a real browser, record by record,
with screenshots, logs and a results file, and without ever submitting the same record twice.

It is built for businesses whose systems have no usable API, and for agencies that automate such systems for clients.

## Why it is different
- **No duplicate side effects.** Every record carries a stable operation reference. Uncertain submissions are looked up
  in the target system (reconciliation) instead of being repeated; anything the platform cannot prove becomes
  "Needs review" rather than a silent duplicate. A crashed Worker is proven stopped before its work resumes.
- **No code to run.** Workflows are validated JSON (13 actions, conditions, result capture); customer code is never
  executed. A guided editor and ready-made templates make them editable by non-developers.
- **Isolation and security by default.** Workspaces are isolated in every layer; browsers run sandboxed with no
  privileges and can reach only the hosts a workflow allows; secrets are encrypted and never shown.
- **Your infrastructure.** A single Docker Compose deployment on your server or ours; no paid third-party services.

## Features
| Area | Included |
|---|---|
| Workflows | versioned JSON definitions, guided form editor and JSON editor, server-side validation with precise errors, publication with explicit confirmation of duplicate-safety claims, 3 ready-made templates |
| Actions | navigate, click, fill, select, wait, extract, screenshot, upload, download, condition, delay, capture result, reconcile match |
| Jobs | CSV/JSON upload with validation summary, idempotent job creation, per-job concurrency and retries, cancellation, live progress |
| Investigation | per-record attempts, step timings, redacted logs, screenshots, downloads, error codes, requeue for reconciliation |
| Results | CSV export without input values |
| Workspaces | Owner/Admin/Operator/Viewer roles, invitations, workspace switching, encrypted secrets, usage totals |
| Platform | operator onboarding of workspaces, suspension, operations view (queue, Workers, metrics, storage), artifact retention |
| Operations | release images, production compose, configuration validation, backup and restore, health checks, structured logs |

## Supported use cases
Back-office data entry and updates in web applications that the customer is authorized to automate: CRM and ERP data
entry, order entry, invoice and approval queues, account setup, data look-ups with result capture. Not supported:
sites that forbid automation, CAPTCHA solving, scraping at scale, payments, and regulated workloads (healthcare,
financial trading) without the customer's own compliance controls.

## Deployment options
| Option | Description | Status |
|---|---|---|
| Self-hosted | the customer runs the production compose on their own Linux server (docs/DEPLOYMENT.md) | available |
| Managed single-tenant | we operate one deployment per customer | available (same artifacts, operated by us) |
| Shared multi-tenant SaaS | many unrelated customers on one deployment | **not yet**: requires the hosted-isolation work in docs/SECURITY.md §8 |

## Capacity
Measured on the reference environment (docs/PERFORMANCE.md). Throughput scales with Worker containers: each runs a
configurable number of parallel browser sessions (default 2, about 1 vCPU and 1 GB each). Plan capacity from the
measured records per minute of your own workflows; the target system's speed usually dominates.

## Service and support model (proposal)
| Tier | Includes |
|---|---|
| Starter | self-hosted licence, documentation, e-mail support on business days |
| Professional | Starter + workflow onboarding for up to 3 workflows, upgrade assistance, response within 1 business day |
| Managed | single-tenant hosting, monitoring, backups with monthly restore tests, workflow maintenance when target screens change |

## Customer onboarding process
1. Discovery: which screens, which records, which duplicate rules exist in the target system.
2. Platform operator creates the workspace; the Owner joins through the invitation.
3. A workflow is built from a template or from scratch, validated, and run on a small sample in a test environment.
4. Duplicate safety is confirmed with the customer (operation reference accepted and searchable by the target) before
   the `targetEnforced` claim is acknowledged at publication.
5. Production runs with monitoring; Needs-review records are handled with the customer (docs/USER_GUIDE.md §4).
