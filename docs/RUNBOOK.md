# AutomataFlow operations runbook (Phase 5)

This runbook is for operators who run the platform. It covers the rules that the safety guarantees depend on (ADR-056
to ADR-060, ADR-071 to ADR-074), and how to handle records that the platform refuses to decide on by itself.

## 1. Rules the guarantees depend on (non-negotiable)
1. **Never restore or clone a Worker host from a memory snapshot**, and never resume a suspended VM image of a Worker. A
   snapshot can resurrect an incarnation after it was fenced. Its stale browser could then submit again, and its old
   boot id would be wrong. A boot-id proof, or an operator's `HostRebootedVerified`, is valid only under this rule
   (§7.5.6).
2. **Never pause, suspend or live-migrate a Worker host while execution is in progress.** Segments queued below the
   socket layer leave when the host resumes, possibly after a reconcile already observed "not found" (§7.6.7,
   limitation 3). Drain the Worker first (stop it gracefully) and only then do maintenance.
3. **The PostgreSQL server must run with `fsync = on`.** The Worker checks this at startup. Every journal transaction
   forces `synchronous_commit = on` itself.
4. **Keep the journal and the Worker clocks sane.** All journal timestamps are the database server's clock. The
   evaluator allows only a small skew (`Automation:Workflows:ClockSkewSeconds`).

## 2. Required configuration (shared by Api and Worker unless noted)
| Setting | Purpose |
|---|---|
| `ConnectionStrings__Default` | the platform database |
| `DataProtection__KeysDirectory` | **shared**, access-restricted key ring. The Api encrypts secrets and sensitive record values; the Worker decrypts them. Back it up together with the database: losing it makes stored secrets unreadable |
| `Artifacts__RootDirectory` | **shared** artifact volume. The Worker writes, the Api reads through the authorized endpoint only. Never serve it statically |
| `Auth__Jwt__SigningKey` (Api) | at least 256 bits. The Api refuses to start without it |
| `Journal__LeaseSeconds` (default 180), `Journal__LeaseSafetyMarginSeconds` (default 10) | attempt lease. It must cover 120 s plus the margin, or the Worker refuses to start |
| `Worker__*` | concurrency, claim lease, heartbeat (at most a third of the claim lease), fence wait (default 900 s) |
| `Platform__Mode` (Api) | `Hosted` restricts who may publish attestations to Owners |

## 3. Worker startup is fail-closed
A Worker executes nothing unless:
- the journal is durable and fencing;
- the lease covers 120 s plus the margin;
- `fsync` is on;
- the key and artifact directories are configured;
- **a process-death supervisor containment handshake succeeds**;
- its incarnation is registered.

There is no override. A refused start is logged at `Critical` with every reason.

> **Phase 5 status:** the production containment handshake (the process-death supervisor, ADR-059) is not delivered yet
> (docs/PHASE5_PLAN.md, step 7). Until it is, the production Worker refuses to start by design. Only the test-only host
> (`tests/AutomataFlow.WorkerTestHost`) runs Workers, for verification.

## 4. Records the platform will not decide on
| Record state | Meaning | What to do |
|---|---|---|
| `AwaitingFence` | an earlier attempt checkpointed a side effect, and nothing proves it can no longer act (frozen, partitioned or dead Worker) | Wait. A completed late barrier, or a fence, releases it automatically. After `FenceWaitSeconds` it becomes `NeedsReview` |
| `NeedsReview` (`FenceNotConfirmed`) | no proof of death arrived in time | Find out whether the Worker incarnation is gone (§5). Confirm a fence with evidence, then **requeue** the record |
| `NeedsReview` (`AttemptsExhaustedWithOpenAmbiguity`, `ReconcileNeedsReview`, `CancelledWithOpenAmbiguity`, …) | the target's state cannot be established safely | Check the target system manually. If the effect exists, record it there and leave the record. If you are sure it does not exist, requeue (the engine still only repeats with evidence) |

A fence **never** restarts automation by itself. Only `POST /api/v1/jobs/{job}/records/{record}/requeue` (Admin or
Owner) does. After a requeue the Worker re-evaluates the journal: an ambiguous record is reconciled first, and is never
blindly resubmitted.

## 5. Confirming a fence (platform operators only)
1. `GET /api/v1/platform/incarnations`: find the incarnation with unfenced attempts, its host, boot id, unit and last
   heartbeat.
2. Verify **personally** that it can no longer run. Acceptable methods (closed list):
   - `HostPoweredOffOrDestroyed`: the VM or machine is off or deleted in the console;
   - `HostRebootedVerified`: the host's boot id differs from the incarnation's (only valid under rule 1);
   - `DrainVerified`: the unit is empty **and** a supervisor drain report shows every ledgered socket safe.

   "Lease expired", "kill sent", "Worker unresponsive" and "process not found" are **not** evidence.
3. `POST /api/v1/platform/incarnations/{id}/fence` with the method, an evidence reference (ticket, console record or
   command output, 10–1,000 characters) and `attestedPersonally: true`. Every call is audited (`FenceConfirmations`),
   accepted or rejected, with your user id.
4. Requeue the affected `NeedsReview` records (§4).

## 6. Cancellation
`POST /api/v1/jobs/{job}/cancel` cancels records with no journal history at once. Records with history are resolved by
the Worker through the repeat-safety evaluator: `Cancelled` when no side effect can have happened, otherwise
`NeedsReview`. Running attempts stop at the next heartbeat.

## 7. Known limitations (see docs/WORKFLOWS.md §9 and docs/PHASE5_PLAN.md)
- No automatic process-death fencing until the supervisor (step 7) is delivered: dead or frozen Workers need §5.
- Windows supervisor and identity paths are not verified (Smart App Control on the development host).
- Attestations (`targetEnforced`, `authoritative`, `businessKey`) are trusted, not proven.
