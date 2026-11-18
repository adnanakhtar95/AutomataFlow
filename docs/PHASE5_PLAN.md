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
| 7 | **Process-death supervisor** (ADR-059 as amended by ADR-075, §7.5–§7.6.5). Containment unit (one Worker per container; nested user and PID namespaces), whole-unit kill, positive "unit empty" proof, socket drain over the connection ledger (Linux `sock_diag` destroy by exact cookie in the container's namespace, supervisor-owned sockets excluded by inode; Windows tuple + timestamp, never terminates), fence reporting, and tests **P1–P13** on real processes where the environment permits. **Resolved by ADR-075: see below.** | 5, 6 |
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

**Step 7 spike, Option A (namespace containment, user decision 2026-11-18).** Observed on the same pinned image,
kernel 6.18 WSL2, uid 10001, no capabilities, Chromium seccomp profile:
- the container has no `CAP_NET_ADMIN`, not even in its bounding set; the image has no `ip`, `ss`, `slirp4netns` or `pasta`;
- a dedicated network namespace contains only `lo`; a contained client gets "Network is unreachable";
- `setns` is denied (EPERM) by the seccomp profile even to a process that is root in its own user namespace, so the
  supervisor cannot enter the namespace to open a `sock_diag` socket (only fd passing from inside remains);
- mount namespaces are denied ("cannot change root filesystem propagation"), so the Worker sees the container's `/proc`;
- proof of death works: killing the namespace init and reaping its wrapper left **0** tasks in the unit's pid or net
  namespace (scan of `/proc` by namespace inode);
- isolation works: the contained Worker cannot signal the supervisor (ESRCH: not in its pid namespace) or read its memory (EACCES).

**Blocker (reported to the user).** Linking a dedicated network namespace to any network needs `CAP_NET_ADMIN` in the
container's (or host's) network namespace. A userspace forwarder (pasta, slirp4netns, a relay) would end the target's
TCP connection outside the unit and keep buffered bytes after the fence, breaking E2 (P7, P8). So the approved design
is unprivileged for containment and the supervisor's destroy capability, but **not** for the Worker's connectivity to targets.

**Decision (user, 2026-11-19): Option 1, recorded as ADR-075.**
- **Topology:** one Worker per container; Docker provides the container's network namespace and kernel networking.
- **Supervisor:** uid 10002 with `CAP_NET_ADMIN` only.
- **Worker:** nested user and PID namespaces, uid 10001, zero capabilities.
- **Launch step:** a root launcher with `NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT`.

**Gate run before implementation** (exact final posture, uid 10002 with `CAP_NET_ADMIN` only):
- a complete `sock_diag` dump with cookies and inodes;
- a stale-cookie destroy refused, with the replacement surviving (P11b);
- an exact-cookie destroy succeeds and re-enumeration confirms it;
- a real FIN_WAIT1 orphan with 2.5 MB queued to a zero-window peer destroyed, 0 left;
- without the capability: EPERM (P9);
- Chromium tests pass inside the unit with the sandbox on.

**Remaining work:**
- step 7: launcher, supervisor, Worker handshake, the production-posture verification container and the P tests;
- step 8: automatic `ProcessDeathConfirmed` recovery and the restart end to end;
- step 9.

## Acceptance checklist
1. [x] (Step 1, ADR-067: 22 integration tests incl. 8 new database tenant-isolation tests; reflection, source-scan and domain tests.) Persistence model, migrations, constraints; every customer row `IOrganizationOwned`; write guard; reflection test; cross-tenant database tests also with query filters disabled.
2. [x] (Step 2, ADR-068: 11 Api tests; step 8a, ADR-073: the platform-operator policy guards fence confirmation, tested for refusal of tenant users and audited acceptance.) Auth: Identity, JWT, rotating hashed refresh tokens, role policies, platform-operator role, login rate limiting; membership verified server-side.
3. [x] (Step 3, ADR-069: Api lifecycle, publication matrix, hosted-mode restriction, tamper refusal and encrypted secrets are tested; cross-tenant tests run with the backstop both on and off. Step 6: the Worker re-verifies the hash before every attempt; a tampered version is never executed, tested end to end.) Workflows: drafts, publish with stored hash, immutability, hash verified on load and before execution; hosted-mode attestation restriction; encrypted organization secrets.
4. [x] (Step 4, ADR-070: 19 reader unit tests; 10 Api tests incl. 6-way concurrent idempotency, atomic rollback, 1,234-row batching, encryption at rest; cross-tenant with backstop on/off.) CSV/JSON: streaming parser with limits, record-level errors, stable OperationId, batched persistence, idempotent job creation.
5. [x] (Step 5, ADR-071: the shared suite unchanged incl. F8 x1,000 against PostgreSQL; 5 PostgreSQL-specific tests; triggers for derived Blocking and append-only history.) PostgreSQL journal passes the unchanged shared conformance suite plus multi-connection concurrency tests; ambiguous commits never assumed successful.
6. [x] (Step 6, ADR-072: 6 end-to-end tests with the real Worker, Chromium, DemoCrm and PostgreSQL; 13 scheduler tests; lease renewal tests; execution endpoints with cross-tenant 404s. The containment handshake itself is step 7.) Worker: SKIP LOCKED claiming with lease and heartbeat, recovery, concurrency, evaluator-driven scheduling, cancellation, deadlines, completion states, export; logs, artifacts and usage persisted; fail-closed startup.
7. [x] (Step 7, ADR-075, user decision Option 1: one Worker per container; nested user and PID namespaces with zero capabilities; a supervisor with CAP_NET_ADMIN only and no tini; exact-cookie SOCK_DESTROY over sock_diag with supervisor sockets excluded by inode; a behavioural seccomp probe; 10 real-process tests in a production-posture container plus 24 logic tests with fakes; posture refusals and the production image checked manually. P-test status in the matrix below: P12 unavailable, Windows not run.) Supervisor: containment, kill, positive proof, socket drain, P1–P13 on real processes (platform-by-platform status stated explicitly; Windows not claimed unless run).
8. [x] (Step 8, ADR-075: AUTOMATIC recovery with the real production Worker, Chromium and DemoCrm. P1: a frozen Worker after its checkpoint is fenced by ProcessDeathConfirmed and a restarted unit reconciles the late commit. P2: a Worker killed alone is fenced and both tenants' jobs finish without crossing. P6: no supervisor means no fence and NeedsReview. Exact customer and POST counts throughout. Step 8a, ADR-073: done WITHOUT the supervisor: demo CSV end to end through Api + two Worker processes; real SIGKILL K1/K2 with recovery via NoCheckpoint, AwaitingFence → NeedsReview, operator fence + requeue + reconcile; two-Worker concurrency; leak checks. Open: automatic ProcessDeathConfirmed recovery, which needs step 7. Step 8b, ADR-074: job-finalization race fixed with a job-row lock and a lock-then-recheck idle-job sweep; journal completion retried, never assumed recorded without a re-read; runbook.) Real killed-Worker recovery (K1, K2), two-Worker concurrency (K3), CSV end to end through Api + Worker, cancellation, retry of temporary errors, tenant leakage tests.
9. [x] (Step 9, 2026-11-19: two consecutive complete pinned-container runs on commit 7ee2918, each 1,760/1,760 with 0 warnings and 0 errors (UnitTests 1,264; DemoCrm.Tests 157; IntegrationTests 96; AutomationTests 243, including the 10 real-process supervision and recovery tests). Migrations: `has-pending-model-changes` reports none. Production image built; it ran a real Worker under the approved posture, both with `docker run` and as the compose `worker` service (supervisor = PID 1), and stopped gracefully with an accepted fence. No record or browser ran through the Release image. Posture refusals checked manually. Runbook, ADR-075 and roadmap updated.) Runbook, ADRs, roadmap; two consecutive full green runs; diff review; one commit per step.

## Step 7 P-test matrix (honest status, ADR-075)
Real tests run against the production launcher, supervisor and (where stated) the production Worker with real Chromium, inside a second container with the production posture (`scripts/verify-in-container.ps1`).

| Test | Status | How |
|---|---|---|
| P1 frozen Worker | **run** | real Worker + Chromium frozen (`SIGSTOP`) after its checkpoint and POST; a stale heartbeat leads to kill, proof, drain and `ProcessDeathConfirmed`; a restarted unit reconciles (`SupervisedRecoveryTests`) |
| P2 Worker killed alone | **run, adapted** | `SIGKILL` of the Worker process only. Under PID-namespace semantics the kernel kills the orphans when the unit's init exits; the supervisor still proves emptiness (reap + scan) before fencing. Combined with a second organization's job |
| P3 unkillable task | **fakes only** | a scripted unit that never empties gives no fence; the kill is repeated until the deadline |
| P4 handshake fails | **run** | the production Worker outside a unit refuses to start; a supervisor that refuses (P9) makes the Worker refuse |
| P5 host reboot | **journal validation only** | no automatic boot-id fence: `HostId` is not unique per host in containers. Existing conformance tests plus operator `HostRebootedVerified` |
| P6 supervisor unavailable | **run** | supervisor killed, then the unit: no fence, `AwaitingFence`, then `NeedsReview` (`FenceNotConfirmed`); one POST |
| P7 lingering socket (zero window) | **run (Linux)**; Windows not run | an orphan in FIN_WAIT1 with a full send queue is destroyed by cookie; the peer gets a reset, not the queued bytes |
| P8 trickling peer | **run (Linux)**; Windows not run | the dead orphan keeps transmitting until the drain destroys it; nothing arrives after the destroy |
| P9 no destroy capability | **run**; mid-life EPERM by fakes | a supervisor without `CAP_NET_ADMIN` is refused and the Worker never starts; EPERM was observed live in the gate run |
| P10 ledger problems | **run** (corrupt), fakes (missing, torn tail) | |
| P11a other namespace | **run, adapted** | an identical tuple across containers is impossible (different IPs); a connection in another container is shown invisible and untouched |
| P11b exact cookie | **run at every supervisor start** | live probe: a same-tuple replacement survives a stale-cookie destroy |
| P11c broken isolation | **run** | ledgered tuple with another cookie: no fence, nothing destroyed |
| P11d, P11e (Windows) | **not run** | Smart App Control; no Windows supervisor |
| P11f orphan identity after death | **run** | the dead process's orphan kept its ledgered cookie (P7 drained by it) |
| P12 below-socket queueing | **unavailable** | `sch_netem` cannot be loaded and the image has no `tc` |
| P13 fail-closed matrix | **mixed** | real: EPERM, corrupt ledger, identity mismatch, live foreign owner (found Docker's DNS listener, ADR-075). Fakes: interrupted dump, EOPNOTSUPP, silently skipped destroy, partial drain, incomplete scan. Windows rows: fakes only. A supervisor restart mid-drain cannot happen: supervisor death tears the container down (no fence, P6) |

## Resolved observations
- **Step 6 stall (resolved in step 8b, ADR-074):** an end-to-end job stayed `Running` although all its records had finished. Cause: a job-finalization race between concurrent record completions (read committed). Fix: a job-row lock in every finalizing transaction, plus an idle-job sweep. The regression test was shown to fail without the fix.