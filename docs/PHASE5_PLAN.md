# Phase 5: Execution platform (background workers and job orchestration)

**Status:** the user authorized Phase 5 on 2026-11-15, to be implemented consecutively without approval between steps.

**Scope sources.** This plan introduces no new architecture. It orders the work that the following binding sources
already specify:
- `ROADMAP.md` Phase 5;
- `ARCHITECTURE.md` §5 (orchestration), §7 (data model, tenant isolation) and §8 (API, authentication);
- `PHASE4_PLAN.md` §7.2–§7.6 (journal, Phase 4/5 boundary, supervisor, socket drain) and the P1–P13 test
  specification;
- ADR-014, ADR-015, ADR-045 to ADR-060 and ADR-066.

Where a step has to make a design choice the documents leave open, the choice is recorded as an ADR (ADR-067 onwards).

## Step breakdown and dependencies

| Step | Content | Depends on |
|---|---|---|
| 1 | **Persistence model and tenant isolation foundation.** EF model for the remaining data-model entities (Workflows, WorkflowVersions, WorkflowInputs, Jobs, JobRecords, JobAttempts, ExecutionLogs, ExecutionArtifacts, Integrations (schema only), UsageRecords), the attempt-journal tables, and organization-scoped secrets. All customer rows are `IOrganizationOwned`, with global query filters as a backstop, a `SaveChanges` write guard for the current organization, constraints and migrations. Reflection and cross-tenant database tests, including runs with the query filters disabled. | — |
| 2 | **Authentication and authorization.** Identity (`AddIdentityCore`), JWT access tokens, hashed rotating refresh tokens, login rate limiting, organization-role policies, a platform-operator role (separate from tenant roles; needed for `HumanConfirmed`), and an `ICurrentUser` implementation that verifies membership server-side on every request. | 1 |
| 3 | **Workflows and secrets API.** Create and edit drafts, publish (`ValidatedWorkflow.CreateForPublication`, stored hash), archive; immutability; hash verification on load. **Hosted-mode publication restriction** on attestations (`targetEnforced`, `authoritative`, `businessKey`). Organization-scoped encrypted secrets (Data Protection), write-only through the API. Cross-tenant tests. | 1, 2 |
| 4 | **CSV/JSON upload and job creation.** Streaming RFC 4180 parser with configurable limits (bytes, rows, columns, cell length, encoding). Record-level validation errors via `RecordValidator`, a stable OperationId per record, batched inserts without holding all records in memory, idempotent job creation (`Idempotency-Key`, unique per organization), and job options (concurrency, retry, timeouts). | 1–3 |
| 5 | **PostgreSQL attempt journal.** `PostgresAttemptJournal` (Infrastructure, `IsDurable` and `SupportsFencing`): unique partial index for in-flight or unfenced entries, conditional checkpoint with lease margin, fencing tokens, lease renewal, the database clock, late completion, audited `ConfirmFenceAsync`, and handling of commits whose outcome is unknown (re-read; never assume success). Must pass the **unchanged shared conformance suite**, plus concurrency tests across connections. | 1 |
| 6 | **Worker execution platform.** Durable claiming (`FOR UPDATE SKIP LOCKED`, lease, heartbeat, recovery); concurrency limits; scheduling from `RepeatSafetyEvaluator` decisions (Normal or Reconcile, backoff, `AwaitingFence` → `NeedsReview` after `FenceWaitSeconds`, never a blind repeat); cancellation and deadlines; record resolution; job completion states and result export; persistence of execution logs, artifacts and usage; authorized artifact endpoint. A **fail-closed Worker startup** (durable fencing journal, lease ≥ 120 s + margin, supervisor configured, incarnation registered, containment handshake). | 3–5 |
| 7 | **Process-death supervisor** (ADR-059, §7.5–§7.6.5). Containment unit, whole-unit kill, positive "unit empty" or boot-id proof, socket drain over the connection ledger (Linux `sock_diag` destroy by exact cookie in the dedicated namespace; Windows tuple + timestamp, never terminates), fence reporting, and tests **P1–P13** on real processes. **Environment conflict: see below.** | 5, 6 |
| 8 | **Real-process recovery and end-to-end proof.** K1/K2 with a **killed Worker process**, K3 with two Worker processes, CSV demo end to end through Api + Worker against DemoCrm, cancellation, retry of injected temporary errors, and leakage tests across tenants for results, logs and artifacts. | 6, 7 |
| 9 | **Completion.** Runbook (no memory-snapshot restore; no pause or live migration during execution), ADRs, roadmap, checklist, two consecutive full runs, diff review. | all |

Each step is verified with targeted tests, architecture tests and the complete pinned-container suite, and committed
and pushed separately.

## Feasibility spike (2026-11-15, pinned image `automataflow-verify:pw-1.64.1`, non-root, Chromium seccomp profile)

| Capability needed by ADR-059 / §7.5 / §7.6.5 | Observed in the verification container |
|---|---|
| cgroup v2 unit: create a sub-unit, write `cgroup.kill`, read `cgroup.events` | `/sys/fs/cgroup` is mounted **read-only** (`mkdir` → EROFS). `cgroup.kill` exists in the kernel (6.18 WSL2) |
| Dedicated network namespace with `CAP_NET_ADMIN` for the supervisor | **Yes**, via unprivileged user namespaces (`unshare --user --net`): full capabilities inside the new namespace; the Worker can run with **zero** capabilities (`--map-current-user`) |
| `SOCK_DESTROY` | kernel `CONFIG_INET_DIAG_DESTROY=y` |
| PID-namespace containment | **Yes:** `unshare --user --pid --net --fork`. When the namespace's init dies, the kernel kills every process in it, orphans included (verified: 0 survivors). Nested user namespaces (as Chromium's sandbox uses) work |
| `setns` into the Worker's namespaces | **Denied** (EPERM). A supervisor-owned netlink socket created inside the namespace is the alternative |
| netem (P12) | `CONFIG_NET_SCH_NETEM=m`, **not loaded**, and cannot be loaded from the container; no `tc` in the image |
| Windows (Job Objects, TCP owner-module tables) | Code can be written, but **cannot be executed** on this host (Smart App Control, ADR-021) |

**Conflict (step 7).** ADR-059 specifies a cgroup v2 unit on Linux. Creating and killing one requires write access
to the cgroup filesystem, and the approved verification environment deliberately does not grant it (non-root, no
`--privileged`, no `CAP_SYS_ADMIN`; CLAUDE.md, ADR-035). Network-namespace isolation and `SOCK_DESTROY` are available
without privileges. The resolution is decided at step 7, where the conflict is reported to the user with options.
Steps 1–6 do not depend on it.

## Acceptance checklist
1. [x] (Step 1, ADR-067: 22 integration tests incl. 8 new database tenant-isolation tests; reflection, source-scan and domain tests.) Persistence model, migrations, constraints; every customer row `IOrganizationOwned`; write guard; reflection test; cross-tenant database tests also with query filters disabled.
2. [x] (Step 2, ADR-068: 11 Api tests; step 8a, ADR-073: the platform-operator policy guards fence confirmation, tested for refusal of tenant users and audited acceptance.) Auth: Identity, JWT, rotating hashed refresh tokens, role policies, platform-operator role, login rate limiting; membership verified server-side.
3. [x] (Step 3, ADR-069: Api lifecycle, publication matrix, hosted-mode restriction, tamper refusal and encrypted secrets are tested; cross-tenant tests run with the backstop both on and off. Step 6: the Worker re-verifies the hash before every attempt; a tampered version is never executed, tested end to end.) Workflows: drafts, publish with stored hash, immutability, hash verified on load and before execution; hosted-mode attestation restriction; encrypted organization secrets.
4. [x] (Step 4, ADR-070: 19 reader unit tests; 10 Api tests incl. 6-way concurrent idempotency, atomic rollback, 1,234-row batching, encryption at rest; cross-tenant with backstop on/off.) CSV/JSON: streaming parser with limits, record-level errors, stable OperationId, batched persistence, idempotent job creation.
5. [x] (Step 5, ADR-071: the shared suite unchanged incl. F8 x1,000 against PostgreSQL; 5 PostgreSQL-specific tests; triggers for derived Blocking and append-only history.) PostgreSQL journal passes the unchanged shared conformance suite plus multi-connection concurrency tests; ambiguous commits never assumed successful.
6. [x] (Step 6, ADR-072: 6 end-to-end tests with the real Worker, Chromium, DemoCrm and PostgreSQL; 13 scheduler tests; lease renewal tests; execution endpoints with cross-tenant 404s. The containment handshake itself is step 7.) Worker: SKIP LOCKED claiming with lease and heartbeat, recovery, concurrency, evaluator-driven scheduling, cancellation, deadlines, completion states, export; logs, artifacts and usage persisted; fail-closed startup.
7. [ ] Supervisor: containment, kill, positive proof, socket drain, P1–P13 on real processes (platform-by-platform status stated explicitly; Windows not claimed unless run).
8. [ ] (Step 8a, ADR-073: done WITHOUT the supervisor: demo CSV end to end through Api + two Worker processes; real SIGKILL K1/K2 with recovery via NoCheckpoint, AwaitingFence → NeedsReview, operator fence + requeue + reconcile; two-Worker concurrency; leak checks. Open: automatic ProcessDeathConfirmed recovery, which needs step 7.) Real killed-Worker recovery (K1, K2), two-Worker concurrency (K3), CSV end to end through Api + Worker, cancellation, retry of temporary errors, tenant leakage tests.
9. [ ] Runbook, ADRs, roadmap; two consecutive full green runs; diff review; one commit per step.

## Open observations
- **Step 6, unexplained stall (not reproduced):** in one full-suite run (2026-11-15), the end-to-end idempotent-retry job stayed `Running` for 3 minutes; that run's AutomationTests project took 6 min 21 s against the usual 3 min 25 s. The test passed alone, in a full AutomationTests run and in the next full-suite run. A 3-minute stall matches waiting out a 180 s attempt lease. The rig now reports record states and Worker logs on any timeout, so a recurrence will show its cause. It must be explained before Phase 5 is declared complete (step 9).
