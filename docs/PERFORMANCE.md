# AutomataFlow — Measured performance and reliability

Measured, not extrapolated. Raw data: `docs/measurements/release-measurements.json`, produced by
`scripts/measure-release.ps1` (release images of the Api, Worker and gateway; DemoCrm test fixture as the target).

## Environment (2026-11-20)
| Item | Value |
|---|---|
| Host | Windows 11 laptop, Docker Desktop 29.8 (WSL2), 12 vCPUs and 7.5 GiB visible to Docker |
| Load | **other unrelated container stacks were running on the same host** during the measurement |
| Stack | 1 Worker container (`Worker:MaxConcurrentAttempts` = 2), 1 Api, gateway, PostgreSQL 18, all on one host |
| Workflow | the shipped CRM customer registration package: sign-in, 9 form steps, submit, confirmation extract, screenshot |
| Job | `maxConcurrency` 4, `maxAttempts` 3; target = DemoCrm (in-memory, same Docker network, no network latency) |

## Results
| Measurement | Result |
|---|---|
| Throughput, 40 records | 40/40 succeeded in 30.8 s = **≈78 records/min** with 2 parallel browser sessions |
| Worker resources (peak while running) | ≈3.3 vCPU (331 % of one CPU), 430 MiB memory |
| Api resources (peak) | 0.2 vCPU, 112 MiB |
| PostgreSQL (peak) | 0.13 vCPU, 73 MiB; database 10 MB after ≈130 records with attempts and logs |
| Artifact growth | ≈52 KB per record (one confirmation screenshot per successful record) |
| Api latency, `GET /api/v1/jobs?take=50` through the gateway, 50 warm requests | p50 10 ms, p95 13.8 ms, max 46 ms |

Interpretation and limits:
- The target here is a local in-memory fixture; real target systems add their own page-load times, which usually
  dominate. Measure your own workflow with the same script before sizing.
- One Worker container's throughput is bounded by `MaxConcurrentAttempts`; scale by adding Worker containers (one
  incarnation each). Multi-Worker scaling was verified functionally (per-job concurrency across two Workers,
  AutomationTests) but its throughput was not measured.
- No long-duration soak test (hours) and no multi-host measurement were run.

## Reliability scenarios on the release stack
| Scenario (40 records each) | Result | Target-side duplicates |
|---|---|---|
| PostgreSQL stopped for 20 s while records were running, then started | job Completed, 40/40 Succeeded, 73.7 s | none: 40 customers, 40 registration POSTs |
| Api container restarted while records were running | job Completed, 40/40 Succeeded, 24.8 s (the Worker does not depend on the Api) | none: 40 customers, 40 POSTs |

Covered by real-process tests elsewhere (AutomationTests, Phases 5–9): Worker killed before/after a side effect
(fencing, reconciliation, NeedsReview), process-death supervision and graceful stop, cancellation, duplicate job
uploads (idempotency key), retries of temporary failures, commit-then-fail targets, tenant isolation, per-job
concurrency across two Workers. Covered by the Phase 7 production drill: backup → wipe → restore with encrypted data,
graceful Worker stop with an accepted fence.

Not run: storage-full behaviour of the artifact volume (documented operator alert at 15 % free disk), host reboot
(P5 proof stays operator-confirmed), network partitions between Worker and target.
