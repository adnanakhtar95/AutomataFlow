# AutomataFlow — Product Specification

Status: Draft v0.1 (pre-implementation)

## 1. Vision
AutomataFlow is a browser automation orchestration platform. Businesses create, configure, execute, monitor and manage repetitive browser-based processes: CRM data entry, customer registration, e-commerce operations, data extraction, portal automation, document processing, administrative tasks.

Eventual capabilities: multiple organizations, isolated workspaces, configurable workflows, browser workers, scheduling, monitoring, usage-based billing.

## 2. Business strategy and sequencing
1. Build a reliable automation engine.
2. Deliver paid custom automation projects using it.
3. Convert reusable solutions into workflow templates.
4. Evolve into a hosted SaaS.
5. Support agencies via white-label and multi-client workspaces.

Billing/subscriptions/advanced SaaS features are explicitly deferred until the engine is proven.

## 3. Users and personas
| Persona | Need |
|---|---|
| Operator (client staff) | Upload records, start jobs, see results, download exports |
| Automation Engineer (us / agency) | Author and version workflows, debug failures with logs and screenshots |
| Organization Admin | Manage members, integrations, settings |
| Platform Owner (us) | Operate workers, monitor health, later usage and billing |

## 4. Scope

### MVP (engine-first, single organization in practice)
- Versioned, validated JSON workflow definitions.
- Actions: Navigate, Click, Fill, Select, Wait, Extract, Screenshot, Upload, Download, Condition, Delay, CaptureResult.
- Variable substitution from record fields and prior step outputs.
- Job creation from CSV or JSON input; record-level processing.
- Background execution with configurable concurrency, cancellation, retry policy, execution timeouts.
- Partial failure handling; job history; successful-result export (CSV/JSON).
- Full traceability: logs, errors, screenshots, download artifacts, results per attempt.
- Demo CRM with CSV-driven end-to-end demonstration.
- Dashboard: overview, workflows (form + JSON editor), jobs, execution history/details, uploads, logs, artifacts, settings.
- Authentication and organization-scoped authorization (schema is multi-tenant from day one; UX is single-org).

### Post-MVP
Scheduling (cron), live execution streaming, integrations (webhooks, email, storage), workflow templates library, multi-organization UX, usage metering, billing, white-label.

### Non-goals (MVP)
- Drag-and-drop visual workflow editor.
- Arbitrary custom code in workflows.
- Browsers other than Chromium.
- Billing, subscriptions, payments.
- Microservices; Kubernetes; paid third-party services.

## 5. Functional requirements

### 5.1 Workflow engine
- A workflow is a versioned JSON document: metadata, input schema, allowed hosts, ordered steps.
- Published versions are immutable; edits create a new version. Jobs pin a specific version.
- Each action has: type identifier, configuration, input validation, execution handler, timeout, result, error handling.
- Validation occurs at save/publish time (schema, selectors present, variables resolvable, hosts allowed) and again before execution.
- No customer-supplied C# or JavaScript is ever executed.

### 5.2 Job orchestration
- Job = workflow version + input dataset + options (concurrency, retries, timeouts).
- Each input row becomes a JobRecord; each execution try is a JobAttempt.
- Statuses: Job {Pending, Running, Completed, CompletedWithErrors, Failed, Cancelled}; Record {Pending, Running, Succeeded, Failed, Cancelled, Skipped}; Attempt {Running, Succeeded, Failed, TimedOut, Cancelled}.
- Durable claiming so pending/interrupted work is recovered after restart.
- Retries only for classified transient errors, with backoff and max attempts.
- Cancellation stops claiming new records and cooperatively aborts running attempts.

### 5.3 Monitoring and traceability
Per attempt capture: JobId, WorkflowId + version, RecordId, attempt number, start/finish, status, structured logs, error details, screenshots, downloads, result. Dashboard eventually shows live status (polling first, push later).

### 5.4 Demo CRM
Fictional local portal: login, customer registration, search, details, duplicate detection, form validation, simulated temporary errors. Fictional data only. First demo: process customers from CSV, submit via browser, capture confirmation IDs, report successes and failures.

### 5.5 Dashboard
Overview, Automation Studio (structured forms + JSON editor), Workflows, Jobs, Execution history, Execution details, Data uploads, Monitoring/logs, Screenshots/artifacts, Integrations, Settings.

## 6. Non-functional requirements
- Security: see ARCHITECTURE.md §9 and CLAUDE.md.
- Reliability: no lost jobs across restarts; at-least-once execution with idempotency guidance for workflow authors.
- Observability: structured logs with correlation IDs (JobId/RecordId/AttemptId).
- Maintainability: clean architecture, tests, documented decisions.
- Cost: no paid dependencies; runs on a developer laptop via Docker Compose.
- Performance target (MVP): N concurrent browser contexts configurable per worker; throughput is bounded by target site, not platform.

## 7. Data model (planned entities)
Organizations, Users, OrganizationMembers, Workflows, WorkflowVersions, WorkflowInputs, Jobs, JobRecords, JobAttempts, ExecutionLogs, ExecutionArtifacts, Integrations, UsageRecords. All customer-owned entities carry `OrganizationId`. Detail in ARCHITECTURE.md §7.

## 8. Success criteria for MVP
1. The CSV demo against Demo CRM runs end to end, with injected temporary errors recovered by retry and duplicates/validation errors reported as record failures.
2. Killing the worker mid-job (including right after a submission committed) and restarting resumes remaining records with **no duplicate customers** in the Demo CRM: stable operation ids, target-side duplicate detection and reconciliation (ARCHITECTURE §5.6).
3. Every attempt has viewable logs, screenshots and results in the dashboard.
4. A new workflow can be created through JSON only, without code changes.
5. A second organization cannot see the first organization's data — proven by cross-tenant tests at the application-service level, with EF query filters both enabled and disabled.
6. Browser traffic to non-authorized destinations is blocked on every path (redirects, subresources, frames, WebSockets, DNS) per ARCHITECTURE §9.1.

## 9. Open questions
See DECISIONS.md "Pending approval".
