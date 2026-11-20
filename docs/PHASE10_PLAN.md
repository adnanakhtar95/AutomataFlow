# Phase 10 — Commercial readiness and release

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). **Complete (2026-11-20)**; see docs/FINAL_PROJECT_COMPLETION_REPORT.md.

## Objective
Operate, measure and document AutomataFlow well enough to deploy it for paying customers, and state precisely what is
and is not ready.

## Scope
- **Operations:** structured logs (already Serilog JSON-capable), health and readiness, Worker health and queue
  visibility for platform operators, execution metrics, artifact retention and disk-usage monitoring, operational alerts
  (log-based, no paid service), backup verification, recovery runbooks.
- **Reliability tests (real processes):** Worker crash, Api restart, database interruption, duplicate requests,
  cancellation, recovery and reconciliation, long-running workflows, concurrent jobs, tenant isolation, storage failure,
  graceful shutdown.
- **Performance measurements** (recorded with environment details, never extrapolated): record throughput, Worker
  CPU/memory, Api latency, database load, browser concurrency, artifact growth.
- **Customer documentation:** installation, onboarding, workflow authoring, operator runbook, troubleshooting, backup
  and recovery, security overview, deployment requirements, known limitations.
- **Commercial packaging:** product description, feature list, supported use cases, deployment options, support model,
  capacity guidance, onboarding process.
- **Final report:** `docs/FINAL_PROJECT_COMPLETION_REPORT.md`.

## Acceptance criteria
docs/FINAL_PROJECT_COMPLETION_REPORT.md states, with evidence levels, what is ready to sell and what remains; every
claim is backed by a recorded test or measurement.

## Delivered
| Area | Delivered | Evidence |
|---|---|---|
| Operations visibility | `GET /api/v1/platform/operations` (queue depth, oldest waiting record, Workers with heartbeat age, 24 h attempt metrics incl. p95, artifact storage and free disk); Operations card on the Platform page | `OperationsTests` (counts move with a real upload; customers refused) |
| Artifact retention | `purge-artifacts <days>` Api command (explicit, scheduled by the operator; rows then files; records kept) | `OperationsTests` (only expired artifacts, every workspace, records kept, minimum 1 day) |
| Monitoring, alerts, backup verification | docs/RUNBOOK.md §8–§10 (health, logs, alert conditions without paid services, monthly restore test) | runbook; Phase 7 drill as reference |
| Reliability on the release stack | database outage (20 s) and Api restart mid-job: 40/40 records each, no duplicate side effect | `scripts/measure-release.ps1`, docs/PERFORMANCE.md |
| Performance | throughput, Worker/Api/PostgreSQL resources, artifact growth, Api latency with environment details | docs/PERFORMANCE.md, docs/measurements/release-measurements.json |
| Customer documentation | README, USER_GUIDE, TROUBLESHOOTING, PRODUCT (features, use cases, deployment options, support model, onboarding), DEPLOYMENT, SECURITY, RUNBOOK, WORKFLOWS, packages/README | documents |
| Final report | docs/FINAL_PROJECT_COMPLETION_REPORT.md | — |