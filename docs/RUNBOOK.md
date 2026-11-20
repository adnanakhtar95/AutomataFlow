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

The handshake exists only on Linux, inside the Worker container below (ADR-075). Anywhere else, including any
Windows host, the Worker refuses to start by design.

## 3a. The Worker container and its process-death supervisor (ADR-075)
- **One container is one Worker incarnation.** To scale, run more containers; never run more Workers in one container.
  - Start: `docker compose --env-file deploy/.env -f deploy/docker-compose.yml --profile worker up -d --build worker`.
  - Set `PLAYWRIGHT_VERSION` in `deploy/.env` to the pin in `Directory.Packages.props`.
- **Posture (required; the launcher refuses anything else):**
  - **no** `init: true` (tini cannot forward SIGTERM to the supervisor's uid without `CAP_KILL`): the supervisor is PID 1;
  - the container starts as root, used by the launcher only;
  - `cap_drop: ALL` and `cap_add: NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT`;
  - the seccomp profile `deploy/verify/chromium-seccomp.json`.

  Never `--privileged`, never `CAP_SYS_ADMIN`. An over-privileged container is refused, not tolerated. So is a container without a seccomp profile derived from Docker's default: the supervisor tests for one behaviourally at startup (`keyctl` must be denied). That probe cannot tell the approved profile from Docker's own default, but the default would break the Chromium sandbox anyway.
- **What runs:**
  - the supervisor (PID 1; uid 10002, `CAP_NET_ADMIN` only);
  - the unit: its init, the Worker, the Playwright driver and Chromium. All run as uid 10001 with **no** capability, in their own user and PID namespaces.
- **Data volumes** belong to uid 10001 and are owner-only (0700): `/var/lib/automataflow/keys`, shared with the Api, and `/var/lib/automataflow/artifacts`. The supervisor must never be able to read them.
- **What the supervisor does:**
  1. It kills the unit when:
     - the Worker exits or dies;
     - the incarnation's heartbeat is older than `Supervisor:HeartbeatStaleSeconds` (default 45), which means frozen or partitioned;
     - the Worker never registers (`Supervisor:StartupTimeoutSeconds`);
     - or the container is stopped.
  2. It proves the unit gone.
  3. It destroys the dead incarnation's lingering sockets by exact cookie.
  4. It records `ProcessDeathConfirmed`.
  5. It exits, and the restart policy starts a **new** incarnation, which continues every record from the journal.
- **Exit codes:**

  | Code | Meaning |
  |---|---|
  | 0 | fenced, or nothing to fence |
  | 2 | startup refused (log level `Critical`; the Worker was refused too) |
  | 3 | **no fence**: proof or drain failed. The log line `Process-death fence NotFenced <reason>` names the cause; ambiguous sockets are logged with tuple, state and inode |

  After exit code 3, the affected records go `AwaitingFence`, then `NeedsReview`; handle them as in §4 and §5.
- **Stopping:** `docker stop` sends SIGTERM. The Worker cancels its attempts, then the unit is proven gone and fenced.
  - `stop_grace_period` must exceed `Supervisor:GracefulStopSeconds + FenceConfirmTimeoutSeconds + DrainPolls × DrainPollMilliseconds / 1000 + 60` seconds. With the defaults that is 45 + 60 + 60 + 60 = 225 s, and compose sets 4 min. Raise it whenever you raise any of these settings.
  - A shorter grace period SIGKILLs everything without a fence (exit code 3 behaviour).
- **Supervisor lost:** if the supervisor process (PID 1) dies, the container stops. No fence is recorded (P6), so recover through §5.
- **`HostId` is not unique per host in containers** (`/etc/machine-id` comes from the image). Identify a Worker by its incarnation id and container, never by `HostId`.

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
- Automatic process-death fencing exists on Linux in the Worker container only (ADR-075). When it reports no fence, or when the supervisor itself is lost, use §5.
- No Windows supervisor is delivered; a Windows Worker refuses to start. The Windows identity and owner-module table code has never been run (Smart App Control on the development host).
- Below-socket queueing (P12) is not verified: the verification kernel cannot load `sch_netem`. The latency attestation (ADR-057) must cover local queueing.
- The Worker holds the same database credentials as the supervisor. Fence confirmation is validated and audited, but it is not yet restricted to a separate database role (ADR-075).
- Attestations (`targetEnforced`, `authoritative`, `businessKey`) are trusted, not proven.

## 8. Monitoring and alerts (Phase 10)
- **Platform → Operations** (platform operators): ready, running and scheduled records, the age of the oldest waiting
  record, records that need attention (Needs review, Awaiting fence), live Workers with heartbeat age, attempts and
  attempt durations over 24 hours, artifact volume size and free disk. API: `GET /api/v1/platform/operations`.
- **Health:** the Api image's `HEALTHCHECK` probes `/health/ready` (database reachable); the gateway probes `/healthz`.
  `docker compose ps` shows both.
- **Logs:** every service logs to stdout (Serilog); Docker rotates them (20 MB x 5). Ship them to your log system.
- **Alerts** (no paid service is required; poll the operations endpoint or use your log system):
  | Condition | Meaning | Action |
  |---|---|---|
  | `queue.oldestReadyAgeSeconds` > 600 with no live Worker | nothing is processing | check the Worker containers |
  | `queue.needsReview` grows | records the platform will not decide on | §4 |
  | a Worker heartbeat older than 60 s that is not fenced, plus `awaitingFence` > 0 | a Worker died without a fence | §5 |
  | free disk < 15 % | artifacts or database growth | §9, extend storage |
  | Api or gateway unhealthy for 2 checks | outage | `docker compose logs` |
  | log lines `Worker startup refused`, `af-worker-launch: refused`, `Fence confirmation False` | fail-closed refusals | read the reason; never bypass |

## 9. Artifact retention
Screenshots and downloads accumulate (measured size per record: docs/PERFORMANCE.md). Delete those older than N days
(records, results and logs are kept) with a scheduled command, e.g. daily:
```powershell
docker compose --env-file deploy/.env.prod -f deploy/docker-compose.prod.yml run --rm --no-deps api purge-artifacts 90
```
Choose N from your customers' agreements; there is no automatic deletion.

## 10. Backup verification
A backup counts only once it was restored. At least monthly, restore the latest backup into a scratch deployment (a
second project name and port) with `scripts/restore.ps1`, then run `scripts/verify-deployment.ps1` with a smoke job.
The Phase 7 drill (docs/PHASE7_PLAN.md) is the reference procedure: it proved that a job queued before the backup
completes after the restore, which requires the restored key ring and its certificate.
