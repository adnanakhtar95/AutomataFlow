# Phase 4 — Workflow Engine and Action Registry: Implementation Plan

Status: **Decisions D1–D7 approved 2026-11-12. Review rounds 1–9 incorporated (round 4: 2026-11-15; round 5, fencing and authoritative evidence: 2026-11-15; round 6, process-death supervisor contract: 2026-11-15; round 7, egress revocation barrier: 2026-11-15; round 8, write gate, horizon proof and socket drain: 2026-11-15; round 9, horizon events and connection identity: 2026-11-15). §16 is the authoritative binding record (ADR-037 to ADR-060). **Final Phase 4 approval: granted by the user on 2026-11-15** on the basis of review round 9; ADR-045 to ADR-060 and all §16 rules are binding. Implementation is authorized and proceeds sequentially by the steps in §15, each step reviewed before the next. Approval of the specification does **not** waive any verification: every safety guarantee counts as implemented only once code and tests demonstrate it, including the platform-specific tests.**
Basis: Phase 3 as committed in `fd942c2`. Real interfaces reused: `IBrowserSessionFactory`, `IBrowserSession`, `ElementSelector`, `InputValue`, `SelectChoice`, `WaitCondition`, `BrowserSessionRequest`, `BrowserOperationException`/`BrowserErrorCode`, `EgressPolicy`/`AllowedHostRule`, `ExecutionScope`, `IExecutionLogSink`, `IArtifactStore`.

**In scope (Phase 4):**
- the workflow engine for one record attempt;
- the repeat-safety evaluator;
- the attempt journal **port** and its contract, with an in-memory implementation for tests;
- the DemoCrm **test-fixture** extensions in §12.3 (approved 2026-11-15).

**Out of scope (Phase 5/6):** database persistence (including the PostgreSQL attempt journal), job scheduling, CSV upload infrastructure, Worker implementation, retry scheduling and back-off, frontend.

**No customer execution in Phase 4 (§7.4, ADR-058):** Phase 4 validates the journal interface and the execution contract with test implementations only. The Worker is not wired to the engine and the Api never references Automation, so no real workflow can run until Phase 5 supplies a durable PostgreSQL journal that passes the shared conformance, crash-recovery, fencing and concurrency tests.

Phase 4 delivers one engine call that executes one workflow version for **one record attempt**. Phase 5 calls it once per record and attempt.

**Precedence:** if any earlier section is ambiguous or appears to conflict with §16, §16 wins.

---

## Terminology (used consistently throughout)
| Term | Meaning |
|---|---|
| **Record** | One input row (CSV/JSON) for one execution; validated against `inputs`. |
| **Operation** | The unit of business work for one record, identified by its stable `OperationId` (ADR-015). Every attempt for a record shares it. |
| **Attempt / execution** | One admitted call of `IWorkflowExecutor.ExecuteAsync` for one operation, with one browser session. Its **intent** is `Normal` (setup + main steps) or `Reconcile` (setup + reconcile steps only). Every attempt is recorded in the attempt journal. |
| **Refusal** | A call of `ExecuteAsync` that was **not admitted** (repeat not authorized, another attempt in flight, journal unavailable, history invalid). A refusal is **not an attempt**: it is never journaled, starts no browser and has no side effect (§7.3). |
| **Attempt journal** | The durable record of all attempts of an operation, and the source of truth for attempt history and side-effect checkpoints (§7.2). Phase 4 defines the port; Phase 5 implements it in PostgreSQL. |
| **Side-effect checkpoint** | A durable journal entry written and acknowledged **before** a side-effect step's handler is invoked (§7.2). |
| **Abandoned attempt** | A started attempt whose lease expired without a completion (Worker crash, kill, freeze, partition). Its side-effect state comes from the journal's checkpoint, not from the lost in-memory result. |
| **Fenced attempt** | An attempt for which it is established that **no further request from its browser can reach any target** (§7.2, ADR-056). Ways to establish it: (a) the executor confirmed egress revocation before completing; (b) the attempt was abandoned **without** a checkpoint; (c) a trusted fencing authority confirmed it (Phase 5 process-death confirmation, Phase 7 isolation, or a recorded human confirmation). |
| **Fence time** | The journal-clock time from which a fenced attempt can no longer dispatch anything. Commit-latency and evidence-delay calculations start here, never from an assumed process timeout. |
| **Unfenced attempt** | An attempt with a side-effect checkpoint that is not (yet) fenced, e.g. a frozen or partitioned Worker. **While any attempt of an operation is unfenced, no other attempt of that operation is admitted** (ADR-056). |
| **Commit-latency contract** | The target-specific, documented upper bound on how long after a request leaves the egress proxy the target can still commit it. Without one, reconciliation evidence cannot be `authoritative` (ADR-057). |
| **Side-effect state** | `NotStarted`, `Started` or `Unknown`. `Started` if a checkpoint exists **or** the result says so. `Unknown` is treated exactly like `Started`. |
| **Terminal outcome** | The single, final outcome of one **attempt** (§7, §16 D3). Exactly one per attempt, never overwritten. |
| **Record resolution** | The final state of a **record** across all its attempts. Decided and persisted by Phase 5 from `RepeatSafetyEvaluator` decisions, never by a single attempt or finding. |
| **Reconciliation finding** | What a reconcile attempt observed through its `reconcileMatch` step: `Found` (exactly one exact match), `NotFound` (positive evidence of no exact match), or `Ambiguous` (several exact matches, or too many rows), which ends `NeedsReview`. A `NotFound` finding is evidence, never a resolution or an authorization by itself (§10). |
| **Side-effect step** | A step declared `"sideEffect": true`, which may change data in the target system (`click`, `upload`). Every `click` and `upload` in `steps` must declare `sideEffect` explicitly (§10.1). |
| **Ambiguous attempt** | A `Normal` attempt whose side-effect state is `Started`/`Unknown` and whose outcome is not `Succeeded` and not a declared permanent **business** failure. The target may or may not have committed. |
| **Safety evidence** | A specific, recorded reason that makes repeating a side effect safe (§10.4). Evidence is bound to the operation id and to the workflow definition snapshot of the attempt that produced it. |
| **Repeat decision** | The result of `RepeatSafetyEvaluator`: what may happen next for an operation (§10.4). |
| **Definition snapshot** | The properties of the workflow version an attempt actually ran, recorded at attempt start: hash, version, timeout, idempotency attestation, operation-id sending, reconciliation policy (§10.4). |
| **Parameter** | A per-environment value supplied by the job configuration (`{{params.x}}`), never by a record. Types `url` and `string`. |
| **Secret** | A value obtained only from `ISecretProvider` via `{{secret.x}}`. |
| **Sensitive input** | A record field declared with `"sensitive": true`. |
| **Sensitive extract** | The output of an `extract` step declared with `"sensitive": true`. |
| **Sensitive value** | Any secret, sensitive input or sensitive extract, and any resolved template that contains one (taint propagation). |
| **Sink** | A place a resolved value is used (a `fill` value, a `select` choice, a condition operand, a URL, a result field, and so on). §5 defines which sinks accept which values. |
| **Step timeout** | A step's `timeoutMs`: one **total** deadline for everything the step does. It fails only that step and never terminates the browser session (§16 D2, ADR-053). |
| **Session-terminating timeout** | The workflow `timeoutSeconds`, the Phase 3 session deadline, or caller cancellation. |
| **Journal clock** | The single clock used for every attempt timestamp (`StartedAt`, `SideEffectCheckpointAt`, `ObservedAt`, `FinishedAt`, `AbandonedAt`, `FencedAt`), for lease expiry and for the evaluator's `now` (§10.5). Phase 5 uses the database server clock. |

---

## 0. Findings from inspecting Phase 3 (gaps this plan must close)
| Gap in Phase 3 (`fd942c2`) | Needed by | Resolution (additive, no new browser abstraction) |
|---|---|---|
| `IBrowserSession` has no file upload | `upload` action | `UploadAsync(ElementSelector, UploadFile, OperationOptions?, ct)` using Playwright `SetInputFilesAsync(FilePayload)`. In-memory content only, never a filesystem path (D2, §5). |
| No non-throwing page probes | `condition` (`elementExists`, `elementVisible`, `urlMatches`), `reconcileMatch` | `CountAsync`, `IsVisibleAsync` (immediate, no auto-wait, never throw for missing elements) and `CurrentUrl` (logged redacted) (D2). |
| No bounded multi-row read | `reconcileMatch` | `QueryRowsAsync(ElementSelector rows, IReadOnlyList<RowValueSpec> values, int maxRows, OperationOptions?, ct)` returns `RowSnapshot(int Count, IReadOnlyList<IReadOnlyList<string?>> Values)`. It uses `Locator.CountAsync` and `Nth(i)` attribute/inner-text reads only, never script evaluation. When `Count > maxRows` it returns the count only. |
| No per-call timeout (operations use the global `ActionSeconds`) | Step `timeoutMs`; `onError: "continue"` | Optional `OperationOptions(TimeSpan? Timeout)` on every operation. When present it is **one total deadline** for the whole operation. `SubmitAsync` currently uses `ActionSeconds` for the click **plus** the navigation timeout for the load wait (`BrowserSession.cs:207–208`); with `OperationOptions` both share the single remaining budget (ADR-053). |
| No way to register a sensitive value except through `FillAsync` | Sensitive extracts must be redacted and their elements masked | `RegisterSensitive(string value, ElementSelector? maskSelector)`: adds the value to the session redactor and, optionally, the element to the screenshot mask set (§5). |
| The pure host/IP policy types live in Automation | Publish-time validation of `allowedHosts` and `url` parameters belongs in Application (the Api must never reference Automation, ADR-017) | Move `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy` and `NormalizedHost` to `AutomataFlow.Application.Network`. Runtime enforcement stays in Automation (D1). |
| `BrowserSessionRequest` policy is built by the caller | Private-network access must never be granted by a request (§16 R3) | Private-network exceptions come only from trusted server configuration (`BrowserEngineOptions.DevelopmentPrivateNetworkOrigins`), honoured only in Development. |
| `NavigateAsync(Uri)` sends `url.AbsoluteUri` (`BrowserSession.cs:164`) | Final URL validation must check what Chromium receives | The final URL is validated on `Uri.AbsoluteUri`, and the built string must round-trip unchanged (§6.1, ADR-046). |
| `EgressSession.Dispose` (`EgressSessions.cs:90–103`) marks the session revoked and closes the tracked **browser-side** sockets, then returns immediately. It is **not** a barrier: an admission race, untracked upstream sockets, queued bytes still written upstream, graceful close, and no wait for handlers (gaps G1–G7, §7.6) | Local fencing of a completed attempt (ADR-056) | **Revocation barrier with an upstream write gate** (§7.6, ADR-060): `EgressSession.RevokeAsync` with atomic admission; every upstream send admitted through `UpstreamWriteGate` under the session lock; waits for admitted sends, then aborts both socket sides; completion of every handler before success; a connection ledger for process-death drains. `IBrowserSession.EgressRevoked` is `true` only after the barrier **completed**; the executor records it as `ExecutionFenced`. |
| Phase 3 never raises `HttpError`; `NavigateAsync` returns the status | `navigate` semantics | The `navigate` step fails with `HttpError` (permanent) for status ≥ 400 unless `config.acceptStatus` lists it (§2). |

All Phase 3 protections stay in force unchanged: every navigation still goes through `RequestGuard`, the egress proxy, fail-closed behaviour, the sandbox, redaction and masking.

---

## 1. Versioned JSON workflow schema (schemaVersion 1)

### 1.1 Shape
```jsonc
{
  "schemaVersion": 1,                       // format version (§11); required, integer
  "name": "democrm-register-customer",      // ^[a-z][a-z0-9-]{2,63}$
  "description": "Registers one customer in DemoCrm.",   // optional, <= 500 chars
  "timeoutSeconds": 120,                    // optional (default 120); 1..3600; at bind time also <= server session maximum (§1.6)
  "parameters": [                           // supplied by the job configuration, never by a record (§16 D7)
    { "name": "crmBaseUrl", "type": "url" },
    { "name": "crmUser", "type": "string" }
  ],
  "allowedHosts": [                         // canonical literal origins and/or url parameters; nothing else is reachable
    "{{params.crmBaseUrl}}"
  ],
  "inputs": [                               // record schema; Phase 5 validates rows against this
    { "name": "email", "type": "email", "required": true, "maxLength": 120 },
    { "name": "company", "type": "string", "required": false },              // maxLength defaults to 1,000
    { "name": "taxId", "type": "string", "required": false, "sensitive": true }
  ],
  "secrets": ["crmPassword"],               // names only; values from ISecretProvider; usable only in fill (§5)
  "maskSelectors": [{ "css": "[data-testid=detail-tax-id]" }],
  "reconciliation": {                       // required if and only if "reconcile" is present (§10.3)
    "evidence": "authoritative",            // required: "advisory" | "authoritative"
    "matchKey": "operationId",              // required: "operationId" | "businessKey"
    "authority": {                          // required for authoritative, forbidden for advisory (§10.4, ADR-057)
      "maxCommitLatencySeconds": 30,        // target's documented commit-latency bound, 1..3600
      "contractReference": "CRM operator runbook §4.2: synchronous commit, 25 s server request deadline, read-your-writes search",   // 10..500 chars
      "maxEvidenceAgeSeconds": 300          // optional, default 300, 30..3600
    }
  },
  "setup":     [ /* run first in every attempt, e.g. login; no side effects */ ],
  "reconcile": [ /* run only in Reconcile attempts; must end with a reconcileMatch step */ ],
  "steps":     [ /* run only in Normal attempts */ ]
}
```

### 1.2 Step shape (all actions)
```jsonc
{
  "id": "submit",                 // ^[a-z][a-z0-9-]{0,39}$, unique across the whole workflow
  "type": "click",                // one of the 13 action types
  "config": { },                  // action-specific, strictly typed (§2)
  "timeoutMs": 15000,             // optional step timeout (total deadline); 100..120000 (side-effect steps: 5000..120000); <= timeoutSeconds * 1000
  "onError": "fail",              // "fail" (default) | "continue" (only on wait, extract, screenshot, fill, select, condition; §9)
  "sideEffect": false,            // REQUIRED on every click and upload in "steps"; must be absent or false in setup/reconcile; forbidden on other types
  "idempotency": {                // optional, only on side-effect steps (§10.1)
    "key": "{{operation.id}}",    // must be exactly the operation id
    "guarantee": "targetEnforced" // the only value: the target deduplicates by this key (author attestation)
  },
  "description": "optional text"
}
```

### 1.3 Selectors (map 1:1 to Phase 3 `ElementSelector`)
`{"testId": "x"}` · `{"css": "x"}` · `{"role": "button", "name": "Sign in"}` · `{"text": "x"}` · `{"label": "x"}` · `{"placeholder": "x"}` · `{"xpath": "//x"}`

- **Templates in selectors:** placeholders are allowed only in `testId`, `text`, `label`, `placeholder` and `name` values. They are never allowed in `css` or `xpath`, so record data cannot alter selector structure.
- **No sensitive values in selectors** (§5).
- **Validation:** the existing `ElementSelector` validation (length ≤ 500, engine prefixes, `>>` chains) is applied at authoring time to literal selectors and again after resolution.

### 1.4 Templates
- **Syntax:** `{{namespace.path}}` only. No expressions, filters, functions, arithmetic or loops.
- **Namespaces:** `record`, `steps`, `operation`, `params` and `secret` (§4). A literal `{{` is a validation error in v1.
- **URL templates** follow a stricter grammar (§6.1).

### 1.5 Formal JSON Schema
A JSON Schema (draft 2020-12) for the editor and tooling ships as `docs/workflow-schema-v1.json`; Appendix A has its outline.
- The **hand-written validator in Application is authoritative** (ADR-016).
- **Rules that exist only in the validator** (JSON Schema cannot express them):
  - sink rules, dominance, per-section outcomes, explicit finalization and overwrite prevention;
  - URL grammar and canonical origins;
  - idempotency prerequisites and reconciliation key consistency;
  - section-dependent `sideEffect` rules and side-effect timeout minimum;
  - step timeout ≤ workflow timeout;
  - condition predicate counts.
- **Parity tests** (ADR-055):
  - the schema's action list, outcome statuses, parameter/input types, evidence levels and match keys equal the code's;
  - **every numeric bound** in the schema equals the corresponding `WorkflowLimits` constant.

### 1.6 Limits — single source of truth: `WorkflowLimits` (Application) (ADR-055)
Every number below is a constant in `WorkflowLimits`. The validator, the runtime checks, the JSON Schema (by parity test) and `docs/WORKFLOWS.md` all use these values. Server-configurable values are marked *server*; a workflow can never exceed them.

| Limit | Value | Enforced by |
|---|---|---|
| Definition size | 262,144 bytes (256 KB), checked before any parsing | parser |
| JSON nesting depth (objects and arrays) | 32 | parser |
| Any JSON string value / any property name (decoded length) | 2,048 / 64 chars | parser |
| Steps (total, including setup, reconcile and branches) | 200 | validator |
| Step nesting depth (condition branches) | 3 | validator |
| Condition expression depth / predicates per expression / children per `all`/`any` | 5 / 20 / 10 (each `if` and each `transientWhen` is one expression) | validator |
| Generic string fields | 2,000 chars | validator, schema |
| Selector values | 500 chars | validator (Phase 3 rule), schema |
| `description` (workflow and step) | 500 chars | validator, schema |
| URL template length / final URL length | 2,048 / 2,048 chars | validator / runtime |
| `urlMatches` / `wait.url` pattern length | 500 chars | validator, schema |
| Inputs / parameters / secrets | 100 / 20 / 20 | validator, schema |
| `allowedHosts` / `maskSelectors` entries | 20 / 50; each `allowedHosts` entry ≤ 300 chars | validator, schema |
| Input `maxLength` | 1..10,000; **default 1,000 when omitted** | validator, `RecordValidator` |
| `string` parameter value | 1..200 chars | binding |
| `url` parameter value | ≤ 300 chars, canonical origin (§6.2) | binding |
| `timeoutSeconds` | 1..3600, default 120; at bind time ≤ *server* `Automation:Browser:Timeouts:SessionSeconds` (default 300) | validator, schema, binding |
| Step `timeoutMs` | 100..120,000; side-effect steps 5,000..120,000; always ≤ `timeoutSeconds × 1000` | validator, schema (range) |
| `delay.milliseconds` | 1..30,000 | validator, schema |
| Extract `maxLength` | 1..10,000, default 1,000 | validator, schema, runtime (`ExtractTooLong`) |
| Result fields | 50 fields; 2,000 chars per resolved value; 32 KB total | validator (count), runtime (`ResultTooLarge`) |
| Upload `maxBytes` | 1..*server* `Uploads.MaxBytes` (default 10 MB, server max 100 MB); default when omitted = server value | validator, runtime |
| Upload `allowedExtensions` | 1..20 entries | validator, schema |
| `reconciliation.authority` | authoritative: required; advisory: forbidden | validator, schema |
| `authority.maxCommitLatencySeconds` | 1..3600, required | validator, schema |
| `authority.contractReference` | 10..500 chars, required, non-template | validator, schema |
| `authority.maxEvidenceAgeSeconds` | 30..3600, default 300 | validator, schema |
| `reconcileMatch.maxRows` | 1..50, default 20 | validator, schema |
| `navigate.acceptStatus` entries | 0..10, each 400..599 | validator, schema |
| *Server* `RepeatSafety:ClockSkewSeconds` | default 5, 0..60 | options validation |
| *Server* `RepeatSafety:MaxReconcileFailures` | default 3, 1..10 | options validation |
| *Server* `RepeatSafety:MaxAmbiguousAttempts` | default 3, 1..10 | options validation |
| *Journal* `LeaseSafetyMarginSeconds` (a checkpoint is accepted only if the remaining lease ≥ step timeout + margin) | default 10, 5..120; the Phase 5 lease length must exceed the longest side-effect step timeout + margin | journal conformance tests |
| *Server* `Egress:RevocationTimeoutSeconds` (revocation barrier completion bound, §7.6) | default 10, 1..60 | options validation |
| *Server* `Egress:UpstreamUserTimeoutSeconds` (Linux `TCP_USER_TIMEOUT` in ms, Windows `TCP_MAXRT` in s; **fail-fast only, never part of a guarantee**, §7.6.4) | default 30, 5..120 | options validation, socket-option test V10 |
| *Proxy* maximum bytes per admitted upstream send (W4) | 16 KB (constant) | write-gate tests |

**No loops and no regular expressions in v1** (§16 D6).

---

## 2. The 13 actions: contracts and classes
Each action has two halves, keeping the Api independent of Automation:

- **Application: `ActionDescriptor`** (static catalog). Type id, config parser and validator, default timeout, declared outputs, capability flags (`SideEffectAllowed`, `ContinueAllowed`, allowed sections). Used for publish-time validation, by the editor (Phase 6) and by the executor.
- **Automation: `IActionHandler`**. Executes against `IBrowserSession` only, never Playwright directly (architecture-tested).

| Type | Config (typed record) | Phase 3 operation used | Outputs (`steps.<id>.*`) | Side effect allowed | `onError: continue` | Sections |
|---|---|---|---|---|---|---|
| `navigate` | `url` (URL template, §6.1), `acceptStatus` (optional list of 4xx/5xx statuses that do not fail) | `NavigateAsync` | `status`, `url` (redacted) | no | **no** | all |
| `click` | `selector`, `waitForNavigation` (bool) | `ClickAsync`, or `SubmitAsync` when waiting | — | **yes** (`steps` only) | **no** | all |
| `fill` | `selector`, `value` (template) | `FillAsync(InputValue.Plain` or `InputValue.Sensitive)` | — | no | yes | all |
| `select` | `selector`, one of `value` / `label` / `index` (template) | `SelectAsync(SelectChoice)` | — | no | yes | all |
| `wait` | `for`: `visible` / `hidden` / `attached` / `detached` + `selector`, or `url` (glob, §8.2), or `load` | `WaitForAsync(WaitCondition)` | — | no | yes | all |
| `extract` | `selector`, `source`: `text` or `attribute` (+ `attribute`), `sensitive` (bool), `maxLength` | `GetTextAsync` / `GetAttributeAsync`; `RegisterSensitive` when sensitive | `value` | no | yes | all |
| `screenshot` | `label`, `fullPage` | `ScreenshotAsync` | `artifactId` | no | yes | all |
| `upload` | `selector`, `file` (template resolving to a `file` input), `allowedExtensions`, `maxBytes` | **new** `UploadAsync` with content from `IExecutionFileProvider` | `fileName` | **yes** (`steps` only) | **no** | `steps` |
| `download` | `trigger` selector | `DownloadAsync` (session created with `AcceptDownloads` when the workflow contains a download step) | `artifactId`, `fileName`, `sha256` | no | **no** | `setup`, `steps` |
| `condition` | `if` (expression, §8), `then` steps (non-empty), `else` steps | **new** `CountAsync` / `IsVisibleAsync` / `CurrentUrl`, plus `GetTextAsync` / `GetAttributeAsync` | `result` (bool) | no (the branches may) | yes (evaluation errors only) | all |
| `delay` | `milliseconds` (1..30000) | none (`Task.Delay` with the execution token) | — | no | n/a | all |
| `captureResult` | `fields` (name → template; no sensitive values), optional `outcome` (§16 D3) | none | — | no | **no** | `setup` (`failed` only), `steps` |
| `reconcileMatch` | `rows` selector, `key` (`{"attribute": "x"}` or `{"text": <child selector>}`), `equals` (template), `ignoreCase`, `emptyIndicator` selector, `maxRows`, `fields` (name → `{"attribute": "x"}` read from the matched row) | **new** `QueryRowsAsync`, `IsVisibleAsync` | — (finding is terminal) | no | **no** | `reconcile` only, last top-level step (§10.3) |

Every step additionally exposes `steps.<id>.failed` (`true`/`false`; §9).

**Why `onError: continue` is restricted (ADR-053):** `navigate`, `click`, `upload` and `download` start browser-side work (a navigation, a form POST, a file transfer) that can still complete **after** the step's timeout has fired. Continuing would let later steps act on a page that is still changing underneath them. Only actions that read, type into or wait on the current page may continue.

**Implementation note (ADR-063):** descriptors parse the strictly parsed immutable `JObject` that is also hashed (not a `JsonElement`), and the typed config is exposed as `WorkflowStep.Action`.

```csharp
// Application (no packages)
public sealed record ActionDescriptor(
    string Type,
    Type ConfigType,
    Func<JsonElement, ActionConfigParseContext, ActionConfig?> Parse,   // strict: unknown properties rejected
    TimeSpan DefaultTimeout,
    IReadOnlyList<string> Outputs,
    bool SideEffectAllowed,
    bool ContinueAllowed,
    WorkflowSections AllowedSections);

public abstract record ActionConfig;                    // one sealed record per action, e.g. FillConfig
public static class ActionCatalog                        // the single list of the 13 descriptors
{
    public static IReadOnlyDictionary<string, ActionDescriptor> All { get; }
}

// Automation
public interface IActionHandler
{
    string Type { get; }
    Task<StepOutcome> ExecuteAsync(StepContext context, ActionConfig config, CancellationToken cancellationToken);
}

public abstract class ActionHandler<TConfig> : IActionHandler where TConfig : ActionConfig
{
    public abstract string Type { get; }
    protected abstract Task<StepOutcome> ExecuteAsync(StepContext context, TConfig config, CancellationToken cancellationToken);
    // the non-generic method casts and delegates
}
```

Handlers: `NavigateHandler`, `ClickHandler`, `FillHandler`, `SelectHandler`, `WaitHandler`, `ExtractHandler`, `ScreenshotHandler`, `UploadHandler`, `DownloadHandler`, `ConditionHandler`, `DelayHandler`, `CaptureResultHandler`, `ReconcileMatchHandler`.

- **`StepContext`** gives a handler the `IBrowserSession`, the `VariableResolver`, a per-step `OperationOptions` (total step deadline), the `ExecutionScope` and an output writer.
- **`StepOutcome`** is a set of outputs, or an `ExecutionError` (§9).

---

## 3. Action registry and dependency injection
- `AddWorkflowEngine()`, a new extension in Automation alongside `AddBrowserEngine()`, registers the 13 handlers as `IActionHandler` singletons. They are stateless; all state lives in `StepContext`.
- It also registers `ActionRegistry`, `WorkflowExecutor` (`IWorkflowExecutor`) and `VariableResolver`. `RepeatSafetyEvaluator` is a pure Application type that needs no registration. The executor takes a `TimeProvider` only for durations; all attempt timestamps come from the journal clock (§10.5).
- **`ActionRegistry` checks at construction, failing startup on any mismatch:**
  - every `ActionCatalog` type has exactly one handler;
  - no handler exists for an unknown type;
  - each handler's config type matches its descriptor.
- **Adding an action** means one descriptor in Application, one handler in Automation, and tests. The executor never changes.
- **Hosts register what Phase 5 needs:**
  - new Application ports: `ISecretProvider`, `IExecutionFileProvider` and **`IAttemptJournal`**;
  - the Phase 3 `IArtifactStore` and `IExecutionLogSink`;
  - `RepeatSafetyPolicy` options.

  Phase 4 tests use in-memory doubles. `InMemoryAttemptJournal` lives in the test projects only, never in production code.
- **No production journal means no execution:** `AddWorkflowEngine` does not register an `IAttemptJournal`. A host without one fails at startup.
- **The Api calls only Application** (`WorkflowDefinitionParser`, `WorkflowDefinitionValidator`, `ActionCatalog`, `RepeatSafetyEvaluator`), never `AddWorkflowEngine`. The architecture test still enforces Api ↛ Automation.

---

## 4. Variable resolution
| Namespace | Source | Example | Rules |
|---|---|---|---|
| `record` | the record, validated against `inputs` | `{{record.email}}` | must be a declared input; a sensitive input yields a sensitive value |
| `steps` | outputs of earlier steps | `{{steps.conf-id.value}}` | the referenced step must **dominate** this use (always executed before it on every path: an earlier step in the same or an enclosing sequence, or in `setup`). Outputs of steps inside a condition branch are visible only later in that same branch. The output name must be declared by that action; a sensitive extract yields a sensitive value |
| `operation` | stable per-record id supplied by the caller (ADR-015) | `{{operation.id}}` | the only key is `id`; rendered as a lowercase hyphenated GUID (`"D"` format); it never changes across attempts |
| `params` | parameters from the job configuration | `{{params.crmBaseUrl}}` | must be declared; usage limited by type (§16 D7) |
| `secret` | `ISecretProvider` at run time | `{{secret.crmPassword}}` | must be declared in `secrets`; **only** allowed in `fill.value` (§5) |

- **Compile once:** at validation time each template is compiled into segments (literal or placeholder). All references are checked statically, so nothing is "discovered" at run time.
- **Run time:** `VariableResolver.Resolve(template, sink, context)` returns a `ResolvedValue(string Text, bool IsSensitive, bool ContainsSecret)`.
  - The sink is part of the call, so the run-time sink check (§5) and the context-aware encoding (§6.1) cannot be skipped.
  - A missing optional input resolves to the empty string unless the sink requires a value. URL path segments and `reconcileMatch.equals` always require a non-empty value. Unexpected misses raise `VariableUnresolved`.
- **Record typing:**
  - Inputs declare `type` (`string` | `email` | `integer` | `decimal` | `boolean` | `date` | `file`), `required`, `maxLength` (default 1,000) and `sensitive`.
  - `RecordValidator` (Application) validates a record before execution. Phase 5 reuses it per record, so no redesign is needed.
  - Values with control characters (other than tab) or invalid UTF-16 (lone surrogates) are rejected (`RecordInvalid`).

---

## 5. Secrets, sensitive values, sinks, results and uploads
- **Secrets:** obtained only from `ISecretProvider` (Application port: `GetAsync(ExecutionScope scope, string name, ct)`).
  - Resolved only when executing and held in memory only for the attempt's lifetime.
  - Never written to step outputs, results, the journal or logs.
  - Phase 5 implements encrypted, organization-scoped storage; Phase 4 uses an in-memory test double.
  - **No secret is ever stored in the workflow JSON.**
- **Taint propagation:** a resolved value is a *sensitive value* if any segment is a secret, a sensitive input or a sensitive extract. `ResolvedValue` carries both `IsSensitive` and `ContainsSecret`, so neither can be dropped silently.

### 5.1 Sink rules (authoritative; enforced by the validator at authoring time **and** again at run time)
| Sink | Secret | Sensitive input / sensitive extract | Non-sensitive value |
|---|---|---|---|
| `fill.value` | **allowed** (`InputValue.Sensitive`) | allowed (`InputValue.Sensitive`) | allowed (`InputValue.Plain`) |
| `select` value / label | rejected | allowed (registered with the redactor) | allowed |
| condition operands (`valueEquals`, `textEquals`, …) | rejected | allowed (only the boolean result is logged) | allowed |
| `navigate.url` | rejected | rejected | allowed (path segment / query value only, encoded; §6.1) |
| `captureResult.fields`, `reconcileMatch.fields` | rejected | rejected | allowed |
| `reconcileMatch.equals` | rejected | rejected | allowed (exactly `{{operation.id}}` or one business-key input; §10.3) |
| selector values | rejected | rejected | allowed (`testId` / `text` / `label` / `placeholder` / `name` only) |
| `screenshot.label` | rejected | rejected | allowed |
| `upload.file` reference | rejected | rejected | allowed (must be a `file` input) |

**Why secrets are `fill`-only:** using a secret in a condition or a selector would let a workflow probe the secret's value through page behaviour, a side channel. Typing it into a field is the only legitimate use.

### 5.2 Sensitive extracts
- **On read:** the extracted value is immediately registered with the session (`RegisterSensitive(value, selector)`). It is redacted from all later messages, and the element it came from is added to the screenshot mask set for the rest of the session.
- **Where it may go:** only into the sinks the matrix allows. It is never written to results, step records, the journal or logs.
- **Length:** an extract longer than `maxLength` fails with `ExtractTooLong`; values are never silently truncated.

### 5.3 Results
- **Content:** result fields (`captureResult.fields`, `reconcileMatch.fields`) are customer data. They are returned in `WorkflowExecutionResult.Results`, persisted organization-scoped by Phase 5, and **never logged**.
- **Restrictions:** they may not contain sensitive values (validator plus run time). Size is limited per §1.6, and exceeding it gives `ResultTooLarge`.
- **Not secret-free:** non-sensitive results can still be personal data. That is why execution events never carry values (§5.5).

### 5.4 Uploads
- **Source:** `IExecutionFileProvider.OpenAsync(ExecutionScope scope, string fileReference, ct)` returns an `ExecutionFile(FileName, ContentType, long SizeBytes, Stream Content)`.
  - The provider must resolve references **within the scope's organization only**; Phase 5 implements this and tests cross-tenant denial.
  - The engine never accepts a filesystem path.
- **Checks before anything reaches the browser**, any failure giving `UploadRejected`:
  1. `SizeBytes` and the actual byte count ≤ `min(step.maxBytes, server Uploads.MaxBytes)`.
  2. The extension of the **sanitized** file name (Phase 3 `FileNameSanitizer`) is in the step's `allowedExtensions`. That list must be a subset of the server's permitted list, which never includes executable or script types.
  3. The declared content type is consistent with the extension (fixed mapping table).
  4. Content sniffing for known types: `%PDF-` for `.pdf`; PNG and JPEG signatures; a ZIP signature for `.docx` / `.xlsx`; valid UTF-8 without NUL bytes for `.csv` / `.txt`. A mismatch is rejected.
  5. The content is read into memory once (bounded by the size limit) and passed as a Playwright `FilePayload`.
- **Ordering with side effects:** all checks run **before** the side-effect checkpoint, so an `UploadRejected` never counts as a started side effect.
- **Logging:** the file name is logged sanitized; content is never logged.

### 5.5 Logging and screenshots
- **Logging:**
  - Step events carry ids, types, durations and error codes, **never input or output values**: even non-secret record values and results are customer data.
  - Every sensitive value is registered with the session's redactor before use, so failure messages stay redacted.
  - `reconcileMatch` logs row counts and the finding, never key values.
- **Screenshots:** masking covers every element filled with a sensitive value, every sensitive-extract element, the Phase 3 built-ins (password and card fields, `[data-af-sensitive]`) and the workflow's `maskSelectors`.
  - **Limitation, stated plainly:** the platform cannot detect or mask every secret a website displays as text. Workflow authors must declare such regions in `maskSelectors`.
  - The validator emits a **warning**, not an error, when a workflow has sensitive extracts or sensitive inputs but declares no `maskSelectors`.

---

## 6. Allowed hosts, URLs and SSRF integration

### 6.1 URL templates, canonicalization and context-aware encoding (§16 R2, ADR-046)
A `navigate.url` template must match this grammar; anything else is a validation error.

```
url-template   = origin-part path-part [ "?" query-part ]          ; no "#fragment"
origin-part    = canonical-origin | "{{params." url-param "}}"     ; scheme://host[:port] only
path-part      = *( "/" segment ) [ "/" ]                          ; optional single trailing "/"
segment        = 1*( pchar-lit | placeholder )
query-part     = pair *( "&" pair )
pair           = key-lit "=" *( qchar-lit | placeholder )          ; keys are always literal; empty value allowed
placeholder    = "{{" namespace "." name "}}"                      ; not secret; not sensitive (§5.1)
pchar-lit      = ALPHA / DIGIT / "-" / "." / "_" / "~" / "!" / "$" / "'" / "(" / ")" / "*" / "+" / "," / ";" / "=" / ":" / "@" / pct-encoded
qchar-lit      = pchar-lit / "/" / "?"  minus "&" "=" "+"           ; "+" must be written %2B in literal query values
key-lit        = 1*( ALPHA / DIGIT / "-" / "." / "_" / "~" )
pct-encoded    = "%" HEXDIG HEXDIG                                 ; must not decode to "/", "\", "." or a control character
```
- **Literal characters are ASCII only.** `\`, space, tab, CR, LF and other control characters are never literal, since browsers strip or reinterpret them. A literal segment equal to `.` or `..` (or a `%2e` form of either) is rejected.
- **Canonical literal origins:**
  - a literal origin in a template or `allowedHosts` must already be canonical, otherwise it is a validation error;
  - canonical means a lowercase scheme, a host normalized by the moved `HostNameNormalizer` (lowercase, IDN as an A-label, no trailing dot), no default port (`:443` for https, `:80` for http) and no path;
  - the platform never silently rewrites a definition.
- **The record never controls scheme, host or port:**
  - Placeholders are forbidden in the origin part, except a whole `{{params.<url parameter>}}`, which is a parameter from the job configuration, never a record.
  - Placeholders are forbidden in query keys and in any fragment.
- **Encoding is by context**, and each value is encoded exactly once, never double-encoded:
  - Path segment placeholder: RFC 3986 segment encoding (`Uri.EscapeDataString`). `/ ? # % \ @ :` and spaces are all percent-encoded, so a value cannot add segments, start a query or change the authority.
  - Query value placeholder: the same encoding (`& = + #` encoded), so a value cannot add parameters. In particular `+` becomes `%2B`, so `name+commit-fail@example.test` cannot be read as a space by the target.
- **Dot-segment protection:** browsers treat `.`, `..`, `%2e` and `%2e%2e` (case-insensitive) as dot segments and normalize them away.
  - A placeholder-bearing segment that resolves (before encoding) to `.` or `..` is rejected (`UnsafeUrlValue`).
  - Encoded `%2e` input becomes `%252e` and is inert, but it is rejected too, for clarity.
- **Empty values:** a placeholder-bearing path segment that resolves to the empty string is rejected (`UnsafeUrlValue`), so `/a/{{x}}/b` can never become `/a//b`. An empty query value (`Term=`) is allowed.
- **Rejected values:** control characters, lone surrogates and NUL (`UnsafeUrlValue`).
- **Final URL validation**, after substitution and before `NavigateAsync`, on the exact string Chromium will receive:
  1. Build the string `s`; parse `u = new Uri(s, UriKind.Absolute)`; require `u.AbsoluteUri == s` (round-trip identical; any normalization by .NET means a mismatch and rejection).
  2. Length ≤ 2,048.
  3. Scheme, host and port are **identical** to the template's bound canonical origin.
  4. No userinfo and no fragment.
  5. **Segment-aware prefix:** the leading literal path segments of the template are equal, segment by segment, to the URL's first segments; the first placeholder-bearing segment starts with its literal prefix.
  6. Matches an `allowedHosts` rule (the moved `AllowedHostRule` parser).

  Any failure gives `UnsafeUrlValue`. The validated `u` is what is passed to `NavigateAsync`.
- **After that,** Phase 3 enforcement applies unchanged: `RequestGuard`, then the egress proxy (DNS resolution, IP policy, pinning, redirect inspection, fail-closed).

### 6.2 Allowed hosts across the lifecycle
1. **Authoring time** (Application validator, using the moved parser, §16 D1):
   - Every `allowedHosts` entry is a canonical literal origin (`https://host[:port]`) or `{{params.<url parameter>}}`.
   - Every `navigate.url` begins with one of those.
   - Wildcards follow the Phase 3 rules (subdomains only).
2. **Binding time** (executor, before the browser starts):
   - **`url` parameter canonicalization:**
     - the value must parse as `scheme://host[:port]` with at most one trailing `/`, and no path, query, fragment or userinfo;
     - it is then canonicalized as in §6.1 (lowercase scheme, normalized host, default port removed, trailing `/` removed);
     - `https` only outside Development, and never a private IP literal outside Development (§16 D7);
     - the canonical value is what is substituted, so `https://CRM.test:443/` + `/Login` gives `https://crm.test/Login`.
   - `timeoutSeconds` ≤ server `SessionSeconds`, else `Failed`/`WorkflowTimeoutExceedsServerLimit` before the browser starts.
   - The `EgressPolicy` is built from the literal origins plus the bound url parameters.
   - **A rule may allow private-network destinations only if** the engine runs in Development **and** the rule's origin exactly matches an entry in the trusted server configuration `Automation:Browser:DevelopmentPrivateNetworkOrigins` (§16 R3).
   - No request field, workflow property, job setting or record value can grant private-network access.
   - The Phase 3 session factory then applies its existing mode rules.
3. **Run time:** every request passes the Phase 3 `RequestGuard` and the authoritative egress proxy. **Phase 4 adds nothing that bypasses them.** A blocked navigation surfaces as `NavigationBlocked` (policy error, §9).
4. **Hosted mode (Phase 5):** an organization-level approved-host list is intersected with the workflow's hosts. The port is reserved, not built.

---

## 7. Execution lifecycle, attempt journal and context

### 7.1 Contracts
```csharp
public interface IWorkflowExecutor   // Automation
{
    Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowExecutionRequest(          // Application (Phase 5 builds one per attempt)
    ExecutionScope Scope,                                  // org, job, record (the attempt number is assigned by the journal)
    Guid OperationId,                                      // stable per record (ADR-015)
    ValidatedWorkflow Workflow,                            // parsed + validated definition, version, verified hash
    IReadOnlyDictionary<string, string?> Record,           // raw record values; validated by RecordValidator
    IReadOnlyDictionary<string, string> Parameters,        // from job configuration only
    ExecutionIntent Intent);                               // Normal | Reconcile
    // Deliberately absent: a private-network flag (§6.2, R3) and caller-supplied attempt history.
    // History comes only from IAttemptJournal (ADR-049), so a caller cannot omit attempts.

public enum ExecutionIntent { Normal, Reconcile }

public enum WorkflowOutcome   // terminal outcome of ONE attempt, or a refusal (§16 D3)
{
    // Normal attempts
    Succeeded, Failed, Indeterminate,
    // Reconcile attempts
    ReconciledFound, ReconciledNotFound, NeedsReview,
    // Either intent
    Cancelled,          // only when no side effect started (Normal) or for any reconcile
    // Not an attempt
    Refused             // admission refused; never journaled; carries the RepeatDecision or refusal reason
}

public sealed record WorkflowExecutionResult(
    WorkflowOutcome Outcome,             // exactly one, set once
    int? AttemptNumber,                  // null for Refused
    ExecutionError? Error,               // code, category, IsTransient, step id, redacted message
    SideEffectState SideEffect,          // Normal attempts: NotStarted | Started (never Unknown in a returned result)
    bool ExecutionFenced,                // true only if the session was disposed and IBrowserSession.EgressRevoked was confirmed
    RepeatDecision? Refusal,             // Refused only
    ReconciliationEvidence? Reconciliation,          // Reconcile attempts only (§10.3)
    IReadOnlyDictionary<string, string> Results,     // captureResult / reconcileMatch fields (never sensitive; never logged)
    IReadOnlyList<StepRecord> Steps,                  // id, type, status, duration, error code (no values)
    IReadOnlyList<ArtifactDescriptor> Artifacts,
    IReadOnlyList<string> Diagnostics,                // e.g. JournalCompletionFailed, cleanup failures (codes only)
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt);
```

### 7.2 The attempt journal (Application port; ADR-049, ADR-056)
```csharp
public interface IAttemptJournal
{
    JournalCapabilities Capabilities { get; }   // IsDurable, SupportsFencing; production requires both (§7.4)

    // Atomically: mark lease-expired Started attempts Abandoned; refuse if the operation has an attempt that is
    // Reserved, Started (lease valid) or UNFENCED (checkpoint present, not fenced); otherwise create a Reserved entry
    // with a fencing token, a lease and the caller's registered WorkerIncarnationId (§7.5).
    // Always returns the full history and the journal clock's "now".
    Task<AttemptReservationResult> ReserveAsync(Guid organizationId, Guid operationId, ExecutionIntent intent, WorkerIncarnationId incarnation, CancellationToken ct);

    // Reserved -> Started, recording the definition snapshot and StartedAt (journal clock). Fenced.
    Task StartAsync(AttemptReservation reservation, AttemptSnapshot snapshot, CancellationToken ct);

    // Durably records "side effect starting at step X" with SideEffectCheckpointAt (journal clock).
    // Atomic conditional write: accepted only if the entry is still Started with this fencing token AND the remaining
    // lease (journal clock) >= stepTimeout + LeaseSafetyMargin. Returns only after the write is durable.
    // The executor invokes the side-effect handler only after this returns successfully.
    Task SideEffectStartingAsync(AttemptReservation reservation, string stepId, TimeSpan stepTimeout, CancellationToken ct);

    // Reconcile attempts: returns the journal clock's ObservedAt, recorded durably, immediately before the reconcile section starts. Fenced.
    Task<DateTimeOffset> MarkObservationAsync(AttemptReservation reservation, CancellationToken ct);

    // Started -> Completed with outcome, error summary, side-effect flag, ExecutionFenced, evidence and FinishedAt.
    // Accepted with a valid lease, AND ALSO after lease loss as a LATE completion of an Abandoned entry. A late completion
    // only adds information: it can fence the attempt (if ExecutionFenced) and record its outcome; it never admits anything.
    Task CompleteAsync(AttemptReservation reservation, AttemptCompletion completion, CancellationToken ct);

    // Reserved -> removed. Used for refusals before StartAsync. A refused reservation never becomes an attempt.
    Task ReleaseAsync(AttemptReservation reservation, CancellationToken ct);

    // Records an external fence confirmation (§7.5). Validates the evidence against the incarnation recorded for the
    // attempt(s); on success fences EVERY unfenced attempt of that Worker incarnation, with FencedAt = journal clock at
    // commit (after the confirmation was observed). Callable only by a trusted fencing authority; Phase 4 defines the
    // contract and evidence types, Phase 5/7 the callers.
    Task<FenceConfirmationResult> ConfirmFenceAsync(FenceEvidence evidence, CancellationToken ct);
}

public enum FenceSource { ExecutorEgressRevoked, NoCheckpoint, ProcessDeathConfirmed, IsolationTerminated, EgressLeaseExpired, HumanConfirmed }
```
**Contract:**
- **Exclusivity (one in flight per operation):** at most one entry per (`OrganizationId`, `OperationId`) is `Reserved`, `Started` with a valid lease, or **unfenced**. Phase 5 enforces this with a unique partial index and the same transaction that marks expired entries `Abandoned`. **An abandoned attempt with a checkpoint keeps blocking the operation until it is fenced.** Lease expiry alone never permits takeover after a checkpoint.
- **Fencing of journal writes:** every call after `ReserveAsync` (except a late `CompleteAsync`) fails with `JournalFenced` if the reservation's lease was lost. Phase 5 renews leases from the Worker heartbeat.
- **Checkpoint atomicity:**
  - the checkpoint write and the abandonment are serialized on the same entry;
  - a Worker that lost its lease **before** its checkpoint committed can never write it, and therefore never invokes the handler;
  - a checkpoint committed **before** lease loss makes the attempt unfenced until fenced.
- **Lease safety margin:** a checkpoint is refused unless the lease, by the journal clock, outlives the step's whole timeout plus `LeaseSafetyMarginSeconds`. A Worker that is not frozen therefore finishes the side-effect step within its lease. This narrows the window to genuine freezes and partitions; it does **not** close it (§7.2 fencing table).
- **Single clock:** every timestamp is assigned by the journal clock, never by the caller.
- **Abandonment:**
  - a `Started` entry whose lease expires becomes `Abandoned` with `AbandonedAt` (journal clock);
  - an expired `Reserved` entry is deleted and was never an attempt.
- **Side-effect state** of an entry:
  - `Started` if a checkpoint exists or the completion says so;
  - `NotStarted` if neither;
  - `Unknown` if the entry cannot be read completely (treated as `Started`).

  Because the handler runs only after the checkpoint is durable, **an abandoned attempt without a checkpoint provably started no side effect** (fence source `NoCheckpoint`).
- **Organization scoping:** the journal is keyed by organization; a history entry for another organization's operation can never be returned (cross-tenant test in Phase 5).

**Fencing: what establishes that an attempt can no longer dispatch (ADR-056)**

| Fence source | Established by | Fence time | Available in |
|---|---|---|---|
| `ExecutorEgressRevoked` | The executor ran the **revocation barrier** to completion (§7.6: admission closed, no upstream byte written afterwards, both socket sides aborted, all handlers finished), so `IBrowserSession.EgressRevoked == true`, then `CompleteAsync(ExecutionFenced: true)`. A late completion counts too. A barrier that timed out gives `ExecutionFenced = false` (unfenced). | `FinishedAt` | Phase 4 contract, Phase 5 |
| `NoCheckpoint` | Abandoned entry without a checkpoint (the checkpoint write is fenced, so the handler can never run) | `AbandonedAt` | Phase 4 contract, Phase 5 |
| `ProcessDeathConfirmed` | The process-death supervisor positively confirms that the attempt's Worker incarnation's **containment unit is empty** (no live process at all: Worker, Playwright driver, every Chromium process, orphans included) **and** that its socket drain succeeded (every ledgered upstream socket is in a non-transmitting state, §7.6.5); or that the host has rebooted since the incarnation started (§7.5). A kill request, an expired lease or a missing PID is **not** a confirmation. | journal time when the confirmation is committed | Phase 5 (required control) |
| `IsolationTerminated` | The Phase 7 orchestrator positively confirms the execution container/sandbox was destroyed (all its processes gone) **and** provides an equivalent socket-drain proof, which Phase 7 must specify and test. Until then, the journal does **not** accept this source (§7.6.6) | journal time when the confirmation is committed | Phase 7 |
| `EgressLeaseExpired` | The Phase 7 **out-of-process** egress proxy binds each session credential to the attempt's lease. After lease expiry (by its own clock, outside the Worker's failure domain) it runs the **revocation barrier** (§7.6) and **reports only after the barrier completed** | journal time when the proxy's revocation report is committed (never the lease expiry itself) | Phase 7 |
| `HumanConfirmed` | A platform operator records verified evidence that the incarnation can no longer run (§7.5 evidence rules) | journal time when the confirmation is committed | Phase 5/6 (platform operator action) |

**Rule for every source:** `FencedAt` is assigned by the journal when the confirmation is committed, which is always **after** the termination or revocation was observed. It is never a predicted, requested or lease-derived time.

**What a frozen Worker can still do (stated plainly):**
- **No in-process check can stop a stale click.** A Worker frozen between a successful checkpoint and the handler (or inside it) will, when it resumes, dispatch the browser click. Its in-process egress proxy is frozen with it and resumes with it. No check inside that process can prevent this, because every check-then-act sequence can be frozen between the check and the act.
- **Why the target can't help:** a third-party target cannot validate our fencing tokens.
- **Phase 4/5 guarantee:** the resumed click is the **only** dispatch for the operation. No other attempt is admitted while the frozen one is unfenced, and evidence observed before its dispatch horizon (§7.6) never counts.
- **Phase 7:** only `EgressLeaseExpired` / `IsolationTerminated` can stop the stale dispatch itself from reaching the target.

### 7.3 Lifecycle
```
ExecuteAsync(request, ct)
 ├─ verify definition hash (§11) ── validate record (RecordValidator)
 │     └─ bad ──► Refused (WorkflowTampered / RecordInvalid); nothing journaled, no browser
 ├─ journal.ReserveAsync(org, operationId, intent)
 │     ├─ another attempt in flight ──► Refused (AttemptInFlight)
 │     ├─ an earlier attempt is unfenced ──► Refused (AwaitingFence); nothing journaled, no browser
 │     └─ journal error ──► Refused (JournalUnavailable)
 ├─ decision = RepeatSafetyEvaluator.Evaluate(workflow, operationId, history, journalNow, policy)
 │     Normal:    decision.Kind != Allowed ──► ReleaseAsync ──► Refused (RepeatNotAuthorized, decision)
 │     Reconcile: workflow has no reconcile section, or decision.Kind ∈ {InFlight, NeedsReview(HistoryInvalid)}
 │                ──► ReleaseAsync ──► Refused
 ├─ bind parameters (canonical origins) ─► timeout vs server limit ─► EgressPolicy (private network only via trusted Development config)
 │     └─ failure ──► ReleaseAsync ──► Refused (PolicyViolation / WorkflowTimeoutExceedsServerLimit)
 ├─ journal.StartAsync(reservation, snapshot)        ── failure ──► ReleaseAsync (best effort) ──► Refused (JournalUnavailable)
 │     [from here on, this is an attempt with exactly one terminal outcome]
 ├─ resolve secrets (ISecretProvider)                [values in memory only]  ── failure ──► Failed (SecretUnavailable)
 ├─ IBrowserSessionFactory.CreateAsync(scope+attempt, policy, maskSelectors, acceptDownloads)
 ├─ log WorkflowStarted (workflow id/version/hash, operation id, intent, attempt)
 ├─ run setup steps (no side effects)
 ├─ Intent = Reconcile ─► journal.MarkObservationAsync ─► ObservedAt
 │                     ─► run reconcile steps; the final reconcileMatch sets the terminal outcome
 │                        ReconciledFound | ReconciledNotFound | NeedsReview
 │                     (errors ─► Failed; cancellation ─► Cancelled; reconcile NEVER continues into main steps)
 ├─ Intent = Normal    ─► run main steps
 │    for each step: resolve config (sink rules, URL grammar) ─► pre-checks (upload checks, URL validation)
 │       side-effect step ─► await journal.SideEffectStartingAsync(stepId, stepTimeout)
 │                            ├─ failure/fenced/lease too short ─► handler NOT invoked ─► terminal Failed (CheckpointFailed, transient)
 │                            └─ ok ─► SideEffect = Started ─► invoke handler (total step deadline)
 │       success ─► record outputs ─► next
 │       error   ─► classify (§9) ─► onError=continue (allowed types only)? next : terminal ─► stop
 │       condition ─► evaluate ─► run then/else sequence (same rules, nested)
 │       terminal captureResult ─► terminal per §9 ─► stop
 │    (validator guarantees every path ends in a terminal captureResult: no implicit success)
 ├─ cancellation / session-terminating timeout ─► Normal: Indeterminate if SideEffect = Started, else Cancelled; Reconcile: Cancelled
 ├─ log WorkflowCompleted (outcome, error code, duration)
 └─ finally: dispose session (Phase 3 cleanup: context close, proxy credentials revoked, proxy connections closed),
             ExecutionFenced = session.EgressRevoked, clear secret values,
             journal.CompleteAsync(outcome, ExecutionFenced, …)
                 ── lease lost ──► accepted as a LATE completion (adds outcome and fence information only)
                 ── failure    ──► diagnostic JournalCompletionFailed; the entry becomes Abandoned at lease expiry;
                                   with a checkpoint it stays UNFENCED and blocks the operation until fenced
             (cleanup and completion failures never change the terminal outcome)
```

- **Steps run strictly sequentially in one session per attempt.** No parallelism inside a workflow.
- **A step timeout never ends the session** (§16 D2). On a side-effect step, however, it ends the attempt as `Indeterminate` (§9).
- **The executor never trusts a caller for history:** it evaluates the journal's history, read in the same atomic reservation that guarantees no concurrent attempt.
- **Phase 5 integration:**
  - Phase 5 implements `IAttemptJournal` in PostgreSQL;
  - it calls `RepeatSafetyEvaluator` on the same history to schedule the next attempt (Normal or Reconcile, and when);
  - it persists results and decides record resolution.

  The engine needs no redesign.

### 7.4 Journal guarantees: the Phase 4 / Phase 5 boundary (ADR-058)
| | Phase 4 | Phase 5 |
|---|---|---|
| What is delivered | `IAttemptJournal` contract, `JournalCapabilities`, executor integration, evaluator | `PostgresAttemptJournal` (Infrastructure): unique in-flight/unfenced constraint, conditional checkpoint writes, fencing tokens, lease renewal, database clock, late completion, `ConfirmFenceAsync` with audit |
| Implementations | `InMemoryAttemptJournal` and fault-injecting journals, **in test projects only** (`IsDurable = false`) | the PostgreSQL journal (`IsDurable = true`, `SupportsFencing = true`) |
| Proof | the **shared journal conformance suite** (`tests/Shared/AttemptJournalConformance/`, compiled into the test projects; no new project) runs against the in-memory journal, plus executor, crash and concurrency tests with test doubles | the **same conformance suite** plus crash-recovery (K1, K2 with a real killed Worker process), fencing (F1–F15), supervisor (P1–P13), revocation barrier and write gate (V1–V12, WG1–WG6), horizon race (H1–H4) and concurrency (K3, with two Worker processes) tests run against PostgreSQL and real processes, and must pass before any customer execution |
| Customer execution | **impossible**: the Worker is not wired to the engine, and the Api never references Automation | allowed only through a Worker whose startup validation passes (below) |

**Fail-closed rules:**
1. **No real customer execution may rely on an in-memory or non-durable journal.**
   - `InMemoryAttemptJournal` exists only in test assemblies; an architecture test asserts that no production assembly contains an `IAttemptJournal` implementation other than the Phase 5 PostgreSQL one.
   - `AddWorkflowEngine` registers no journal.
2. **A production Worker fails closed at startup** (Phase 5 `ValidateOnStart`, in every environment including Development) if:
   - no `IAttemptJournal` is registered;
   - `Capabilities.IsDurable` or `Capabilities.SupportsFencing` is false;
   - the journal's lease length is shorter than the longest permitted side-effect timeout (120 s) plus `LeaseSafetyMarginSeconds`;
   - the process-death fence supervisor (`ProcessDeathConfirmed`) is not configured;
   - the Worker cannot register its incarnation, or the supervisor's containment handshake (§7.5.2) fails.

   There is no override flag.
3. **At run time** the executor refuses to start any attempt (`Refused`/`JournalUnavailable`) if the journal is unreachable, and never invokes a side-effect handler without a successful checkpoint (§7.3).

### 7.5 Process-death supervisor and fence-confirmation contract (ADR-059)
**Phase split:**
- **Phase 4** defines this contract, the `WorkerIncarnation` / `FenceEvidence` types, the journal's validation of evidence, and tests with a fake supervisor.
- **Phase 5** implements the supervisor and proves it against real processes.

**1. Identifying the processes that belong to an attempt**
- **Worker incarnation:** every Worker process registers a **Worker incarnation** in the journal at startup, before it may reserve anything:

  ```csharp
  public sealed record WorkerIncarnation(
      WorkerIncarnationId Id,          // random GUID generated at process start, never reused
      string HostId,                   // stable machine identity
      string BootId,                   // Linux /proc/sys/kernel/random/boot_id; Windows: last boot time
      int ProcessId, DateTimeOffset ProcessStartTime,   // diagnostic and cross-check only, never sufficient alone
      ContainmentUnit Unit,            // Linux cgroup v2 path (container or systemd scope) | Windows Job Object name
      string ConnectionLedger);        // path of the incarnation's upstream connection ledger (§7.6.5), read by the supervisor's drain
  ```
- **Attempt binding:** every attempt is bound to exactly one incarnation (`ReserveAsync` records it).
- **Containment unit:** all processes that can act for that attempt run inside the incarnation's **containment unit**:
  - the Worker process itself, which also hosts the attempt's egress proxy;
  - the Playwright driver process;
  - every Chromium process (browser, renderers, GPU/utility, zygote/sandbox helpers).

  This holds whether the browser is shared (Development/SelfHosted) or per session (hosted), because all of them are descendants launched by the Worker.
- **Scope of a fence:** the containment unit is exclusive to one incarnation. A fence therefore always covers **every** attempt of that incarnation; the supervisor never tries to fence a single attempt inside a live Worker.
- **Topology restriction:** `ProcessDeathConfirmed` is valid only when the browser and the egress proxy run inside the Worker's containment unit. Any other topology (remote browser, out-of-process proxy) must use the Phase 7 sources.

**2. Containment requirements, verified before the Worker may execute (Phase 5 startup handshake; otherwise the Worker fails closed, §7.4)**

| Platform | Containment unit | Requirements the supervisor verifies |
|---|---|---|
| Linux | a dedicated cgroup v2 unit (the Worker's container cgroup or a systemd scope) | the Worker PID is in the unit; the Worker runs non-root without `CAP_SYS_ADMIN` and cannot write `cgroup.procs` outside its unit, so no descendant can leave it; the supervisor can read `cgroup.events` and write `cgroup.kill` for that unit only; the Worker runs in a **dedicated network namespace** (not the host's, not shared with any other incarnation or workload), verified at the handshake; the supervisor holds a handle to it; its `SOCK_DESTROY` capability (`CAP_NET_ADMIN` in that namespace, kernel support, destroy by exact cookie) is proven by a probe in a scratch namespace on the same kernel; the ledger directory is readable by the supervisor |
| Windows | a Job Object created by the supervisor, which launches the Worker into it | breakaway is disallowed (no `JOB_OBJECT_LIMIT_BREAKAWAY_OK` / `SILENT_BREAKAWAY_OK`); `KILL_ON_JOB_CLOSE` is set; the supervisor holds the job handle; Chromium's own sandbox jobs nest inside it; the supervisor can enumerate the complete IPv4 and IPv6 owner-module TCP tables (proven at the handshake); it **never** terminates connections (no `SetTcpEntry`, §7.6.5); the ledger directory is readable by the supervisor |

**3. Positive termination confirmation.** The supervisor (a separate process on the same host, outside the containment unit, never frozen with it by design) acts when an incarnation's heartbeat is stale and it has abandoned attempts:
1. **Record the kill request** in the journal (diagnostic only; it fences nothing).
2. **Kill the whole unit:** Linux `cgroup.kill` (on kernels without it, repeated `SIGKILL` of every PID in `cgroup.procs` until empty); Windows `TerminateJobObject`. `SIGKILL`/termination also works on a stopped (frozen) process.
3. **Wait for positive proof** within `FenceConfirmTimeoutSeconds` (Phase 5 option, default 60):
   - Linux: the kernel reports `populated 0` in the unit's `cgroup.events`, i.e. no live task remains, including orphans re-parented away from the Worker;
   - Windows: the job's accounting reports `ActiveProcesses == 0`.
4. **Drain upstream sockets** (§7.6.5): read the connection ledger, then:
   - **Linux:** enumerate every TCP socket in the incarnation's dedicated namespace, destroy each transmit-capable one by exact cookie, and re-enumerate until all are safe;
   - **Windows:** match each ledger entry by tuple **and** creation timestamp, never terminate anything, and poll until every entry is drained.

   Any failure, incompleteness or ambiguity means no fence (fail-closed table, §7.6.5).
5. **Report** `ConfirmFenceAsync(FenceEvidence(ProcessDeathConfirmed, incarnation, unit, method, observedAt, drainReport))`.
6. **Journal validation:** the journal checks that the incarnation and unit match the attempts' recorded incarnation, then commits. `FencedAt` = journal time at commit.

**Alternative proof: host reboot.** The supervisor may also confirm by **host reboot**: the host's current `BootId` differs from the incarnation's recorded `BootId`, which means every process **and every kernel socket** of the previous boot is gone, so no drain is needed. This is valid only together with the operational rule in §7.5.6 (no restore or clone from memory snapshots).

**Not confirmations:**
- a kill request or signal delivery;
- an expired lease or a stale heartbeat;
- "PID not found" or a PID reused by another process;
- the Worker process alone being gone while the unit still contains processes;
- a container "stopping" state;
- a timeout.

If proof is not obtained (the unit never empties, a process is stuck in uninterruptible sleep, the ledger is unreadable, enumeration is incomplete, connection ownership is ambiguous, a transmit-capable socket cannot be destroyed or does not reach a safe state, the drain is partial, the supervisor or host is unreachable or itself frozen, or the containment handshake was never completed), **no fence is recorded**. The attempts stay `AwaitingFence` and become `NeedsReview` after `FenceWaitSeconds`. They are **never retried automatically**.

**4. Child processes that survive their parent**
- **Orphans stay in the unit:** an orphaned Chromium process (whose Worker or driver died) is re-parented by the OS but **remains in the cgroup / Job Object**, because membership is inherited and cannot be left without privileges the Worker does not have.
- **Proof covers them:** "unit empty" therefore proves that every orphan is gone too; the supervisor's kill in step 2 targets the unit, not a process tree, so orphans are killed with everything else.
- **Defence in depth, not the proof:** even before it is killed, an orphan cannot reach a target:
  - Chromium resolves no names and connects only to the egress proxy (ADR-036);
  - the proxy dies with the Worker process;
  - a replacement proxy never accepts old credentials (rule 5 below).

  This is never used as the confirmation itself.

**5. When proxy credentials and connections become unusable**

| Situation | Credentials unusable | Existing connections unusable |
|---|---|---|
| Live Worker, attempt completes | at step 1 of the revocation barrier (§7.6), which runs first in `DisposeAsync`, before the context is closed | browser-side and upstream sockets aborted (RST, unsent data discarded) at barrier step 3; `EgressRevoked` only after every handler has finished (step 4) |
| Worker frozen or partitioned (alive) | **still usable**; the in-process proxy resumes with the process | still usable; **this is why such attempts stay unfenced** |
| Worker incarnation killed | when the process hosting the proxy no longer exists. Credentials exist only in that process's memory (random per session, never persisted, logged or shared; Phase 3 rule, reaffirmed) | **no barrier runs**. A socket closed by process exit "always lingers in the background" (`socket(7)`), so data already in an upstream send buffer may still be transmitted after death, and no OS timeout bounds that (§7.6.4). The fence therefore requires the supervisor's socket drain (§7.6.5): every upstream connection of the incarnation, identified by OS identity (Linux: dedicated namespace and cookie; Windows: tuple and creation timestamp), is proven to be in a state where TCP originates nothing more. This proves E2 (connection destruction), not E3 (physical departure of already-queued segments, §7.6.6). H is `FencedAt` |
| Phase 7 out-of-process proxy | at lease expiry, by the proxy's clock | the proxy runs the same barrier (§7.6) and sends the revocation report only after it completed |

A newly started proxy (any incarnation) generates fresh credentials and has no way to accept credentials from another incarnation. A conformance test asserts that no proxy state is persisted.

**6. Audited human fence confirmation (`HumanConfirmed`)**
- **Who:** only a **platform operator** role (Workers are platform infrastructure; tenants can never confirm fences), through an authenticated, audited action (Phase 5/6).
- **Required evidence**, all mandatory:
  - the incarnation id (shown with its `HostId`, `BootId`, unit and last heartbeat);
  - a **method** from a closed list:
    - `HostPoweredOffOrDestroyed`: the VM or machine is confirmed off or deleted in the hypervisor or cloud console;
    - `HostRebootedVerified`: the boot id has changed, checked by the operator (no kernel sockets survive a reboot);
    - `DrainVerified`: the unit is verified empty **and** a supervisor drain report (§7.6.5), run by the operator with the supervisor tool, shows every ledgered upstream socket in a safe state. A container or unit merely shown as removed, without a drain report, is **not** an acceptable method, because lingering kernel sockets may still transmit.
  - an evidence reference (ticket, console record or command output; 10..1,000 characters);
  - an explicit attestation that the operator checked the evidence personally.
- **Not acceptable:** "lease expired", "kill sent", "Worker unresponsive" or "probably dead". The action offers no such method.
- **Recording:** the actor's user id, method, reference and attestation are stored with the fence. `FencedAt` is the journal time when the confirmation is committed.
- **Hosted deployments (Phase 6/7):** a second operator approval may be required by configuration.
- **Operational rule:** Worker hosts are never restored or cloned from memory snapshots, because a snapshot could resurrect an incarnation after its fence. This is documented in the Phase 5 runbook and is a precondition for `HostRebootedVerified` and the boot-id proof.

**7. Timestamps and replacement attempts**
- **Timestamps:**
  - `FencedAt` is written only by `ConfirmFenceAsync` (or a fenced completion), at commit, after the confirmation was observed;
  - the kill-request time, lease expiry and supervisor-observed time are stored as diagnostics only and are never used as a fence time;
  - all commit-latency waits count from `FencedAt` (§10.5).
- **Replacement attempts:**
  - until `FencedAt` exists, `ReserveAsync` refuses every new attempt for the operation (Normal and Reconcile), and the evaluator returns `AwaitingFence` (S1b);
  - a confirmation is rejected if its incarnation or unit does not match the attempt's recorded incarnation, or if it names a method that is not on the closed list;
  - a fence arriving **after** the record moved to `NeedsReview` does not restart automation by itself: an operator must explicitly requeue the record, and the evaluator then applies its normal rules from the dispatch horizon (§7.6).

**8. A fence is not evidence that a request failed.**
- **What a fence means:** *nothing further* can be dispatched by that execution. It says nothing about requests that were already dispatched; a request sent before the Worker died may have committed, or may still commit within the target's commit latency.
- **The attempt stays ambiguous:** a process-death (or any other) fence never changes an attempt's side-effect state or outcome; an abandoned attempt with a checkpoint remains ambiguous after it is fenced.
- **Resolution rules are unchanged:** it can be resolved only by rules A1–A8 (`Found`, target-enforced idempotency, an authoritative `NotFound` observed after the dispatch horizon plus the commit latency, or `NeedsReview`). The commit-latency contract (ADR-057) and the reconciliation rules (ADR-052) are unchanged.

### 7.6 Egress revocation barrier, upstream write gate and dispatch horizon (ADR-060)
**Findings.** Phase 3 `EgressSession.Dispose` (`fd942c2`) is a credential revocation, not a barrier (G1–G6). The round-7 proposal had a further gap (G7):

| Gap | Behaviour | Violates |
|---|---|---|
| G1 admission race | `Authenticate` → `TryOpenConnection` → `Track` (`EgressProxyServer.cs:232–247`) is not atomic with revocation; a connection authenticated just before `Dispose` and tracked just after is never closed | new requests rejected |
| G2 upstream not tracked | only the browser-side `TcpClient` is tracked; the proxy→destination socket closes only when the handler unwinds, after `Dispose` returned | all connections closed |
| G3 queued bytes | handlers are not cancelled; an already-read plain-HTTP request (head and possibly the whole body in `leftover`) is still connected and written upstream (`:364`, `:401–402`); a tunnel pump that already read a buffer still writes it (`:447`) | queued requests not dispatched |
| G4 graceful close | disposing a `NetworkStream` closes gracefully; the kernel keeps transmitting unsent send-buffer data after close | queued requests not dispatched |
| G5 no completion | `Dispose` returns without waiting for handlers | fence only after completion |
| G6 unsynchronized flag | `IsRevoked` is a plain field | new requests rejected |
| **G7 check-then-write (round-7 proposal)** | "check `RevocationToken`, then write" is two steps. A handler can pass the check, pause, let revocation cancel the token, then resume and write. Passing the token to `WriteAsync` does not close the gap: whether a cancelled token is observed before the send syscall is itself a race, and an issued send cannot be recalled | no byte written after revocation begins |

**Already correct:**
- **Plain HTTP:** each browser→proxy connection carries exactly one request; it is authenticated, forwarded with `Connection: close` and then closed (`:389–403`, `:259`), so there is no browser→proxy keep-alive reuse.
- **The keep-alive case is the CONNECT tunnel:** after one authentication it carries any number of HTTPS keep-alive requests, HTTP/2 streams and WebSocket frames.

#### 7.6.1 The upstream write gate: the synchronization contract
One `UpstreamWriteGate` per egress session. Its lock is the **same lock** that guards session admission (`TryAdmit`).
```csharp
internal sealed class UpstreamWriteGate
{
    // All three members take the session lock; there is no lock-free path.
    public bool TryEnter();      // lock { if (revoked) return false; admitted++; return true; }
    public void Exit();          // lock { admitted--; if (revoked && admitted == 0) drained.TrySetResult(); }
    public Task CloseAsync();    // lock { revoked = true; if (admitted == 0) drained.TrySetResult(); }  returns drained
}

internal sealed class UpstreamChannel   // the ONLY type that may touch an upstream socket for sending
{
    // Each payload write: if (!gate.TryEnter()) -> discard the buffer, mark the connection for abort, return false;
    // try { await socket.SendAsync(buffer /* <= 16 KB, no cancellation token */) } finally { gate.Exit(); }
    public ValueTask<bool> WriteAsync(ReadOnlyMemory<byte> buffer);
    // Creating an upstream connection: if (!gate.TryEnter()) -> return null;
    // try { connect; record tuple + OS identity in the connection ledger (§7.6.5, fsync); TrackUpstream } finally { gate.Exit(); }
    public ValueTask<UpstreamChannel?> OpenAsync(IPEndPoint endpoint);
}
```
**Rules:**
- **W1, single linearization point:** `TryEnter` under the session lock is the only admission check for any operation that creates an upstream connection or puts payload bytes into an upstream socket (the forwarded head, every body chunk, every tunnel pump write). There is no separate "check, then write".
- **W2, no write outside the gate:**
  - every upstream send syscall is issued inside an admission (between a successful `TryEnter` and its `Exit`);
  - `UpstreamChannel` is the only code that sends on an upstream socket, which a source-scan test enforces: no `Write*`/`Send*` on an upstream `Stream`/`Socket` elsewhere in `Network/Proxy`.
- **W3, no cancellation inside an admission:** an admitted send is not cancelled. It either completes, fails because the socket was aborted, or is still outstanding when the barrier times out, which gives `TimedOut` and therefore no fence. The revocation token only wakes reads, DNS and waits; it is **never** a safety mechanism.
- **W4, bounded writes:** each admitted send is ≤ 16 KB. A write that blocks on a full send buffer holds its admission, and the barrier waits for it only up to `RevocationTimeoutSeconds`.
- **W5, ledger before payload:** a connection's tuple **and OS identity** (Linux `SO_COOKIE`; Windows `liCreateTimestamp`) are durably in the connection ledger before any payload admission on it; a connection whose identity cannot be read is aborted unused (§7.6.5).

#### 7.6.2 Barrier ordering (`EgressSession.RevokeAsync`)
| Step | Action | Guarantee after the step |
|---|---|---|
| B1, the linearization point **Lr** | under the session lock: `revoked = true` (closes `TryAdmit` and `TryEnter` at once), remove the credentials from the registry, cancel `RevocationToken` | no new connection, request, upstream connection or payload write can be admitted. **Revocation has begun.** |
| B2 | await the gate's `drained`: every write admitted **before** Lr has returned (bounded by `RevocationTimeoutSeconds`, default 10) | no send syscall is executing or can start on any upstream socket of the session |
| B3, abort time **Ta** | cancel pending reads; close every upstream socket **abortively** (linger on, timeout 0) and every browser-side socket | **E2 (connection destruction):** each upstream TCP connection's send queue is discarded and TCP originates no further segment or retransmission for it (verified per OS, §7.6.4), including established keep-alive CONNECT tunnels. Segments already handed **below** the socket layer before Ta are **not** covered (E3, §7.6.6) |
| B4 | await every handler task registered to the session (same overall timeout) | nothing of the session is still running |
| B5 | return `Completed` if B2–B4 finished in time, otherwise `TimedOut` | — |

**After the barrier:**
- `IBrowserSession.EgressRevoked = (result == Completed)`;
- the executor then calls `CompleteAsync(ExecutionFenced: EgressRevoked)`, which assigns `FencedAt` at commit;
- `BrowserSession.DisposeAsync` runs the barrier **before** closing the browser context;
- `Dispose()` (no waiting) remains for non-execution paths and never establishes a fence;
- the Phase 7 out-of-process proxy runs the same barrier before sending its revocation report.
- **Upstream sockets are only ever closed abortively,** including at the normal end of a connection (Step 2 follow-up, audit defect D1). A graceful close by a connection handler could run before B3, because B1 cancels the handler's reads so it unwinds. The kernel would then keep sending and retransmitting queued data after Ta, and B3's abort of an already-closed socket does nothing. The proxy-level regression test sends 128 KB queued behind a non-reading peer. It fails on the old code (all bytes delivered, FIN) and passes on the fix (fewer bytes, RST).

#### 7.6.3 The review-round-8 interleaving, resolved
The handler's "check" is now `TryEnter`, which runs under the same lock as B1. The two operations are totally ordered:

| Order | What happens |
|---|---|
| `TryEnter` **before** Lr (the handler pauses after it, inside its admission) | B1 sets `revoked`, but **B2 blocks** on this admission. The handler resumes and its send executes, legitimately, because it was admitted before revocation began. It calls `Exit`; only then do B2 and B3 proceed (abort). The bytes either left the host before Ta or were discarded by the abortive close. |
| `TryEnter` **after** Lr (the handler paused before entering) | `TryEnter` returns `false` under the lock: the buffer is discarded, the connection is aborted, and **no send syscall is issued**. |

There is no third order. Therefore:
- **no send syscall begins after Lr;**
- every send that began before Lr has finished before Ta;
- after Ta no byte can be put into an upstream socket of the session, and TCP originates no further segment for it (E2). Segments that TCP had already passed below the socket layer may still leave the host after Ta (E3, never claimed; §7.6.6).

#### 7.6.4 Kernel behaviour the barrier relies on (verified against OS documentation, 2026-11-15)

| Mechanism | Linux | Windows |
|---|---|---|
| Abortive close (B3) | `SO_LINGER` `{l_onoff = 1, l_linger = 0}` then `close`. `socket(7)` documents lingering, but **does not document** the zero-timeout behaviour (RST, send queue discarded). This is long-standing kernel behaviour, but we **do not rely on documentation**: test V7 verifies on the verification image's kernel that no queued payload is transmitted after the close and the peer receives RST. | `SO_LINGER` with zero timeout: Microsoft documents an **abortive** shutdown in which "any unsent data is lost" (*Graceful Shutdown, Linger Options, and Socket Closure*). V7 also runs on Windows. |
| Graceful close at process exit | `socket(7)`: a socket closed as part of `_exit` "**always lingers in the background**", so unsent data keeps draining after a crash or kill. | Default `closesocket` is a graceful shutdown that completes in the background; the application "has no way of knowing when (or whether)" it completes. |
| `TCP_USER_TIMEOUT` (Linux) / `TCP_MAXRT` (Windows) | `unsigned int`, **milliseconds**; 0 = system default. Closes the connection (`ETIMEDOUT`) only when transmitted data stays **unacknowledged**, or buffered data stays untransmitted **due to a zero window**, longer than the value. Effective only in synchronized states. "has no effect on when TCP retransmits a packet". Without it, failure can take up to ~13–30 min (`tcp_retries2` = 15). | `DWORD`, **seconds**: "the desired connection timeout"; -1 disables (retransmit forever, with RTO capped at 60 s). Documented for retransmission only; zero-window persist and orphaned connections are **not** documented. |
| What these options do **not** bound | A peer that keeps acknowledging through a small but non-zero window keeps the connection alive, so buffered bytes can keep leaving for an unbounded time. Behaviour on orphaned sockets is not documented. | Same: there is no documented bound on when the last byte of a draining connection leaves. |

**Conclusion:**
- **No retransmission deadline is claimed.** Neither option yields a documented guarantee that no byte leaves after a fixed time once a process has died, so the round-7 "`FencedAt + 30 s`" horizon is **withdrawn**, and the 256 KB send-buffer cap is **not** treated as a deadline either (it is removed from the plan).
- **Defence in depth only:** both options are still set on every upstream socket (`Egress:UpstreamUserTimeoutSeconds`, default 30, passed as 30,000 ms on Linux and 30 s on Windows) to make stalled connections fail fast. No guarantee depends on them.
- **What guarantees a process-death horizon instead:** a supervisor-verified socket drain (§7.6.5).

#### 7.6.5 Connection ledger, connection identity and supervisor socket drain (fences without a barrier)
**Ledger (W5).** One record per upstream connection, appended and `fsync`ed **before any payload admission** on that connection. Each record carries a CRC.

| Field | Linux | Windows |
|---|---|---|
| Tuple | address family, local IP:port, remote IP:port | the same |
| **OS identity** | the socket cookie (`SO_COOKIE`, 64-bit), the kernel's per-socket identifier. Uniqueness and non-reuse within a boot are stated in the kernel change that introduced `SO_COOKIE`, not in the man pages, so they are verified by P11 rather than assumed | `liCreateTimestamp` of the connection's row in `GetExtendedTcpTable(TCP_TABLE_OWNER_MODULE_ALL)` (IPv4 `MIB_TCPROW_OWNER_MODULE`, IPv6 `MIB_TCP6ROW_OWNER_MODULE`), documented as the time "the context bind operation that created this TCP link occurred". The Worker reads it right after `connect` by matching the tuple **and its own PID**; exactly one matching row is required |
| Session id | egress session | egress session |

- **Identity unreadable:** if the identity cannot be read (zero, absent, or more than one matching row), the connection is aborted unused, so a connection without a recorded identity never carries payload.
- **Torn final record:** if the CRC fails on the **last** record only, it is ignored. By W5, no payload was ever admitted on that connection.
- **Any other damage:** a CRC failure elsewhere, a gap, or an unreadable file means **no fence**.

**Linux: isolation, plus exact identity as a second check.**
- **Isolation (primary):**
  - every Worker incarnation runs in a **dedicated network namespace** (its container's namespace, or one created by the supervisor);
  - the containment handshake verifies that the namespace differs from the host's and from every other incarnation's, and that only processes of the Worker's unit are in it;
  - the supervisor keeps an `ns/net` handle, so the namespace and its TCP table outlive the Worker.
- **No foreign sockets after death:** once the unit is proven empty, **no live process exists in that namespace**. Every TCP socket in it is therefore an orphan of this incarnation, and no other process can create a replacement connection there (joining a namespace needs privileges no other workload has).
- **Enumeration:** dump **all** TCP sockets in the namespace (IPv4 and IPv6, all states) via `sock_diag`.
  - The dump must finish without a netlink error, truncation or the dump-inconsistency flag (`NLM_F_DUMP_INTR`). It is retried a bounded number of times; persistent failure means **no fence**.
  - **Cross-check:** every ledger entry must be either absent or present **with the same cookie**. A same-tuple socket with a different cookie would mean isolation is broken, which is treated as ambiguity and gives **no fence**.
- **Destroy by exact identity:** every transmit-capable socket, including unledgered ones such as orphaned loopback browser→proxy sockets (they also belong to the dead incarnation), is destroyed by a `SOCK_DESTROY` request that carries its **exact cookie**. The kernel refuses a request whose cookie does not match the current socket for that tuple, so a socket other than the identified one is never destroyed. This is not in the man pages, so P11b verifies it.
- **Re-enumerate:** the namespace counts as drained only when **every** TCP socket in it is in a safe state.

**Windows: exact identity, no force-abort, ambiguity fails closed.**
- **Why there is no force-abort:**
  - plain processes have no per-process network namespace (Windows container compartments are a Phase 7 topic), so the TCP table is shared with unrelated processes;
  - `SetTcpEntry(MIB_TCP_STATE_DELETE_TCB)` targets a connection **only by its tuple** (and only IPv4), so a replacement connection with the same tuple could appear between the identity check and the call.

  **The supervisor therefore never calls `SetTcpEntry` and never terminates any connection on Windows.** The round-8 IPv4-only restriction is withdrawn with it.
- **Drain:** enumerate the **complete** IPv4 and IPv6 owner-module tables (retrying on `ERROR_INSUFFICIENT_BUFFER`; any other error means **no fence**). Then, for each ledger entry:

  | Rows with the entry's tuple | Result |
  |---|---|
  | none | drained |
  | exactly one, identical `liCreateTimestamp`, safe state | drained |
  | exactly one, identical `liCreateTimestamp`, transmit-capable state | not yet drained: poll again |
  | any row with a different or zero timestamp, or more than one row | **ambiguous: not drained.** It is never treated as "the original is gone" and never touched |
- **Polling:** the supervisor polls until every entry is drained or `FenceConfirmTimeoutSeconds` elapses; at the deadline there is no fence (`AwaitingFence` → `NeedsReview`).
  - A lingering connection usually ends on its own; the fail-fast `TCP_MAXRT` helps when the peer has stalled.
  - A trickling peer, or a long-lived replacement on the same tuple, cannot be resolved automatically on Windows. This is accepted: Windows is a development and self-hosted platform, and the hosted platform is Linux.

**Fail-closed conditions.** Any one of these means the supervisor reports **no fence**; the attempts stay `AwaitingFence` and become `NeedsReview` after `FenceWaitSeconds`. The journal accepts `ProcessDeathConfirmed` (and `DrainVerified`) only with a drain report whose result is `Drained` for every ledger entry and, on Linux, every socket in the namespace (F9).

| Condition | Linux | Windows |
|---|---|---|
| Ledger missing, unreadable, or corrupt (other than a torn final record) | no fence | no fence |
| Namespace handle lost, namespace not dedicated, or a live process found in it after "unit empty" | no fence | n/a |
| Enumeration incomplete: netlink error, truncated or interrupted dump (`NLM_F_DUMP_INTR`) that keeps failing on retry; `GetExtendedTcpTable` error other than the buffer-size retry | no fence | no fence |
| `SOCK_DESTROY` unsupported (`EOPNOTSUPP`, kernel without destroy support) or denied (`EPERM`, no `CAP_NET_ADMIN` in that namespace) | no fence | — |
| Destroy reported success, or was silently skipped (as `ss -K` does), but re-enumeration still shows the socket transmit-capable | no fence | — |
| Ambiguous identity: cookie or timestamp mismatch, zero identity, or duplicate rows | no fence | no fence |
| **Partial drain:** any socket still transmit-capable at the deadline, even if all others were drained | no fence | no fence |
| Supervisor timeout, crash or restart during a drain. A drain restarts from the beginning; a partial result is never reported | no fence | no fence |
| `SetTcpEntry` | — | never called. If a future change used it, a failed call (`ERROR_ACCESS_DENIED`, 317 non-elevated, `ERROR_NOT_SUPPORTED`) would also have to prevent the fence, and an ADR would be required, because it cannot target by identity |

**Host reboot or power-off:** a changed boot id, or a host confirmed powered off or destroyed, implies that no kernel TCP state survives, so no drain is needed.

#### 7.6.6 Three different events, and what a fence actually proves
| Event | Meaning | Proven by |
|---|---|---|
| **E1, write completion** | every admitted upstream send syscall has returned, and no further send can be admitted | barrier B1 + B2 (write gate); process death: unit empty (no process can issue a send) |
| **E2, connection destruction** | the TCP state of every upstream connection is gone or terminal: its send queue is discarded, and TCP **originates** no further segment or retransmission for it | barrier B3 (abortive close; V7); process death: the socket drain (§7.6.5) |
| **E3, physical departure** | every segment already handed **below** the socket layer has physically left the host: IP output, queueing and traffic control, driver and NIC rings, veth / bridge / NAT, host firewall, hypervisor switch | **nothing.** An absent or terminal TCP entry says nothing about segments TCP had already passed down. E3 is never claimed |

**Dispatch horizon H:** the journal commit time of the fence, `FencedAt` (`AbandonedAt` for `NoCheckpoint`).
- It is recorded **only after E1 and E2 are proven**, so H is a conservative upper bound on every upstream write admission, every write completion, and the end of all TCP-originated transmission for the attempt.
- **H is not a physical-departure time.**

| Fence source | E1 and E2 proven by | H |
|---|---|---|
| `ExecutorEgressRevoked` | barrier `Completed` (B1–B5); `CompleteAsync` after the barrier | `FencedAt` |
| `EgressLeaseExpired` (Phase 7) | the same barrier in the out-of-process proxy, completed before its report | `FencedAt` |
| `NoCheckpoint` | no side-effect handler ever ran, so no side-effect request exists | `AbandonedAt` |
| `ProcessDeathConfirmed` | unit empty (E1) **and** drain `Drained` (E2), or a boot-id change (both) | `FencedAt` |
| `HumanConfirmed` | host powered off or destroyed, host rebooted, or `DrainVerified` (unit empty plus a drain report) | `FencedAt` |
| `IsolationTerminated` (Phase 7) | not accepted until Phase 7 specifies and tests an equivalent E1 + E2 proof | — |

#### 7.6.7 What the latency contract must cover, and why H is safe
**The contract covers everything after H, as a trusted attestation.** `maxCommitLatencySeconds` (L, ADR-057) bounds everything a request of the attempt can still go through **after the dispatch horizon H**:
1. **local buffering below the socket layer** (the E3 queues, which the platform cannot bound);
2. **network transit**;
3. **target processing**, until the request is committed or definitively abandoned;
4. **query visibility**, until a committed request is visible to the reconcile lookup.

This is an **attestation by the author, based on the target operator's contract. It is not a TCP guarantee.**

**Relation to "measured from upstream write admission":** H is never earlier than the last upstream write admission of any request in the attempt. Applying L from H is therefore at least as conservative as applying it from write admission.
- An author may reason from admission ("committed and visible within L of being sent"), **provided** their L also covers time in local queues.
- The platform does not apply L from admission itself, because flow control can hold admitted bytes in our socket buffer for an unbounded time before they are sent. Measuring from H removes that wait from the attestation: by H, such bytes were either sent (E2 happened after them) or discarded at E2.

**Argument.** For any request R of the attempt:
1. **Admission:** every byte of R was admitted before Lr (barrier) or before the process died, so before H.
2. **Our TCP stack:** whatever TCP transmitted, it transmitted before E2, which is before H. Nothing is transmitted or retransmitted by our TCP after E2.
3. **Below the socket layer and beyond:** bytes queued below the socket layer, in transit, or at the target are covered by the attestation, so R is committed and visible, or definitively abandoned, by H + L.
4. **Eligibility:** an authoritative `NotFound` counts only if `ObservedAt` ≥ H + L + skew, and the lookup reads after `ObservedAt`. So R is either already visible (giving `Found`) or definitively abandoned. A `NotFound` can therefore not be followed by R committing.
5. **One clock:** H and `ObservedAt` are both journal-clock values; the skew allowance covers small adjustments.

**When the attestation is violated, safety can fail.** Examples: a transit or processing time longer than L, or a **host that is paused or suspended** while segments sit in its queues (they leave when it resumes). This is limitation 2 in §15, and test H4 demonstrates it.
- **Operational rule (Phase 5 runbook, with the no-snapshot rule):** Worker hosts must not be paused, suspended or live-migrated with execution in progress.

**Not claimed:**
- E3, physical departure;
- that an already-dispatched side effect failed.

A fenced attempt stays ambiguous, and only rules A1–A8 resolve it (§7.5.8).

---

## 8. Condition evaluation (no scripting)

### 8.1 Expressions
```jsonc
{ "id": "check", "type": "condition", "config": {
    "if": { "any": [
      { "urlMatches": "**/Customers/Confirmation/**" },
      { "all": [
          { "elementVisible": { "testId": "form-error" } },
          { "not": { "attributeEquals": { "selector": { "testId": "form-error" }, "attribute": "data-error-kind", "value": "validation" } } }
      ] }
    ] },
    "then": [ /* steps (at least one) */ ],
    "else": [ /* steps (may be empty) */ ] } }
```

| Group | Predicates |
|---|---|
| Combinators | `all` (AND, 1..10 children), `any` (OR, 1..10 children), `not` (NOT, exactly one child) |
| Page | `elementExists`, `elementVisible`, `elementCountAtLeast` (selector + n), `textEquals`, `textContains`, `attributeEquals`, `urlMatches` (§8.2) |
| Values | `valueEquals`, `valueContains`, `valueStartsWith`, `isEmpty` (template operands; ordinal; optional `ignoreCase`) |

- **No scripting:** no regular expressions in v1 (no ReDoS surface), no arithmetic, no user functions, no JavaScript and no C#.
- **Bounds:** limits per §1.6; evaluation is short-circuit, left to right.
- **Operands** follow the sink rules (§5.1): sensitive inputs and sensitive extracts are allowed, secrets are rejected.
- **Nested validation:**
  - Expressions are validated recursively (shape, depth, child counts, operand templates); `transientWhen` uses the same rules.
  - `then` and `else` sequences are validated with the same rules as their enclosing section: ids, dominance, sinks, side-effect placement and declaration, terminal-last, per-section outcomes and explicit finalization.
  - Step nesting depth ≤ 3; `then` must be non-empty.
- **Implementation:** a closed set of sealed records (`ConditionExpr`) and a pure evaluator over an `IPageProbe` interface, which the session implements via `CountAsync` / `IsVisibleAsync` / `CurrentUrl` / `GetTextAsync` / `GetAttributeAsync`. It is unit-tested with a fake probe.
- **Logging:** only the boolean result is logged, never the compared values.

### 8.2 `urlMatches` and `wait.url` glob semantics (no regular expressions)
- **Subject:** the full current URL exactly as the browser reports it (scheme, host, port, path, query and fragment).
- **Pattern:** either an absolute pattern (beginning with `http://` or `https://`) or one beginning with `**/`.
- **Wildcards:** exactly two.
  - `*` matches zero or more characters, **excluding `/`**.
  - `**` matches zero or more characters, **including `/`**.
- **Every other character is literal**, and the characters `? { } [ ] \` are **rejected** in patterns.
- **Why this subset:** it means the same thing to our matcher and to Playwright's glob (used by `wait` with `url`), so conditions and waits cannot disagree.
- **Case:** comparison is ordinal (case-sensitive) everywhere. Browsers always report scheme and host in lowercase, so the validator **rejects uppercase letters in the scheme and host of an absolute pattern**.
- **Implementation:** a hand-written iterative glob matcher, bounded by the pattern and URL length limits (§1.6), with no regular expressions.
- **Clarification (ADR-062):** to be identical to Playwright, `**` must be a whole path element; `**/` matches zero or more whole segments (`/a/**/b` matches `/a/b`) and a final `/**` matches the rest. Other `**`, `***` and default ports in absolute patterns are rejected.

---

## 9. Errors, classification and continue/stop
```csharp
public sealed record ExecutionError(
    string Code,                 // stable code, see table
    ErrorCategory Category,      // Transient | Permanent | Policy | Business | Cancelled
    bool IsTransient,            // hint only; Phase 5 decides via RepeatSafetyEvaluator (§10)
    string? StepId,
    string Message);             // redacted
```

| Source | Code(s) | Category / transient |
|---|---|---|
| Phase 3 `BrowserErrorCode` | `Timeout`, `SessionTimeout`, `ElementNotFound`, `NetworkError`, `EgressUnavailable`, `BrowserCrashed`, `SessionClosed` | Transient / yes (as in Phase 3) |
| Phase 3 `BrowserErrorCode` | `ElementNotUnique`, `InvalidSelector`, `HttpError` (raised by `navigate` for status ≥ 400 not in `acceptStatus`), `DownloadRejected` | Permanent / no |
| Phase 3 `BrowserErrorCode` | `NavigationBlocked`, `PolicyViolation` | Policy / no |
| Engine | `RecordInvalid`, `VariableUnresolved`, `UnsafeUrlValue`, `UploadRejected`, `ExtractTooLong`, `ResultTooLarge`, `WorkflowTampered`, `ResultOverwrite`, `WorkflowTimeoutExceedsServerLimit` | Permanent / no |
| Engine | `SecretUnavailable`, `CheckpointFailed`, `JournalUnavailable`, `LookupInconclusive` | Transient / yes |
| Engine (refusals only) | `RepeatNotAuthorized`, `AttemptInFlight`, `AwaitingFence`, `HistoryInvalid` | Policy / no |
| Engine (reconcile findings) | `AmbiguousMatch`, `TooManyRows` (outcome `NeedsReview`) | Business / no |
| `captureResult.outcome` | author-defined (e.g. `DuplicateCustomer`) | Business / as declared (`transient` or `transientWhen`) |
| Cancellation or session-terminating timeout | `Cancelled` | Cancelled / no |

### 9.1 Normal attempts (ADR-051)
- **`onError`:**
  - `onError: "fail"` (the default) makes the error terminal and stops.
  - `onError: "continue"` records the error and proceeds. It is allowed only on `wait`, `extract`, `screenshot`, `fill`, `select` and `condition` (§2).
  - Later steps can read `{{steps.<id>.failed}}`.
- **Before any side effect started:**

  | Situation | Outcome |
  |---|---|
  | Any error | `Failed`, with category and transience as classified |
  | `captureResult` `failed` | `Failed` |
  | `captureResult` `succeeded` | `Succeeded` |
  | Cancellation | `Cancelled` |

  Repeating is safe (no ambiguity).
- **After a side effect started** (a checkpoint was written):

  | Situation | Outcome |
  |---|---|
  | A terminal `captureResult` with status `succeeded` | `Succeeded` |
  | A terminal `captureResult` with status `failed` whose resolved transience is **false** (a declared permanent **business** outcome, e.g. `DuplicateCustomer`, `RegistrationRejected`) | `Failed` |
  | **Everything else**: any transient business outcome; any Transient, Permanent, Policy or Cancelled error from the browser or the engine (`ElementNotUnique`, `InvalidSelector`, `ExtractTooLong`, `ResultTooLarge`, `ResultOverwrite`, `VariableUnresolved`, `UnsafeUrlValue`, `HttpError`, `NavigationBlocked`, timeouts, crashes); a run-time failure while evaluating the terminal `captureResult` itself; cancellation; a session-terminating timeout | **`Indeterminate`**, keeping the original code |

  A technical error after a side effect says nothing about whether the target committed. Only the target's own, explicitly classified answer can.
- **Side-effect step timeouts:**
  - side-effect steps require `timeoutMs` ≥ 5,000;
  - a side-effect step that times out is always `Indeterminate`, because the request may have reached the target, so it is never classified as "not executed";
  - the session survives the step timeout (D2), but the attempt ends there.

### 9.2 Reconcile attempts (ADR-052)
- Any step error ends the attempt `Failed`, with transience as classified.
- Cancellation or a session-terminating timeout ends it `Cancelled`.
- `reconcileMatch` produces the finding (§10.3).
- A failed or cancelled reconcile is never a finding and never authorizes anything. The evaluator bounds how many may happen (§10.4).

### 9.3 Step timeouts and late effects (ADR-053)
- `timeoutMs` (or the action's default) is passed as `OperationOptions.Timeout` and is **one total deadline** for the step, including every sub-operation (`SubmitAsync`: click **and** load wait).
- **Expiry stops our wait, not necessarily the browser's work.** A form POST already sent continues on the server.
  - **Side-effect steps:** `Indeterminate`, the attempt stops, and the session is disposed, which aborts the client side. A server that keeps processing may still commit later. That is why authoritative evidence requires the target's documented `maxCommitLatencySeconds` to have elapsed after the attempt's dispatch horizon (§7.6, §10.4), and the late-commit scenario tests it (§13.4 D1).
  - **Non-side-effect steps:** `onError: continue` is forbidden on every action that can start a navigation, POST or transfer (`navigate`, `click`, `upload`, `download`). A timed-out step of these types ends the attempt (`Failed` before a side effect, `Indeterminate` after).
- **Remaining limitation:** a `fill` or `select` on a page with an auto-submitting `onchange` handler can trigger a navigation that the platform cannot detect. Authors must not use such controls without declaring the triggering step as a `click` with `sideEffect: true` (documented in `docs/WORKFLOWS.md`).

---

## 10. Side effects, reconciliation and repeat safety

### 10.1 Side effects (ADR-053)
- **`sideEffect` must be declared explicitly** on every `click` and `upload` anywhere in `steps`, including branches. A missing value is a validation error, so an author cannot forget to mark a submit.
  - `true` is allowed only on `click`/`upload` in `steps`.
  - In `setup` and `reconcile` it must be absent or `false`.
  - On other action types it is forbidden.
  - The validator **warns** when a `click` with `waitForNavigation: true` in `steps` declares `false` (a likely form submit).
- **Checkpoint first:** before a side-effect handler is invoked, `journal.SideEffectStartingAsync` must return successfully (§7.2). The flag is therefore durable before any request can leave the browser.
- **`{{operation.id}}`** gives a stable idempotency key.
- **`idempotency: { key: "{{operation.id}}", guarantee: "targetEnforced" }`** on a side-effect step is an **author attestation** that the target deduplicates submissions carrying that key.
  - **Not assumed by default:** the platform does **not** assume any website supports idempotency.
  - **Validator prerequisite:** a dominating `fill` or `select` step must send exactly `{{operation.id}}` before that side-effect step. The validator cannot prove the target honours it; that is what "attestation" means.
  - **Hosted mode** (Phase 5/6): publishing a workflow with this attestation is reserved for an organization admin role.
- **Static workflow properties** (computed once per `ValidatedWorkflow` and recorded in every attempt's snapshot):
  - `TargetEnforced`: the workflow has at least one side-effect step and **every** side-effect step declares `targetEnforced`.
  - `OperationIdSent`: **every** side-effect step is dominated by a `fill`/`select` of exactly `{{operation.id}}`.

### 10.2 Reconcile attempts never continue into main steps
A `Reconcile` attempt runs `setup`, then `reconcile`, then **stops**. It never runs main steps and never performs a side effect, since side-effect steps are forbidden in `reconcile`.

### 10.3 Exact-match reconciliation: `reconcileMatch` (ADR-052)
**Structure (validator):**
- The `reconcile` section's **last top-level step** must be a `reconcileMatch`, and it is the only one in the workflow.
- `captureResult` is forbidden in `reconcile`.
- Conditions in `reconcile` may not contain `reconcileMatch`.

**Key rules (validator):**

| `reconciliation.matchKey` | `reconcileMatch.equals` must be | Additional rule |
|---|---|---|
| `operationId` | exactly `{{operation.id}}` | The workflow's `OperationIdSent` must be true. A lookup by a key the submission never carried proves nothing. |
| `businessKey` | exactly one `{{record.<input>}}`, a required, non-sensitive input | The author attests the key is unique in the target (DemoCrm: email is unique). |

**Search-superset attestation:** the author attests that the page's rows are a **superset** of the exact matches (a search may return extra rows; it must not omit exact matches). `reconcileMatch` then filters exactly.

**Evaluation** (all reads bounded; values never logged):
1. `n = CountAsync(rows)`. If `n > maxRows`: terminal `NeedsReview` (`TooManyRows`); the engine cannot be sure it saw every candidate.
2. If `n == 0`:
   - if `emptyIndicator` is visible (`IsVisibleAsync`): **`ReconciledNotFound`** (positive evidence of an empty result);
   - otherwise: `Failed`/`LookupInconclusive` (transient). This covers an error page, a login redirect or a layout change, which never count as "not found".
3. Otherwise, read each row's key (`QueryRowsAsync`).
   - Compare `Trim(key)` with `Trim(resolved equals)` using ordinal or, with `ignoreCase`, ordinal-ignore-case comparison.
   - The resolved `equals` must be non-empty (`VariableUnresolved`, permanent).
   - Count the exact matches `m`.
4. Decide on `m`:

   | `m` | Outcome |
   |---|---|
   | 0 | **`ReconciledNotFound`** (rows were shown; none is an exact match) |
   | 1 | **`ReconciledFound`**; `fields` are read from that row (non-sensitive, §1.6 result limits) |
   | > 1 | terminal `NeedsReview` (`AmbiguousMatch`) |
5. Any browser error is a step error, so the attempt ends `Failed` (classified; §9.2).

**Evidence:** the result carries `ReconciliationEvidence(Finding, ObservedAt, Policy)`:
- `ObservedAt` is the journal-clock time recorded by `MarkObservationAsync` **immediately before the first reconcile-section step**. Every row the attempt reads was fetched after it, so it is conservative for both the commit-latency wait and the maximum age.
- `Policy` is the `ReconcilePolicySnapshot` of the version that ran the lookup.

### 10.4 What a `NotFound` finding proves
It proves only that **this lookup, at `ObservedAt`, found no exact match**. It does **not** prove that the ambiguous submission failed, because:
- the target may index or replicate with a delay;
- the server may still be processing a request whose client already gave up (§9.3);
- the target may have accepted the submission into a pending or moderation state that search does not show.

Therefore **`NotFound` never authorizes a repeat side effect by itself.** It contributes to an authorization only when **all** of these hold:
- the lookup was declared **authoritative** with a target **commit-latency contract** (below);
- the ambiguous attempt is **fenced**, and the lookup ran at least `maxCommitLatencySeconds` + clock-skew allowance after its **dispatch horizon** (§7.6);
- the evidence is younger than `maxEvidenceAgeSeconds`;
- the lookup's key was actually sent by the ambiguous attempt (`operationId` keys).

An **advisory** lookup can confirm `Found` but can never authorize a repeat.

**What justifies `evidence: "authoritative"` (ADR-057).** The author must be able to point to a **target-specific contract**, recorded in `authority.contractReference` (part of the hashed definition), that states all of the following:

| # | Required property of the target | Why |
|---|---|---|
| 1 | **Bounded commit latency:** every request is committed **and visible to the lookup**, or definitively abandoned, within `maxCommitLatencySeconds` after the dispatch horizon (§7.6.7). The bound must cover **local buffering below our socket layer, network transit, target processing and query visibility**. An author who reasons from upstream write admission must also include the time a request can wait in local queues. This normally rests on a server-side request deadline with rollback, or on synchronous commit before the response is sent. There must be no unbounded queues, retries or background jobs that can commit later. **This is a trusted attestation, not a TCP guarantee.** | Otherwise a submission still being processed can appear after the lookup |
| 2 | **Read-your-writes lookup:** once committed, a record is visible to the lookup page immediately (no replica lag, search index delay or cache longer than the bound in #1) | Otherwise "committed" does not imply "found" |
| 3 | **Pending or moderated records are visible** to the lookup, or the target has no such state | Otherwise accepted-but-pending submissions are invisible |
| 4 | **Unique key and superset search:** the match key is unique in the target, and the lookup returns every exact match (ADR-052) | Otherwise `NotFound` can be a search artefact |

- **How the contract is established:** only from the target operator, e.g. the target's API/operations documentation, a written statement from the system owner, or the organization's own system configuration when it operates the target.
- **Observation is not a contract.** A delay that "seemed long enough", repeated empty searches, or measured response times alone are **never** sufficient. They cannot bound worst-case latency, so a generic third-party website **cannot** be declared authoritative merely because a search returned nothing after a delay.
- **Unverifiable means advisory:** the validator requires the `authority` block for `authoritative`, and the authoring guide instructs authors to use `advisory` whenever any of #1–#4 cannot be backed by such a source.
- **Advisory consequence:** with advisory evidence, an ambiguous attempt can be resolved only by `Found`, by target-enforced idempotency, or by a human. It **never** leads to an automatic repeat.
- **Hosted mode (Phase 5/6):** publishing `authoritative` (like `targetEnforced`) is reserved for an organization admin role. The attestation and its reference are shown in the workflow's audit view.
- **DemoCrm's contract:**
  - `docs/DEMO_CRM.md` (updated in step 8) documents it: synchronous in-process commit under a lock, read-your-writes search, no pending state, unique email and operation id;
  - maximum commit latency = `max(SlowDelayMs, LateCommitDelayMs)` + 1 s (8 s with the test configuration). The 1 s margin covers local buffering, loopback transit and query visibility, all of which are negligible on loopback;
  - variants C and D therefore declare `maxCommitLatencySeconds: 10` with that reference.

### 10.5 `RepeatSafetyEvaluator` (Application, pure; used by Phase 5 and enforced by the executor) (ADR-045, ADR-050)
```csharp
public enum AttemptState { Reserved, Started, Completed, Abandoned }
public enum SideEffectState { NotStarted, Started, Unknown }
public enum EvidenceLevel { Advisory, Authoritative }
public enum MatchKeyKind { OperationId, BusinessKey }

public sealed record ReconcilePolicySnapshot(          // advisory: latency and age are 0 and never used
    EvidenceLevel Level, MatchKeyKind MatchKey, int MaxCommitLatencySeconds, int MaxEvidenceAgeSeconds);

public sealed record AttemptSnapshot(                  // recorded at StartAsync from the version that actually ran
    string DefinitionHash, int WorkflowVersion, int TimeoutSeconds,
    bool TargetEnforced, bool OperationIdSent, ReconcilePolicySnapshot? Reconcile);

public sealed record ReconciliationEvidence(ReconciliationFinding Finding, DateTimeOffset ObservedAt, ReconcilePolicySnapshot Policy);
public enum ReconciliationFinding { Found, NotFound, Ambiguous }

public sealed record AttemptSummary(
    Guid OperationId, int AttemptNumber, ExecutionIntent Intent, AttemptState State,
    WorkflowOutcome? Outcome, ErrorSummary? Error,           // Error: code, category, IsTransient
    SideEffectState SideEffect, AttemptSnapshot Snapshot,
    DateTimeOffset StartedAt, DateTimeOffset? SideEffectCheckpointAt,
    DateTimeOffset? FinishedAt, DateTimeOffset? AbandonedAt,
    bool ExecutionFenced, DateTimeOffset? FencedAt, FenceSource? FenceSource,   // see §7.2 fencing table
    ReconciliationEvidence? Reconciliation);

public sealed record RepeatSafetyPolicy(TimeSpan ClockSkewAllowance, int MaxReconcileFailures, int MaxAmbiguousAttempts);   // server options (§1.6)

public enum RepeatDecisionKind { Allowed, NotNeeded, ReconcileRequired, ReconcileTooEarly, InFlight, AwaitingFence, NeedsReview }
public enum SafetyEvidence { None, NoPriorAttempt, NoSideEffectStarted, TargetEnforcedIdempotency, AuthoritativeNotFound, Resolved }
public enum Resolution { Succeeded, BusinessFailure, PermanentFailureBeforeSideEffect, ReconciledFound }

public sealed record RepeatDecision(
    RepeatDecisionKind Kind, SafetyEvidence Evidence, Resolution? Resolution,
    RepeatReason Reason, DateTimeOffset? RetryAfter);

public static class RepeatSafetyEvaluator
{
    public static RepeatDecision Evaluate(ValidatedWorkflow current, Guid operationId,
        IReadOnlyList<AttemptSummary> history, DateTimeOffset now, RepeatSafetyPolicy policy);
}
```

**Derived definitions** (attempts ordered by `AttemptNumber`, never by timestamps):
- `SE(a)`: `Started` if `a.SideEffect` ∈ {`Started`, `Unknown`}, else `NotStarted`.
- `Permanent(a)`: `a.Error.IsTransient == false` and `a.Error.Category != Cancelled`.
- **Resolving Normal attempt:**
  - `Outcome == Succeeded`; or
  - `SE == Started`, `Outcome == Failed`, `Error.Category == Business` and not transient.
- **Ambiguous Normal attempt:** `SE == Started` and not resolving. This covers `Indeterminate`, `Abandoned` with a checkpoint, `Unknown`, and any legacy or defective combination.
- **A\*:** the highest-numbered ambiguous Normal attempt, if any.
- **Fenced(a)** and **FenceTime(a)** (§7.2 fencing table; the earliest applicable time):

  | Attempt | Fenced | FenceTime |
  |---|---|---|
  | `SE == NotStarted` | yes (nothing to dispatch) | — |
  | Completed with `ExecutionFenced` (in time or late) | yes | `FinishedAt` |
  | Abandoned without checkpoint | yes | `AbandonedAt` |
  | Has `FencedAt` (external confirmation) | yes | `FencedAt` |
  | Any other attempt with a checkpoint (Abandoned or Completed without `ExecutionFenced`) | **no (unfenced)** | — |

  **No time is ever derived from an assumed process timeout.**
- **DispatchHorizon(a):** a **conservative upper-bound timestamp** (§7.6.6), recorded only after every admitted upstream write has completed (E1) and every upstream connection has been destroyed so that TCP originates nothing more (E2). It equals `FenceTime` (`AbandonedAt` for `NoCheckpoint`). It is **not** a proof that already-queued segments have physically left the host (E3). Local buffering below the socket layer, transit, processing and visibility after H are covered by the trusted commit-latency attestation (§7.6.7). **Every evidence calculation uses `DispatchHorizon`.** A fence never resolves an ambiguity by itself (§7.5.8).
- **Recs:** Reconcile attempts numbered after A\*.

**Decision procedure: first match wins.** Every path ends in exactly one rule, and the last rule is a mandatory default.

| Rule | Condition | Decision | Evidence / reason |
|---|---|---|---|
| **S0 Integrity** | any entry's `OperationId` ≠ `operationId`; duplicate or non-positive attempt numbers; a `Completed` entry without an outcome | NeedsReview | None / `HistoryInvalid` |
| **S1 In flight** | any entry `Reserved` or `Started` (lease not yet expired; the journal marks expired ones `Abandoned`) | InFlight | None / `AttemptInFlight` |
| **S1b Unfenced** | any attempt is unfenced (it has a checkpoint and could still dispatch, e.g. a frozen Worker) | AwaitingFence | None / `UnfencedAttempt` |
| **S2 Empty** | no entries | Allowed | NoPriorAttempt |
| **S3 Resolved** | a resolving Normal attempt exists **after A\*** (or anywhere, if there is no A\*) | NotNeeded | Resolved (Succeeded or BusinessFailure) |
| *If A\* exists (open ambiguity): rules A1–A9; resolution and no-side-effect rules are never reached* | | | |
| **A1 Found** | any Rec ended `ReconciledFound` | NotNeeded | Resolved (ReconciledFound) |
| **A2 Reconcile needs review** | any Rec ended `NeedsReview` (AmbiguousMatch, TooManyRows) or `Failed` permanently | NeedsReview | None / `ReconcileNeedsReview` |
| **A3 Broken workflow** | the latest Normal attempt after A\* ended `Failed` permanently with `SE == NotStarted` | NeedsReview | None / `PermanentFailureWithOpenAmbiguity` |
| **A4 Reconcile budget** | Recs that ended `Failed` (transient), `Cancelled` or `Abandoned` ≥ `policy.MaxReconcileFailures` | NeedsReview | None / `ReconcileBudgetExhausted` |
| **A4b Ambiguity budget** | ambiguous Normal attempts in the history ≥ `policy.MaxAmbiguousAttempts` (a deterministic failure after the side effect would otherwise be replayed forever) | NeedsReview | None / `AmbiguityBudgetExhausted` |
| **A5 Idempotency** | **every** ambiguous Normal attempt in the history has `Snapshot.TargetEnforced && Snapshot.OperationIdSent`, **and** `current.TargetEnforced && current.OperationIdSent` | Allowed | TargetEnforcedIdempotency |
| **A6 Latest NotFound** | let L = the latest Rec that ended `ReconciledNotFound`. If L exists: | | |
| A6a | L.Policy.Level = Advisory | NeedsReview | None / `AdvisoryNotFound` |
| A6b | L.Policy.MatchKey = OperationId and A\*.Snapshot.OperationIdSent = false | NeedsReview | None / `KeyNotSent` |
| A6c | L.ObservedAt < DispatchHorizon(A\*) + L.Policy.MaxCommitLatency + skew | ReconcileTooEarly (RetryAfter = that time, or `now` if later) | None / `EvidenceTooEarly` |
| A6d | now − L.ObservedAt > L.Policy.MaxEvidenceAge | ReconcileRequired (NeedsReview if `current` has no reconcile section) | None / `EvidenceExpired` |
| A6e | otherwise | Allowed | AuthoritativeNotFound |
| **A7 Reconcile next** | `current` has a reconcile section | `current` authoritative and now < DispatchHorizon(A\*) + current.MaxCommitLatency + skew ? ReconcileTooEarly : ReconcileRequired | None |
| **A8 No safe path** | otherwise | NeedsReview | None / `NoSafePath` |
| *If there is no A\* (no open ambiguity):* | | | |
| **N1 Permanent** | the latest Normal attempt ended `Failed` permanently (`SE == NotStarted`) | NotNeeded | Resolved (PermanentFailureBeforeSideEffect) |
| **N2 Reconciled first** | no Normal attempt exists and a Reconcile ended `ReconciledFound` | NotNeeded | Resolved (ReconciledFound) |
| **N3 No side effect** | every Normal attempt has `SE == NotStarted`, and the latest one (if any) ended `Failed` transient, `Cancelled` or `Abandoned` | Allowed | NoSideEffectStarted |
| **N4 Default** | anything else | **NeedsReview** | None / `NoMatchingRule` |

**Properties guaranteed by this ordering** (each is a unit test, §13.1):
1. With open ambiguity, only A5 and A6e can return `Allowed`. Both need positive, attempt-bound evidence.
2. `Found` (A1) wins over idempotency (A5): reconcile results are never ignored.
3. A reconcile `NeedsReview` or permanent failure (A2) wins over idempotency (A5): a human was explicitly requested.
4. A permanent pre-side-effect failure never hides an open ambiguity (A3 versus N1).
5. A newer version cannot vouch for an older ambiguous attempt. A5 checks every ambiguous attempt's own snapshot. A6b checks the ambiguous attempt's snapshot for key sending.
6. Nothing loops:
   - a failed reconcile: permanent failure goes to NeedsReview immediately (A2), and transient failures, cancellations and abandonment are capped (A4);
   - repeated ambiguous attempts are capped too (A4b), even when target-enforced idempotency makes each replay harmless.
7. Refusals are never in the history (§7.3), so they cannot change a decision.
8. If nothing matches, the answer is `NeedsReview` (N4).
9. **No takeover while anything could still dispatch:** S1b precedes every rule that can return `Allowed`. The journal's `ReserveAsync` enforces the same rule independently, so neither a scheduling bug nor a stale evaluation can admit a second attempt.
10. **Delays count from a proven dispatch horizon, never from a guess:**
    - A6c and A7 measure from `DispatchHorizon`, which is after every write admission and completion and after connection destruction (E1, E2), but is never claimed to be a physical-departure time (§7.6.6–7.6.7);
    - an unfenced attempt has no horizon, so no evidence can be "late enough" for it;
    - a fence alone never moves an ambiguous attempt to a resolution.

**Executor guard:**
- `Normal` requires `Allowed`.
- `Reconcile` is admitted unless the decision is `InFlight`, `AwaitingFence` or `HistoryInvalid`, or the workflow has no `reconcile` section. An early reconcile is harmless; the evaluator ignores its evidence (A6c). While an attempt is unfenced, even read-only reconciles are refused, because their findings could be overtaken by the unfenced attempt's late dispatch.
- Refusals are not journaled.

**Phase 5 boundary:**
- **Phase 4 delivers** the evaluator, the journal contract, the guard and the evidence.
- **Phase 5 decides** scheduling (when to run `ReconcileRequired` and `ReconcileTooEarly` work), back-off, attempt limits and record resolution.
- **`AwaitingFence` in Phase 5:**
  - it is shown to operators immediately;
  - it becomes record status `NeedsReview` if no fence arrives within `RepeatSafety:FenceWaitSeconds` (Phase 5 option);
  - it is cleared only by a fence (late completion, process-death confirmation, isolation, or an audited human confirmation).

### 10.6 Cost of safety (accepted)
After a side effect, the engine cannot tell "failed before saving" from "saved, response lost". Without target-enforced idempotency or authoritative evidence, such a record goes to `NeedsReview` rather than being resubmitted. Example: variant B `+fail-2`, where nothing was stored but the lookup is advisory. The platform deliberately prefers a human decision to a possible duplicate business record (ADR-015, ADR-045).

---

## 11. Version immutability, schema versioning and hash verification
- **`schemaVersion`** versions the *format*. The parser dispatches on it, rejects unknown versions and keeps a parser per supported version. Future versions add an explicit, tested upgrader (`v1 → v2`) that runs in memory. Stored definitions are never rewritten.
- **Workflow versions** version the *content* with the Domain entity `WorkflowVersion` (rules only; persisted in Phase 5):
  - lifecycle `Draft` → `Published` → `Archived`, with no transitions back;
  - Published and Archived definitions are immutable, and edits create a new version (§16 D5).
- **Deterministic hash:** `DefinitionHash` = `"sha256:" + lowercase hex` of the canonical form (§16 D5). The algorithm prefix leaves room for a future algorithm change via a new ADR.
- **Where it is verified:**
  1. **At publish**, computed and stored.
  2. **On load:** `ValidatedWorkflow.Load(definitionJson, recordedHash)` (hash mandatory) recomputes it from the parsed definition and refuses a mismatch (`WorkflowTampered`). New definitions use `ValidatedWorkflow.CreateForPublication(definitionJson)`, which computes the hash to be stored. There is no other way to construct a `ValidatedWorkflow` (ADR-061).
  3. **At execution start:** the executor calls `VerifyAgainst(recordedHash)`, which recomputes the hash from the canonical bytes held by the `ValidatedWorkflow` and compares it with the hash recorded for the pinned `WorkflowVersion` (external to the object). `VerifyIntegrity()` is only a self-consistency check.
- **Recorded per attempt:** the hash and version are part of the attempt snapshot (§10.5), so evidence is always bound to the definition that produced it.
- **Formatting-independent:** the hash is computed over the *parsed* definition, so whitespace or property-order changes in storage do not change workflow identity.
- **`ValidatedWorkflow`** is deeply immutable (records, read-only collections). Jobs (Phase 5) reference a specific `WorkflowVersion` id, never a "latest" pointer.

---

## 12. DemoCrm workflow and DemoCrm test-fixture extensions

### 12.1 The shipped sample (variant A; pure JSON, no workflow-specific code)
Stored as `samples/workflows/democrm-register-customer.json` and executed by the generic engine in the tests.
- **Why it may attest `targetEnforced`:** DemoCrm documents an idempotency contract (`docs/DEMO_CRM.md`): the operation reference is deduplicated server-side.
- **Why it can reconcile by operation id:** the §12.3 fixture extension adds exact lookup by operation id.
- **Typical sites:** most real sites would offer neither. Test variants B and C (§13.4) cover workflows without them.

```json
{
  "schemaVersion": 1,
  "name": "democrm-register-customer",
  "description": "Signs in to DemoCrm and registers one customer from a record, duplicate-safe.",
  "timeoutSeconds": 120,
  "parameters": [ { "name": "crmBaseUrl", "type": "url" }, { "name": "crmUser", "type": "string" } ],
  "allowedHosts": [ "{{params.crmBaseUrl}}" ],
  "secrets": [ "crmPassword" ],
  "inputs": [
    { "name": "firstName", "type": "string", "required": true, "maxLength": 60 },
    { "name": "lastName", "type": "string", "required": true, "maxLength": 60 },
    { "name": "email", "type": "email", "required": true, "maxLength": 120 },
    { "name": "phone", "type": "string", "required": true, "maxLength": 30 },
    { "name": "company", "type": "string", "required": false, "maxLength": 80 },
    { "name": "country", "type": "string", "required": true, "maxLength": 60 },
    { "name": "customerType", "type": "string", "required": true, "maxLength": 30 }
  ],
  "reconciliation": { "evidence": "advisory", "matchKey": "operationId" },
  "setup": [
    { "id": "open-login", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Login" } },
    { "id": "user", "type": "fill", "config": { "selector": { "testId": "login-email" }, "value": "{{params.crmUser}}" } },
    { "id": "password", "type": "fill", "config": { "selector": { "testId": "login-password" }, "value": "{{secret.crmPassword}}" } },
    { "id": "sign-in", "type": "click", "config": { "selector": { "testId": "login-submit" }, "waitForNavigation": true } },
    { "id": "signed-in", "type": "wait", "config": { "for": "visible", "selector": { "testId": "current-user" } } }
  ],
  "reconcile": [
    { "id": "find", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Customers/Search?Field=OperationId&Term={{operation.id}}" } },
    { "id": "match", "type": "reconcileMatch", "config": {
        "rows": { "testId": "search-result-row" },
        "key": { "attribute": "data-operation-id" },
        "equals": "{{operation.id}}",
        "ignoreCase": false,
        "emptyIndicator": { "testId": "search-no-results" },
        "maxRows": 5,
        "fields": { "customerId": { "attribute": "data-customer-id" } } } }
  ],
  "steps": [
    { "id": "open-register", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Customers/Register" } },
    { "id": "first-name", "type": "fill", "config": { "selector": { "testId": "input-first-name" }, "value": "{{record.firstName}}" } },
    { "id": "last-name", "type": "fill", "config": { "selector": { "testId": "input-last-name" }, "value": "{{record.lastName}}" } },
    { "id": "email", "type": "fill", "config": { "selector": { "testId": "input-email" }, "value": "{{record.email}}" } },
    { "id": "phone", "type": "fill", "config": { "selector": { "testId": "input-phone" }, "value": "{{record.phone}}" } },
    { "id": "company", "type": "fill", "config": { "selector": { "testId": "input-company" }, "value": "{{record.company}}" } },
    { "id": "country", "type": "select", "config": { "selector": { "testId": "input-country" }, "label": "{{record.country}}" } },
    { "id": "type", "type": "select", "config": { "selector": { "testId": "input-customer-type" }, "value": "{{record.customerType}}" } },
    { "id": "operation", "type": "fill", "config": { "selector": { "testId": "input-operation-id" }, "value": "{{operation.id}}" } },
    { "id": "terms", "type": "click", "sideEffect": false, "config": { "selector": { "testId": "input-accept-terms" } } },
    { "id": "submit", "type": "click", "sideEffect": true, "timeoutMs": 30000,
      "idempotency": { "key": "{{operation.id}}", "guarantee": "targetEnforced" },
      "config": { "selector": { "testId": "register-submit" }, "waitForNavigation": true } },
    { "id": "outcome", "type": "condition", "config": {
        "if": { "urlMatches": "**/Customers/Confirmation/**" },
        "then": [
          { "id": "customer-id", "type": "extract", "config": { "selector": { "testId": "confirmation-customer-id" }, "source": "text", "maxLength": 20 } },
          { "id": "result-kind", "type": "extract", "config": { "selector": { "testId": "confirmation-message" }, "source": "attribute", "attribute": "data-result", "maxLength": 20 } },
          { "id": "confirmation", "type": "screenshot", "config": { "label": "confirmation" } },
          { "id": "done", "type": "captureResult", "config": {
              "fields": { "customerId": "{{steps.customer-id.value}}", "registration": "{{steps.result-kind.value}}" },
              "outcome": { "status": "succeeded" } } }
        ],
        "else": [
          { "id": "error-kind", "type": "extract", "config": { "selector": { "testId": "form-error" }, "source": "attribute", "attribute": "data-error-kind", "maxLength": 40 } },
          { "id": "classify", "type": "condition", "config": {
              "if": { "valueEquals": { "left": "{{steps.error-kind.value}}", "right": "duplicate" } },
              "then": [
                { "id": "existing", "type": "extract", "config": { "selector": { "testId": "form-error" }, "source": "attribute", "attribute": "data-existing-customer-id", "maxLength": 20 } },
                { "id": "duplicate", "type": "captureResult", "config": {
                    "fields": { "existingCustomerId": "{{steps.existing.value}}" },
                    "outcome": { "status": "failed", "code": "DuplicateCustomer", "transient": false } } }
              ],
              "else": [
                { "id": "rejected", "type": "captureResult", "config": {
                    "fields": { "errorKind": "{{steps.error-kind.value}}" },
                    "outcome": { "status": "failed", "code": "RegistrationRejected", "transientWhen": { "valueEquals": { "left": "{{steps.error-kind.value}}", "right": "temporary" } } } } }
              ] } }
        ] } }
  ]
}
```

**How this example satisfies the rules:**
- **Secret:** used only in `fill.value`.
- **Side effects:** none in `setup` or `reconcile`; every `click` in `steps` declares `sideEffect` explicitly; the only side-effect step is `submit`, with `timeoutMs` ≥ 5,000 and ≤ `timeoutSeconds × 1000`.
- **Idempotency:** `submit` is dominated by the `operation` fill that sends exactly `{{operation.id}}`, so `TargetEnforced` and `OperationIdSent` are both true.
- **Reconciliation:** `matchKey: operationId` with `equals: "{{operation.id}}"` and `OperationIdSent` true. `reconcileMatch` is the last reconcile step. `advisory` carries no delay or age fields.
- **Explicit finalization:** every path through `steps` ends in a terminal `captureResult`; no result field is written twice on any path.
- **URLs:** they follow the grammar. `{{operation.id}}` and `{{record.email}}` are query values, so they are encoded and cannot change the host.
- **Matching:** `urlMatches` uses only `**`. DemoCrm's confirmation route is `/Customers/Confirmation/{id}?result=…`.
- **Classification after the side effect** (§9.1):
  - `duplicate` gives `Failed`/`DuplicateCustomer` (declared permanent business outcome);
  - `validation` and `operation-conflict` give `Failed`/`RegistrationRejected` (permanent business);
  - `temporary` gives `Indeterminate` (transient business);
  - anything unexpected (missing banner, unique-element violation, too-long id) gives `Indeterminate`.

### 12.2 Expected behaviour with DemoCrm (each line is an integration test)
- **Valid record:** `Succeeded`, with `customerId=C-0000nn`, `registration=created`; POSTs = 1.
- **Seeded email:** `Failed`/`DuplicateCustomer` (declared permanent business outcome after the side effect); evaluator `NotNeeded`. No new customer; POSTs = 1.
- **Invalid record** (bad email): `Refused`/`RecordInvalid` before any journal entry or browser work; POSTs = 0.
- **Ambiguity scenarios:** see §13.4.

### 12.3 DemoCrm test-fixture extensions (approved 2026-11-15; ADR-054)
DemoCrm remains a fully independent test fixture (ADR-027). Only `DemoCrm.Tests` and `AutomataFlow.AutomationTests` reference it; no production project depends on it, directly or transitively (architecture test unchanged).

| Extension | Behaviour | DemoCrm.Tests |
|---|---|---|
| **Exact OperationId lookup** | New `CustomerSearchField.OperationId`. The term is parsed as a GUID and normalized to `"D"` format; an exact match on the stored operation reference returns 0 or 1 customers (unique index); an invalid GUID returns no results. Search rows gain `data-operation-id` (empty when none). The operation reference is not personal data. | exact hit; no substring hit (prefix of a GUID returns nothing); case and brace variants normalize; invalid GUID gives empty; row attribute present; existing search fields unchanged |
| **Registration POST counter** | Every POST to `/Customers/Register` (including validation failures, duplicates, simulated failures and requests later aborted) increments a counter **at the start of the handler, before validation**. `GET /demo/state` adds `registrationSubmissions`. `POST /demo/reset` zeroes it. Token protection is unchanged (ADR-028); state still exposes counts only. | each outcome increments by exactly 1; reset zeroes; unauthorized state request still 404/401 |
| **Late-commit scenario** | Plus-tag `+late-commit`: the handler waits `Simulation:LateCommitDelayMs` (default 7,000) **ignoring request abortion** (`CancellationToken.None`), then stores the customer, then responds normally. This models a server that commits after the client gave up. The existing `+slow` keeps honouring abortion. | commits after the client disconnects; counter = 1; no commit if validation fails |

`docs/DEMO_CRM.md` is updated in implementation step 8.

---

## 13. Test matrix
Test IDs are referenced by §16 and the acceptance checklist. "R4" marks cases added in review round 4.

### 13.1 Unit — Application (no browser, `AutomataFlow.UnitTests`)
| Area | Scenarios |
|---|---|
| Parser | unknown properties rejected at every level (including `loop` / `forEach` / `regex` / `matches`, §16 D6); missing/unknown `schemaVersion`; wrong types; non-integer numbers; size limit; duplicate keys |
| Structure | unique step ids across setup/reconcile/steps/branches; id pattern; limits (§1.6); `onError=continue` allowed only on the six permitted types (R4: rejected on navigate/click/upload/download/captureResult/reconcileMatch); side effects only on click/upload and only in `steps`; **R4: missing `sideEffect` on a click/upload in `steps` (top level and inside branches) rejected; `sideEffect: true` in setup/reconcile rejected; warning for `waitForNavigation: true` + `false`**; side-effect `timeoutMs` ≥ 5,000; **R4: step `timeoutMs` > `timeoutSeconds × 1000` rejected**; `reconciliation` required if and only if `reconcile` exists |
| Idempotency attestation | allowed only on side-effect steps; key must be exactly `{{operation.id}}`; rejected without a dominating fill/select of `{{operation.id}}`; rejected inside a branch that the fill does not dominate; **R4: `TargetEnforced` false when only some side-effect steps attest; `OperationIdSent` computed per side-effect step** |
| Reconciliation structure (R4) | `reconcileMatch` must be the last top-level reconcile step and unique; rejected in `steps`/`setup`/branches; `captureResult` rejected in reconcile; `matchKey: operationId` requires `equals` exactly `{{operation.id}}` **and** `OperationIdSent`; `businessKey` requires exactly one required, non-sensitive `{{record.x}}`; **R5: `authority` required for authoritative and forbidden for advisory; `maxCommitLatencySeconds` 1..3600 required; `contractReference` 10..500 chars, required, no placeholders; the old `minDelaySeconds` key is rejected as unknown**; `maxEvidenceAgeSeconds` default 300 and range; `maxRows` 1..50 |
| Terminal outcomes and finalization (§16 D3) | every path through `steps` ends in a terminal captureResult (missing on one branch → error); terminal captureResult must be last in its sequence; per-section statuses (`setup`: failed; `steps`: succeeded/failed); a result field written twice on one path is rejected; once per branch is accepted; `transientWhen` must be a valid condition and counts against condition limits |
| Per action (13) | valid config; each required field; bounds; selector rules (placeholders not in css/xpath; ≤ 500 chars); download implies `AcceptDownloads`; upload `allowedExtensions` 1..20 and a subset of the server list; `maxBytes` ≤ server; extract `maxLength` bounds; **R4: `navigate.acceptStatus` bounds** |
| Templates | syntax errors (`{{`, unterminated, empty, unknown namespace); undeclared input/param/secret; step reference not dominating (later step, sibling branch, branch output used after the condition); undeclared output name; `{{operation.id}}` rendered lowercase `"D"` |
| Sink rules (§5.1) | the full matrix (secret only in `fill.value`; sensitive values in fill/select/condition only; **R4: rejected in `reconcileMatch.equals` and `.fields`**); taint through mixed templates; checks at validation **and** at run time |
| URL templates (§6.1) | placeholder in scheme/host/port/query key/fragment rejected; `{{record.x}}` cannot become a host; encoding of `/ ? # & = % \ @ :` and spaces in path segments and query values; `.`, `..`, `%2e`, `%2E%2e` segment values rejected; control characters and lone surrogates rejected; final URL must keep origin; length limit; no double encoding (`%25` stays literal) |
| URL canonicalization (R4) | **U1** literal origin with uppercase host, `:443`, trailing dot or path rejected at validation; **U2** url parameter `https://CRM.test:443/` binds to `https://crm.test` and `{{params.x}}/Login` → `https://crm.test/Login` (no `//`); parameter with a path, query, fragment or userinfo rejected; IDN parameter becomes an A-label; **U3** literal `\`, space, tab, non-ASCII and `%2f`/`%5c`/`%2e` in literal segments rejected; **U4** empty path placeholder → `UnsafeUrlValue`; empty query value accepted (`Term=`); **U5** `AbsoluteUri` round-trip mismatch → `UnsafeUrlValue`; **U6** segment-aware prefix (`/Customers/SearchAdmin` does not satisfy literal segment `/Customers/Search`); **U7** `name+commit-fail@example.test` in a query value becomes `name%2Bcommit-fail%40example.test` |
| `urlMatches` glob (§8.2) | `*` vs `**` across `/`; literal `.`, `+`, `(`; query and fragment included in the subject; patterns with `? { } [ ] \` rejected; uppercase scheme/host in absolute patterns rejected; ordinal comparison; same results as Playwright's glob for a shared corpus (checked in the real-browser `wait.url` tests); pattern length limit; pathological patterns (`**a**a**a…`) complete within bound |
| Parameters (§16 D7) | `http` rejected outside Development; private IP literal rejected outside Development; url parameter not in `allowedHosts` cannot prefix `navigate.url`; `string` parameter in a URL scheme/authority rejected; `string` parameter length 1..200; records cannot supply or override parameters |
| Conditions | each predicate; all/any/not truth tables; child-count and depth limits; `not` with ≠ 1 child rejected; empty `then` rejected; nested branch sequences validated with full rules |
| Records | each input type, required, maxLength (**R4: default 1,000 when omitted**), sensitive flag, control characters/lone surrogates rejected |
| Results | `ResultTooLarge` (fields, per-field, total); sensitive values rejected |
| Uploads | size limit (declared and actual); extension allow-list on the sanitized name; content-type/extension mismatch; signature sniffing (renamed `.exe` as `.pdf` rejected); executable types never allowed even if listed |
| Versioning and hashing (§11, §16 D5) | canonical bytes and hash stable under reordering, whitespace and escaping variants; changed by any semantic change; golden values for the DemoCrm sample; duplicate keys rejected before canonicalization; `ValidatedWorkflow.Create` refuses a wrong expected hash; `WorkflowVersion` lifecycle and immutability |
| Limits parity (R4) | **L1** every numeric bound in `docs/workflow-schema-v1.json` equals its `WorkflowLimits` constant; **L2** the validator rejects value+1 and accepts the boundary for every limit in §1.6; **L3** server options (`SessionSeconds`, `Uploads.MaxBytes`, `ClockSkewSeconds`, `MaxReconcileFailures`, `MaxAmbiguousAttempts`) validated at startup with the documented ranges |
| Host rules (moved, §16 D1) | the existing Phase 3 rule/normalizer/IP tests keep passing unchanged after the move |

**Repeat safety (§10.5), one test per rule plus properties (R4/R5):** completed attempts are `ExecutionFenced` unless the row says otherwise.
| ID | History (operation O; "T" = targetEnforced+OperationIdSent snapshot) | Expected |
|---|---|---|
| E1 | entry with another OperationId | NeedsReview / HistoryInvalid |
| E2 | duplicate attempt numbers; Completed without outcome | NeedsReview / HistoryInvalid |
| E3 | one `Started` entry | InFlight |
| E4 | empty | Allowed / NoPriorAttempt |
| E5 | #1 Normal Succeeded | NotNeeded / Succeeded |
| E6 | #1 Normal Failed `DuplicateCustomer` (business, permanent, SE Started) | NotNeeded / BusinessFailure |
| E7 | #1 Normal Failed `ElementNotUnique` **after** side effect (legacy/defective) | treated as ambiguous: not NotNeeded |
| E8 | #1 Normal Failed transient, SE NotStarted | Allowed / NoSideEffectStarted |
| E9 | #1 Normal Failed permanent, SE NotStarted | NotNeeded / PermanentFailureBeforeSideEffect |
| E10 | #1 Indeterminate (T), current T | Allowed / TargetEnforcedIdempotency |
| E11 | #1 Indeterminate (T) → #2 Reconcile Found | NotNeeded / ReconciledFound (**A1 beats A5**) |
| E12 | #1 Indeterminate (T) → #2 Reconcile NeedsReview (AmbiguousMatch) | NeedsReview (**A2 beats A5**) |
| E13 | #1 Indeterminate (T) → #2 Normal Failed permanent SE NotStarted | NeedsReview / PermanentFailureWithOpenAmbiguity (**C1 overlap**) |
| E14 | #1 Indeterminate (not T, v1) ; current v2 is T | **not** Allowed by A5 → ReconcileRequired / NeedsReview (**version binding**) |
| E15 | #1 Indeterminate (T) → #2 Indeterminate (not T) ; current T | not Allowed by A5 |
| E16 | #1 Indeterminate → #2 Reconcile NotFound advisory | NeedsReview / AdvisoryNotFound |
| E17 | #1 Indeterminate (OperationIdSent false) → #2 Reconcile NotFound authoritative, matchKey operationId | NeedsReview / KeyNotSent |
| E18 | authoritative NotFound with ObservedAt < finish + delay + skew | ReconcileTooEarly with RetryAfter = finish + delay + skew |
| E19 | authoritative NotFound exactly at finish + delay + skew | Allowed / AuthoritativeNotFound |
| E20 | authoritative NotFound older than maxEvidenceAge | ReconcileRequired / EvidenceExpired |
| E21 | #1 Indeterminate → #2 NotFound auth (valid) → #3 Normal Failed transient SE NotStarted (login) | Allowed while #2 still fresh; ReconcileRequired once expired |
| E22 | #1 Indeterminate → #2 auth NotFound → #3 Indeterminate | #2 ignored (it precedes A\* = #3) → ReconcileTooEarly/ReconcileRequired |
| E23 | #1 Indeterminate → #2 Reconcile Failed permanent | NeedsReview / ReconcileNeedsReview |
| E24 | #1 Indeterminate → 3 × Reconcile (Failed transient / Cancelled / Abandoned) | NeedsReview / ReconcileBudgetExhausted; 2 × → ReconcileRequired |
| E25 | #1 Abandoned **with** checkpoint, then fenced (`ProcessDeathConfirmed`); no reconcile section, not T | NeedsReview / NoSafePath |
| E26 | #1 Abandoned **without** checkpoint | Allowed / NoSideEffectStarted |
| E27 | #1 SideEffect Unknown | treated as Started and unfenced → AwaitingFence |
| E28 | Abandoned A\* with checkpoint, later fenced by `ProcessDeathConfirmed` at time P | latency wait measured from the dispatch horizon P (P is committed only after unit-empty **and** socket drain; never from `StartedAt + timeoutSeconds`) |
| E29 | no Normal attempt; #1 Reconcile Found | NotNeeded / ReconciledFound |
| E30 | no Normal attempt; #1 Reconcile NotFound | Allowed / NoSideEffectStarted |
| E31 | history supplied out of order | same decision as ordered (ordering by AttemptNumber) |
| E32 | clock skew: ObservedAt inside the skew allowance | ReconcileTooEarly |
| E34 | 3 ambiguous attempts (all T), current T, MaxAmbiguousAttempts 3 | NeedsReview / AmbiguityBudgetExhausted (A4b beats A5); with 2 → Allowed |
| E35 (R5) | #1 Abandoned **with** checkpoint, unfenced; current T | AwaitingFence (**S1b beats A5**: no takeover even with idempotency) |
| E36 (R5) | #1 Completed `Indeterminate` with `ExecutionFenced = false` (cleanup could not confirm revocation) | AwaitingFence |
| E37 (R5) | #1 unfenced → late `CompleteAsync(Indeterminate, ExecutionFenced = true)` at L | fenced; FenceTime = L; A-rules apply |
| E38 (R5) | #1 unfenced → late `CompleteAsync(Succeeded)` | S3 NotNeeded |
| E39 (R5) | authoritative NotFound observed **before** DispatchHorizon(A\*) + latency + skew (including a lookup observed while A\* was still unfenced) | ReconcileTooEarly; never Allowed |
| E40 (R5) | a valid `ConfirmFenceAsync(HumanConfirmed)` committed at H (§7.5.6 evidence) | fenced; FenceTime = DispatchHorizon = H; actor, method (closed list incl. `DrainVerified` with drain report), reference recorded; `ContainerRemovedVerified` or `UnitEmptyVerified` without a drain report rejected |
| E41 (R7/R8) | authoritative NotFound; latency 10 s, skew 1 s; A\* fenced by `ProcessDeathConfirmed` at P (drain report present) **or** by `ExecutorEgressRevoked` at F | TooEarly before P + 11 (or F + 11); Allowed at ≥ P + 11 (or F + 11); a `ProcessDeathConfirmed` evidence **without** a drain report is rejected by the journal, so the attempt stays unfenced → AwaitingFence |
| E42 (R7) | A\* abandoned with checkpoint, then fenced by process death; no reconcile yet | still ambiguous: ReconcileRequired (or A5 / NeedsReview), **never** N3 or NotNeeded; side-effect state and outcome unchanged by the fence |
| E33 | property test: random histories (10,000 generated) | no `Allowed` while an ambiguous attempt is open unless A5 or A6e preconditions hold; **no `Allowed` and no admitted reconcile while any attempt is unfenced**; every history yields exactly one decision |

### 13.2 Unit — Automation (fake `IBrowserSession`, in-memory journal, no browser)
- **Registry:** completeness (13↔13), duplicate handler, unknown type, config-type mismatch.
- **Handlers:** each of the 13 calls exactly the expected `IBrowserSession` operation with the right arguments and `OperationOptions`. A sensitive `extract` calls `RegisterSensitive`. `upload` never passes a path.
- **`reconcileMatch` (R4):**
  - **M1** 0 rows + empty indicator → NotFound;
  - **M2** 0 rows, no indicator → Failed/LookupInconclusive;
  - **M3** one exact match among substring rows (`ann@` vs `joann@`) → Found, with fields from the right row;
  - **M4** two exact matches → NeedsReview/AmbiguousMatch;
  - **M5** rows > maxRows → NeedsReview/TooManyRows;
  - **M6** rows present, none exact → NotFound;
  - **M7** ignoreCase on/off;
  - **M8** trimming;
  - **M9** empty `equals` → VariableUnresolved;
  - **M10** no key values in log entries.
- **Executor:**
  - sequential order; setup → main; setup → reconcile, with **no main steps** after any reconcile finding;
  - `onError` continue/fail on allowed types; `steps.x.failed`;
  - terminal outcome set exactly once (`TrySetTerminal`); run-time field overwrite raises `ResultOverwrite`; cancellation after the terminal outcome changes nothing; cleanup failures do not change the outcome;
  - **classification after side effect (R4):**
    - **X1** `ElementNotUnique`, **X2** `ExtractTooLong`, **X3** `NavigationBlocked`, **X4** `HttpError`, **X5** `ResultTooLarge` in the terminal captureResult, **X6** `VariableUnresolved` after the checkpoint: each → `Indeterminate`;
    - **X7** declared permanent business failure → `Failed`;
    - **X8** transient business failure → `Indeterminate`;
    - **X9** cancellation → `Indeterminate`;
    - **X10** the same errors before the checkpoint → `Failed`/`Cancelled`;
  - **journal and checkpoint (R4):**
    - **J1** the checkpoint is awaited and durable **before** the handler: the fake journal records ordering, and the fake session asserts no click before the checkpoint;
    - **J2** checkpoint throws → handler never invoked → `Failed`/`CheckpointFailed`, `SideEffect = NotStarted` in the result;
    - **J3** checkpoint written but acknowledgement lost (the journal stores it, then throws) → result `Failed`, journal shows the checkpoint → evaluator treats the attempt as ambiguous;
    - **J4** fenced checkpoint (lease lost) → handler not invoked;
    - **J5** `CompleteAsync` fails → diagnostic only; the entry later becomes Abandoned and, having a checkpoint, stays unfenced (blocks the operation);
    - **J6** `ReserveAsync` while another attempt is in flight → `Refused`/AttemptInFlight, no session created;
    - **J7** two concurrent `ExecuteAsync` calls for the same operation → exactly one admitted;
    - **J8** refusals (RepeatNotAuthorized, AttemptInFlight, RecordInvalid, PolicyViolation) are never in the history and do not change the next decision (I1);
    - **J9** `MarkObservationAsync` runs before the first reconcile step, and `ObservedAt` equals the journal time;
    - **J10** all result timestamps come from the journal clock;
    - **J11** a reconcile attempt is cancelled → `Cancelled` → counts toward the A4 budget;
  - **intent guard:**
    - `Normal` with an open ambiguity and no evidence gives `Refused`/RepeatNotAuthorized, and the session factory is **never called**;
    - `Normal` with `TargetEnforcedIdempotency` or `AuthoritativeNotFound` evidence runs;
    - `Reconcile` without a `reconcile` section is refused;
  - **step timeouts (R4):**
    - **T1** `OperationOptions` is a total deadline for `SubmitAsync` (click + load share one budget);
    - **T2** a side-effect click timeout → `Indeterminate`;
    - **T3** a non-side-effect `navigate`/`click` timeout ends the attempt (continue is impossible by validation, and the executor rejects it if it is bypassed);
  - private network: no request field exists; a rule matching `DevelopmentPrivateNetworkOrigins` in Development is allowed; the same configuration outside Development is rejected at startup;
  - **binding (R4):** `timeoutSeconds` > server `SessionSeconds` → `Refused`/WorkflowTimeoutExceedsServerLimit before the browser starts;
  - tamper check; session always disposed; no values in log entries or journal entries.

### 13.3 Integration — real Chromium (`AutomataFlow.AutomationTests`, verification container)
- **DemoCrm sample:** success, duplicate and invalid-record scenarios (§12.2), each asserting `registrationSubmissions`.
- **Fixture workflows:**
  - download and upload round-trip with content hash; an upload rejected by signature (POSTs = 0);
  - screenshot artifact; a sensitive extract masked in a later screenshot (pixel test);
  - conditions against real pages (`urlMatches` with query strings, `elementVisible`, `attributeEquals`);
  - wait/delay.
- **URL parity (R4):**
  - **U8** for a corpus of resolved URLs, the page URL Chromium reports after navigation has the same origin and path segments as the validated `AbsoluteUri`;
  - **U9** DemoCrm search with a `+` email finds the right customer.
- **Step timeouts** (§16 D2):
  - a non-side-effect `wait`/`extract` step timeout followed by further successful steps in the same session (`onError: "continue"`);
  - a **side-effect** step timeout against a fixture form that never responds ends the attempt `Indeterminate`, with a checkpoint in the journal;
  - **R4 T4:** `SubmitAsync` with `timeoutMs: 5000` against a slow fixture fails at ≈ 5 s, not at `ActionSeconds` + `NavigationSeconds`;
  - a session-terminating `timeoutSeconds` ends the run `Cancelled` (no side effect) or `Indeterminate` (after one).

### 13.4 Reconciliation ambiguity scenarios (real Chromium + DemoCrm)
Every scenario:
- resets DemoCrm;
- drives the executor attempt by attempt with an in-memory journal shared across attempts;
- asks the `RepeatSafetyEvaluator` for the next step exactly as Phase 5 will;
- asserts the decision sequence;
- asserts **both** the customer count **and** `registrationSubmissions`.

The submission counter catches a resubmission even when DemoCrm rejects it as a duplicate (C6). Delays use the test's fake journal clock where possible; DemoCrm's own wait is real.

**Variant A: the shipped sample** (`targetEnforced`, advisory reconcile by operation id)

| Case | Expected sequence | Final assertions |
|---|---|---|
| A1 `+commit-fail` | #1 `Indeterminate` → A5 Allowed → #2 `Succeeded` `registration=replayed` → S3 NotNeeded | customers +1; **POSTs = 2** (the second is an authorized, deduplicated replay); same customer id |
| A2 `+fail-2` | #1, #2 `Indeterminate` (A5 Allowed each time) → #3 `Succeeded` `registration=created` | customers +1; POSTs = 3 |
| A3 `+commit-fail`, reconcile first | #1 `Indeterminate` → Phase-5-style choice to reconcile → #2 Reconcile `ReconciledFound` (by operation id) → A1 NotNeeded | customers +1; **POSTs = 1** |
| A4 `+fail-1`, reconcile first (R4) | #1 `Indeterminate` → #2 Reconcile `ReconciledNotFound` (advisory) → A5 Allowed (idempotency, not the advisory NotFound) → #3 `Succeeded` `created` | customers +1; POSTs = 2 |

**Variant B: no `idempotency`, no operation-id fill, advisory reconcile by business key (email)** (a typical site)

| Case | Expected sequence | Final assertions |
|---|---|---|
| B1 `+commit-fail` | #1 `Indeterminate` → A7 ReconcileRequired → #2 Reconcile `ReconciledFound` (exact email) → A1 NotNeeded | customers +1; **POSTs = 1** |
| B2 `+fail-2` | #1 `Indeterminate` → ReconcileRequired → #2 `ReconciledNotFound` (advisory) → A6a **NeedsReview** | customers +0; **POSTs = 1** |
| B3 guard bypass | after B2, call `ExecuteAsync(Normal)` anyway | `Refused`/RepeatNotAuthorized; session factory never called; nothing journaled; POSTs still 1 |
| B4 reconcile never falls through | any Reconcile attempt | `Steps` contains no main-step ids; no checkpoint; POSTs unchanged by the reconcile |
| B5 substring collision (R4) | seed `joann+commit-fail@example.test` … record `ann+commit-fail@example.test`: #1 `Indeterminate` (stored) → Reconcile: the search shows both, exact match = 1 → Found | the found customer id is ann's, not joann's; POSTs = 1 |
| B6 version drift (R4) | #1 runs variant B (no key) `+commit-fail` → `Indeterminate`; #2 is requested with the variant A definition (T) | evaluator: A5 not satisfied (#1 snapshot not T) → ReconcileRequired; `ExecuteAsync(Normal)` → `Refused`; **POSTs = 1** |

**Variant C: as B, but `evidence: "authoritative"` with `authority: { maxCommitLatencySeconds: 10, contractReference: "docs/DEMO_CRM.md commit-latency contract", maxEvidenceAgeSeconds: 60 }`** (DemoCrm's documented bound is 8 s with the test configuration; the evaluator runs on a fake journal clock, because nothing commits late in these cases)

| Case | Expected sequence (F = FenceTime of A\* = its fenced completion; skew 1 s in tests) | Final assertions |
|---|---|---|
| C1 `+fail-2` | #1 `Indeterminate` (fail 1), F1 → A7 TooEarly (RetryAfter F1+11) → #2 Reconcile at F1+5 → NotFound → A6c TooEarly → #3 Reconcile at ≥ F1+11 → NotFound → A6e Allowed → #4 Normal `Indeterminate` (fail 2), F4 → #3 is now before A\* and ignored → A7 TooEarly → #5 Reconcile at ≥ F4+11 → NotFound → Allowed → #6 Normal `Succeeded` `created` → NotNeeded | customers +1; **POSTs = 3** (#1, #4, #6) |
| C2 `+commit-fail` | #1 `Indeterminate` → reconcile at ≥ F1+11 → `ReconciledFound` → NotNeeded | customers +1; **POSTs = 1** |
| C3 early lookup ignored | NotFound observed before F+latency+skew | evaluator never returns Allowed on it; `ExecuteAsync(Normal)` is Refused |
| C4 expired evidence (R4) | valid NotFound, then the clock advances beyond 60 s → ReconcileRequired | Normal Refused until a fresh lookup |
| C5 no contract (R5) | the same workflow with `authoritative` but **no** `authority` block, or with `minDelaySeconds` instead | rejected by the validator; it never runs |

**Variant D: late commit (R4/R5, DemoCrm `+late-commit`, `LateCommitDelayMs` 7,000; submit `timeoutMs` 5,000; authoritative with `maxCommitLatencySeconds: 10`; real clock)**

| Case | Expected sequence | Final assertions |
|---|---|---|
| D1 | #1: the submit times out at ≈ 5 s → `Indeterminate`; the session is disposed (egress revoked, `ExecutionFenced`) at F; DemoCrm commits at ≈ 7 s, still within the contract. An immediate reconcile (< F+11) gives NotFound but A6c TooEarly. A reconcile at ≥ F+11 gives **Found** → NotNeeded | customers +1; **POSTs = 1**; demonstrates that the contract's latency bound covers a server that commits after the client gave up |
| D2 | as D1 but advisory evidence: an immediate reconcile → NotFound → NeedsReview | customers +1 (the late commit); POSTs = 1; no resubmission |

**Crash and concurrency scenarios (R4, real Chromium + DemoCrm, in-memory journal with a controllable lease):**

| Case | Steps | Final assertions |
|---|---|---|
| K1 crash after commit | variant B `+commit-fail`: the test harness cancels the executor's process-level token **and** suppresses `CompleteAsync` immediately after the submit POST is observed (simulated kill); the lease expires → `Abandoned` with checkpoint, **unfenced** | `ExecuteAsync(Normal)` **and** `ExecuteAsync(Reconcile)` → `Refused`/AwaitingFence (not NoPriorAttempt); the test then records `ConfirmFenceAsync(ProcessDeathConfirmed)` → ReconcileRequired → reconcile → Found → NotNeeded; **POSTs = 1** |
| K2 crash before checkpoint | kill after login, before the checkpoint | Abandoned without checkpoint → N3 Allowed → #2 `Succeeded`; POSTs = 1 |
| K3 concurrent workers | two `ExecuteAsync(Normal)` for the same operation at once | one runs, one `Refused`/AttemptInFlight; **POSTs = 1** |

**Fencing scenarios (R5; F1–F6 real Chromium + DemoCrm with a controllable journal clock and a "freeze" hook in the executor test seam; F7–F8 journal conformance):**

| Case | Steps | Final assertions |
|---|---|---|
| F1 frozen after checkpoint (the §16 R13 trace) | variant C `+fail-1`: Worker A's checkpoint succeeds, then the freeze hook blocks A before the handler; the journal clock passes A's lease expiry; Worker B calls `ExecuteAsync(Normal)` and `ExecuteAsync(Reconcile)` | both B calls `Refused`/AwaitingFence; A's entry `Abandoned`, unfenced; **POSTs = 0** while A is frozen |
| F2 A resumes | continue F1: release the hook; A clicks (its proxy session is still valid, since nothing in-process can stop it), gets the 503, disposes (egress revoked) and calls `CompleteAsync` after lease loss | late completion accepted; A fenced at L; **POSTs = 1** (A's only); B's next evaluation → TooEarly until L + 11 → reconcile → NotFound → Allowed → B `created`; **POSTs = 2, customers +1** |
| F3 A never resumes | F1, then a fake supervisor kills A's (test) unit, observes it empty and reports `ProcessDeathConfirmed` evidence for A's incarnation | fenced at P (the commit time, after unit-empty **and** socket drain); dispatch horizon P; POSTs = 0 so far; B reconciles at ≥ P + 11 → NotFound → Allowed |
| F4 checkpoint after lease loss | A frozen **before** its checkpoint; the lease expires; B is admitted (no checkpoint → fenced by `NoCheckpoint`) and starts; A resumes | A's `SideEffectStartingAsync` → `JournalFenced`; A's handler is never invoked; **POSTs = 1** (B's only) |
| F5 short lease | a checkpoint requested with remaining lease < step timeout + margin | refused; handler not invoked; `Failed`/`CheckpointFailed` |
| F6 egress revoked after dispose | after `DisposeAsync`, a request on the old context's proxy credentials | 407; `EgressRevoked == true` only because the barrier completed (V1–V9); completion records `ExecutionFenced` |
| F7 conformance: unfenced blocks reservation | `ReserveAsync` while an Abandoned-with-checkpoint entry is unfenced | refused (AwaitingFence) for both intents, atomically with the abandonment |
| F8 conformance: conditional checkpoint | concurrent abandonment and checkpoint on the same entry (1,000 iterations) | exactly one wins; never both "abandoned without checkpoint" and "checkpoint accepted" |
| F9 conformance: evidence must match (R6) | `ConfirmFenceAsync` with another incarnation's id or unit, an unknown method, or a missing reference/attestation (human) | rejected; the attempts stay unfenced |
| F10 conformance: kill request is not a fence (R6) | the kill request is recorded; the lease expires; no confirmation arrives | still `AwaitingFence`; `ReserveAsync` refused; no `FencedAt` |
| F11 conformance: fence covers the whole incarnation (R6) | two unfenced attempts (different operations) of incarnation X; one valid confirmation for X | both fenced with the same `FencedAt` (the commit time); attempts of incarnation Y unaffected |
| F12 conformance: timestamps (R6) | confirmation observed at t1 (supervisor clock), committed at t2 (journal clock) | `FencedAt == t2`; t1, the kill-request time and the lease expiry are stored only as diagnostics; the latency wait counts from t2 |
| F13 fence after NeedsReview (R6) | `FenceWaitSeconds` elapses → record `NeedsReview`; then a valid fence arrives | no automatic attempt; after an explicit operator requeue the evaluator applies its rules from `FencedAt` |
| F14 human confirmation (R6) | `HumanConfirmed` by a tenant user / without a platform-operator role | rejected; with a platform operator, closed-list method, reference and attestation → accepted, all fields audited |
| F15 no proxy state persisted (R6) | inspect the egress proxy after session creation; restart it | credentials exist only in memory; a new proxy instance refuses the old credentials (407) |

**Revocation barrier tests (R7, §7.6; `AutomataFlow.AutomationTests` with a fake upstream server that timestamps every received byte, plus real Chromium; fault-injection hooks in the proxy test seam):**

| ID | Condition | Scenario | Expected |
|---|---|---|---|
| V1 | 1 | after `RevokeAsync` completes: a new CONNECT and a new plain-HTTP connection with the revoked credentials (extends the existing Phase 3 tests `Revoked_sessions_can_no_longer_authenticate` and the CONNECT 407 test) | 407; the upstream server receives no connection |
| V2 | 1 | admission race: a hook pauses a handler between credential check and admission; revoke; resume (1,000 iterations with random interleavings) | admission fails, or the connection is inside the barrier; **never** a live connection or an upstream byte after `RevokeAsync` returned `Completed` |
| V3 | 2, 3 | **established keep-alive CONNECT tunnel:** request 1 is sent through the tunnel and delivered; the tunnel is kept open; revoke; the browser side then writes request 2 into the same tunnel | upstream receives **zero bytes** of request 2 (its pump write fails `TryEnter`, or was admitted before Lr and completed before Ta, in which case its bytes arrive before the RST, never after); the upstream socket sees RST and the browser side sees the connection closed, both **before** `RevokeAsync` returns |
| V4 | 2, 3 | real Chromium: an open WebSocket sending a frame every 10 ms and an HTTP/2 keep-alive `fetch` loop to a fixture server; then `DisposeAsync` | the fixture records no frame or request after the barrier's completion timestamp |
| V5 | 3 | queued plain-HTTP POST: a hook pauses the handler after reading the head (and the full body in `leftover`), before the upstream connect; revoke; resume | `OpenAsync` fails `TryEnter`; no upstream connection, zero bytes; the handler exits; `RevokeAsync` waited for it |
| V6 | 3 | tunnel pump: a hook pauses after a buffer was read from the browser side, before the upstream write; revoke; resume | the write fails `TryEnter`; the buffer is discarded; zero bytes upstream |
| V7 | 2, 3 | abortive close, on Linux (verification image kernel) **and** Windows: the upstream server stops reading, so data sits in the proxy's kernel send buffer with **no send outstanding**; revoke; the server then resumes reading | the server receives RST and **none** of the bytes that were still in the **socket send queue** (the zero window guarantees they had not been passed below TCP); no retransmission follows. This verifies E2 for the undocumented Linux zero-linger behaviour (§7.6.4). It makes **no** claim about segments already below the socket layer (see V12) |
| V12 | E2 ≠ E3 | **destroyed is not delivered:** a test relay acknowledges and accepts the request's segments at once (so, from our TCP's view, they have left the socket layer), but holds them for 3 s before forwarding them to the fake target; the barrier runs right after the send returns | at `Completed` our socket is gone from the TCP table (E2 proven), yet the fake target receives the complete request about 3 s **after** Ta and `FencedAt`. This shows that socket destruction does not prove physical delivery or departure, and that the horizon argument depends on the latency attestation covering such delays (H1–H4) |
| V8 | 4 | a handler that ignores cancellation (fault hook) | `RevokeAsync` → `TimedOut` after `RevocationTimeoutSeconds`; `EgressRevoked == false`; `CompleteAsync(ExecutionFenced: false)`; the attempt stays unfenced → `AwaitingFence` |
| V9 | 4 | ordering: the journal fake records the time of `CompleteAsync`, and the proxy records the barrier phases | `CompleteAsync(ExecutionFenced: true)` happens strictly after barrier step 4; a slow context close (simulated) starts only after the barrier, and no request reaches the fixture during it |
| V10 | — | upstream socket options | every upstream socket has the fail-fast user timeout set (Linux `TCP_USER_TIMEOUT` = 30,000 ms, Windows `TCP_MAXRT` = 30 s, read back via `getsockopt`). This is asserted as **configuration only**: no test or rule treats it as a retransmission deadline |
| V11 | — | regression | every existing Phase 3 proxy test passes unchanged |

**Write-gate tests (R8, §7.6.1–7.6.3; deterministic hooks in `UpstreamChannel`):** **H-pre** before `TryEnter`; **H-in** after a successful `TryEnter`, immediately before the send syscall (the exact point between the final admission check and the write); **H-post** after the send returned, before `Exit`.

| ID | Scenario | Expected |
|---|---|---|
| WG1 | the review-round-8 interleaving, admitted case: pause a tunnel pump write at **H-in**; call `RevokeAsync` | B1 completes (`TryAdmit` and new `TryEnter` now fail), but `RevokeAsync` stays at B2 and **no socket is aborted** while the write is paused. Release the hook: the send executes; the fake upstream receives exactly that write's bytes **before** the RST; then B3/B4 run and the result is `Completed`. Recorded order: Lr < send < `Exit` < Ta < `Completed` |
| WG2 | the review-round-8 interleaving, not-admitted case: pause at **H-pre**; call `RevokeAsync` and let it **complete**; release | `TryEnter` returns `false`; **no send syscall is issued** (the socket-level send counter is unchanged); zero bytes reach upstream; the connection is aborted |
| WG3 | pause at **H-post** (send done, `Exit` not yet called); revoke | B2 waits until `Exit`; abort happens after; `Completed` |
| WG4 | admitted write blocked on a full send buffer (upstream not reading, send outstanding); revoke | B2 waits until `RevocationTimeoutSeconds` → `TimedOut` → `EgressRevoked = false` → attempt unfenced → `AwaitingFence` (no fence is ever recorded while a send is outstanding) |
| WG5 | stress, 10,000 iterations: each write carries a sequence number and its admission order; revocation fires at a random moment; random delays at H-pre/H-in/H-post | every byte the upstream receives belongs to a write whose `TryEnter` linearized **before** Lr; no byte is received after the RST; every `Completed` barrier finished B2 before B3 |
| WG6 | source scan | no `Write*`/`Send*` call on an upstream `Stream`/`Socket` in `Network/Proxy` outside `UpstreamChannel`; no `CancellationToken` passed to the gated send |

**Dispatch-horizon race tests (R8, §7.6.7; a test-only TCP relay between the egress proxy and a fake target simulates network transit δ. Bytes the relay accepted before our RST are delivered after δ, as packets already in the network would be. The fake target commits a complete request after a processing delay p. The journal runs on a controllable clock.)**

| ID | Scenario | Expected (invariant) |
|---|---|---|
| H1 | **request received just before the barrier completes, committed after it:** the barrier is triggered while the request's last byte is in the relay (δ = 2 s), p = 5 s; contract L = 10 s ≥ δ + p | the target commits at ≈ Ta + 7 s, **after** `FencedAt`; a reconcile at `FencedAt` + 11 s returns **Found** → NotNeeded. The evaluator returns TooEarly for every lookup before `FencedAt` + 11 s. **POSTs = 1** |
| H2 | **request cut by the RST:** the barrier fires mid-body (Ta inside the body) | the target never receives a complete request and never commits; a reconcile at `FencedAt` + 11 s returns NotFound → Allowed. After waiting another L, the target's commit log is still empty, so the eligible NotFound was not followed by a commit |
| H3 | randomized, 1,000 iterations: barrier timing, δ ∈ [0, 3] s, p ∈ [0, 6] s, all with δ + p ≤ L | for every committed request, commit time ≤ `FencedAt` + L; for every iteration where an eligible authoritative lookup returned NotFound, **no commit ever follows** |
| H4 | **negative control (proves the test can fail):** the same as H1 but with δ + p = 14 s > declared L = 10 s (a wrong attestation) | the eligible lookup at `FencedAt` + 11 s returns NotFound and the target commits afterwards; the H3 invariant checker **reports the violation**. This test asserts that the checker detects it. It documents that safety depends on a correct contract (limitation 2) |

**Phase 5 supervisor tests (specified now, implemented in Phase 5 on Linux in the verification container and on Windows):**
- **P1:** the Worker plus real Chromium in a unit; the Worker is frozen with `SIGSTOP`; the supervisor kills the unit, waits for `populated 0`, then confirms.
- **P2:** the Worker is killed alone, leaving orphaned Chromium processes; the unit is not empty, so there is no fence until the supervisor kills the orphans and the unit empties.
- **P3:** a process that cannot be killed (simulated by a fake unit that never empties) → no fence → `NeedsReview` after `FenceWaitSeconds`.
- **P4:** the containment handshake fails (breakaway allowed / not in the unit) → the Worker refuses to start.
- **P5:** a host reboot (boot id changed) confirms the fence.
- **P6:** the supervisor itself is unavailable → no fence, no retry.
- **P7, lingering socket (Linux):** the Worker is killed while an upstream socket holds unsent data in its **socket send queue** for a server that has stopped reading (zero window). The socket lingers (`ESTABLISHED`/`FIN_WAIT_1`). The drain finds it transmit-capable, destroys it by cookie, re-enumerates and only then confirms. After the server resumes, it receives RST and none of the queued bytes; no retransmission follows (E2). On Windows the same scenario gives **no fence** while the connection lingers; it is confirmed only after the OS ends it, otherwise `NeedsReview`.
- **P8, trickling peer:** the server advertises a tiny non-zero window and keeps acknowledging (so `TCP_USER_TIMEOUT` never fires); the Worker is killed. **Linux:** the drain destroys the socket. Without the drain the server would keep receiving bytes, which proves the user timeout is not a deadline. **Windows:** no fence until the deadline, then `NeedsReview`; nothing is terminated.
- **P9, no destroy capability (Linux):** the supervisor lacks `CAP_NET_ADMIN` in the namespace, or the kernel lacks destroy support. The containment handshake fails, so the Worker refuses to start. If the capability disappears later (`EPERM`/`EOPNOTSUPP` from `SOCK_DESTROY`, or a destroy that was silently skipped and still shows the socket on re-enumeration), there is no fence and the attempt goes `AwaitingFence` → `NeedsReview`.
- **P10, ledger problems:** the ledger is missing, truncated or corrupt → no fence.
- **P11, connection identity and port reuse** (the supervisor must never mistake a replacement for the original, never kill an unrelated connection, and never call an ambiguous connection drained):
  - **P11a (Linux isolation):** after the unit is empty, an unrelated process in **another** namespace opens a connection with the identical tuple. The drain enumerates only the incarnation's namespace, so the unrelated connection is never seen or touched; the original orphan is destroyed; the fence is confirmed.
  - **P11b (Linux exact cookie):** in a test namespace, the original socket closes and a new socket with the same tuple appears; a `SOCK_DESTROY` carrying the **old** cookie is issued. The kernel refuses it and the new socket stays up. This empirically verifies cookie matching.
  - **P11c (Linux broken isolation):** a socket with a ledgered tuple but a different cookie appears in the incarnation's namespace → ambiguity → **no fence**; nothing is destroyed.
  - **P11d (Windows replacement):** after the Worker dies and its connection closes, another process opens a connection with the identical tuple (different `liCreateTimestamp`) → ambiguous → **no fence**, and the replacement is **not** terminated (the supervisor never terminates on Windows). When the replacement closes, a later poll finds the tuple absent → drained → fence.
  - **P11e (Windows identity read):** a Worker connection whose own-PID row is missing or duplicated at ledger time is aborted unused before any payload (W5).
  - **P11f (orphan identity after death):** on each OS, verify that an orphaned connection keeps its recorded identity (cookie / `liCreateTimestamp`) after the owning process dies. If an OS build does not keep it, P11 fails, and on that OS every non-absent ledger entry is ambiguous (fail-closed) until it disappears.
- **P12, below-socket queueing (Linux, Phase 5 test host with `CAP_NET_ADMIN`):** add a 2 s `netem` delay on the namespace's egress interface; send a request and kill the Worker immediately; drain and confirm. The fake target receives the request about 2 s after `FencedAt`, so E2 is proven but E3 is not. With L ≥ 2 s + processing, an authoritative `NotFound` is never eligible before the commit; with L < 2 s (negative control) the H-invariant checker reports the violation.
- **P13, fail-closed matrix:** each condition in the §7.6.5 fail-closed table is injected (missing or corrupt ledger; torn final record, which is accepted; lost namespace handle; interrupted or failing dump; `EPERM`; `EOPNOTSUPP`; silently skipped destroy; identity mismatch; duplicate rows; `GetExtendedTcpTable` failure; partial drain with one socket left; supervisor restart mid-drain). Every case except the torn final record yields **no fence**, and the journal rejects any drain report not `Drained` for every entry.

**Journal conformance suite (R5/R6, `tests/Shared/AttemptJournalConformance/`):**
- reservation exclusivity, including unfenced entries;
- fencing of every call;
- the conditional checkpoint with the lease margin (F5, F7, F8);
- late completion accepted only as information;
- `ConfirmFenceAsync` evidence validation, incarnation scope, timestamps and audit fields (F9–F14);
- release of refusals;
- single clock;
- history ordering and organization scoping.

It runs against `InMemoryAttemptJournal` in Phase 4, and unchanged against PostgreSQL in Phase 5 (§7.4).

**Other ambiguity tests:**
- A reconcile that itself fails transiently (navigation timeout) gives `Failed`, then ReconcileRequired, never Allowed; the 3rd consecutive failure gives NeedsReview (A4).
- A reconcile against a page that shows neither rows nor the empty indicator (login page) gives `LookupInconclusive` and is never treated as NotFound.
- A cancellation after the submit click started gives `Indeterminate`, and the same rules apply.

### 13.5 Security
| Scenario | Expectation |
|---|---|
| Record value `x/../../Admin`, `..`, `%2e%2e`, `a?b=c`, `a#b`, `@evil.test`, `//evil.test`, empty, `\evil` in a path or query placeholder | encoded or rejected (`UnsafeUrlValue`); destination origin and literal path segments unchanged |
| Workflow navigating to a host not in `allowedHosts` | rejected at validation; also `NavigationBlocked` at run time when constructed directly |
| Url parameter pointing to `169.254.169.254` / `127.0.0.1` | binding rejects (outside Development, or not in the trusted Development list) or the Phase 3 proxy blocks; no internal hit |
| Attempt to grant private-network access via workflow JSON, parameters, record or request | impossible: no such field exists; unknown properties rejected; only `DevelopmentPrivateNetworkOrigins` (server config, Development only) grants it |
| Url parameter whose public name later resolves to a private IP | blocked by the Phase 3 egress proxy |
| Redirect from an allowed page to an internal address | blocked by Phase 3 (exercised through the workflow path) |
| Secret value | never in log entries, ILogger output, results, step records, journal entries or error messages; masked in screenshots (pixel test); rejected in every non-fill sink |
| Secret used in a condition to probe its value | rejected at validation |
| Sensitive extract | redacted from later messages; element masked in later screenshots; rejected in results |
| Script or code in JSON (`"type":"script"`, `"evaluate"`, `"javascript:"` URL, CSS engine prefixes, regex keys) | rejected by parser/validator; no handler exists |
| Upload reference `../../etc/passwd`, an executable renamed `.pdf`, an oversized file | rejected (`UploadRejected`) before the checkpoint; no filesystem path ever reaches Playwright |
| Oversized/deeply nested definition, 10,000 conditions | rejected by limits before execution |
| Ambiguous attempt resubmitted without evidence | `Refused`/RepeatNotAuthorized; no browser session (§13.4 B3, B6, K1) |
| Journal history from another organization or operation | `HistoryInvalid` (E1); Phase 5 adds the database-level cross-tenant test |

### 13.6 Architecture (`DependencyGraphTests`, `BrowserEngineSourceTests`)
- Api still ↛ Automation, and still has no Playwright package.
- Application stays package-free, including the moved Network types, `RepeatSafetyEvaluator`, `IAttemptJournal` and `WorkflowLimits`.
- No `Microsoft.Playwright` in `Automation/Workflows` (handlers use `IBrowserSession` only).
- No script/eval APIs (existing scan, extended to `QueryRowsAsync`); no `System.Text.RegularExpressions` usage in the workflow condition, glob, URL template or `reconcileMatch` code (source scan).
- Every `ActionCatalog` type has a handler and a schema entry.
- Domain has no new references.
- **No production project references DemoCrm** (ADR-027, unchanged after the fixture extensions).
- **Journal implementations:** `InMemoryAttemptJournal` exists only in test projects, and no production assembly contains any `IAttemptJournal` implementation in Phase 4 (R15).
- **No in-process lease checks are claimed as fencing:** the executor contains no "check lease, then click" logic presented as a guarantee (review checklist item, R13).

---

## 14. Dependencies and project references
- **No new projects, no new packages.** `System.Text.Json` is in the BCL.
- **Project references are unchanged:** Domain ← Application ← Automation ← Worker; the Api stays independent of Automation; DemoCrm stays independent of everything.

| Project | Phase 4 additions |
|---|---|
| Domain | `Workflows/WorkflowVersion` (entity rules only) |
| Application | `Workflows/Definition`: records, parser, validator, `WorkflowLimits`, templates, URL template grammar, canonicalizer and encoder, glob matcher, sink rules, conditions model, `ActionCatalog`, `RecordValidator`, canonical JSON and hash, `ValidatedWorkflow`<br>`Workflows/Execution`: request/result/error/evidence contracts, `RepeatSafetyEvaluator`, `RepeatSafetyPolicy`<br>ports `ISecretProvider`, `IExecutionFileProvider`, `IAttemptJournal` (with `WorkerIncarnation`, `FenceEvidence`, `FenceSource` contracts)<br>`Network`: the five moved pure policy types |
| Automation | `Workflows/`: registry, 13 handlers, `WorkflowExecutor` with journal integration, intent guard and checkpointing, `VariableResolver`, `ConditionEvaluator` runtime, terminal-outcome state, upload checks, `AddWorkflowEngine`<br>`Browser/` additive extensions: `OperationOptions` (total deadline), `UploadAsync`, `CountAsync`, `IsVisibleAsync`, `CurrentUrl`, `QueryRowsAsync`, `RegisterSensitive`, `DevelopmentPrivateNetworkOrigins` option |
| DemoCrm (test fixture) | OperationId search, `data-operation-id` on rows, registration POST counter in `/demo/state`, `+late-commit` scenario (§12.3) |
| Tests | UnitTests (Application + Automation unit, `InMemoryAttemptJournal`), AutomationTests (real-browser workflow, reconciliation, crash, fencing and concurrency tests), DemoCrm.Tests (fixture extensions), shared journal conformance sources (`tests/Shared/AttemptJournalConformance/`, linked into the test projects), architecture tests extended (no production `IAttemptJournal` implementation in Phase 4) |
| Not touched | Infrastructure, Api, Worker, web |

---

## 15. Implementation steps, effort, acceptance, risks and limitations

### Steps and estimate (indicative, ADR-034)
| # | Step | Days |
|---|---|---|
| 1 | Move the five pure network policy types to Application (no behaviour change; all 508 Phase 3 tests green before continuing) | 0.25 |
| 2 | Phase 3 extensions: `OperationOptions` as a total deadline (including `SubmitAsync`), `UploadAsync`, `CountAsync`, `IsVisibleAsync`, `CurrentUrl`, `QueryRowsAsync`, `RegisterSensitive`, trusted `DevelopmentPrivateNetworkOrigins`, **egress revocation barrier, upstream write gate, `UpstreamChannel`, connection ledger with OS connection identity and fail-fast socket options (§7.6)** (+ tests incl. V1–V12, WG1–WG6, H1–H4) | 2.0 |
| 3 | Definition model, strict parser, `WorkflowLimits`, canonicalizer and hash (golden values), `ValidatedWorkflow`, `WorkflowVersion` rules | 0.75 |
| 4 | Templates (dominance, sink rules, taint), URL grammar, canonical origins, encoder and final validation, glob matcher, parameter binding, `RecordValidator` | 1.25 |
| 5 | Action catalog (13 descriptors + config validation), finalization, `sideEffect` declaration rules, idempotency and reconciliation rules, condition model and evaluator | 1.0 |
| 6 | `IAttemptJournal` contract (fencing, fence sources, late completion), `WorkerIncarnation` / `FenceEvidence` types and evidence validation, `InMemoryAttemptJournal`, fault-injecting journals and a fake supervisor (tests), shared conformance suite (incl. F9–F15), `RepeatSafetyEvaluator` (rules S0–N4 incl. S1b) + E1–E40 | 1.75 |
| 7 | Registry, 13 handlers, executor (reservation, guard, checkpoint, classification, reconcile without fall-through, completion, logging) + unit tests | 1.25 |
| 8 | DemoCrm fixture extensions + DemoCrm.Tests; `docs/DEMO_CRM.md` | 0.5 |
| 9 | DemoCrm sample and variants B/C/D, real-browser integration, ambiguity, crash, fencing (F1–F6) and concurrency scenarios (§13.4), security tests | 1.75 |
| 10 | JSON Schema file + parity tests, `docs/WORKFLOWS.md`, ADR/ROADMAP updates, verification, commit | 0.5 |
| | **Total** | **11.0 (range 10.0–13.0)** |

### Acceptance checklist
1. Decisions:
   - [x] D1–D7 approved (2026-11-12) and recorded (ADR-037 to ADR-043); sink rules accepted and recorded (ADR-044).
   - [x] Round 2–9 decisions recorded (ADR-045 to ADR-060); final approval granted 2026-11-15.
2. [x] The five pure policy types moved to Application; runtime enforcement unchanged in Automation; all 508 Phase 3 tests green after the move. (Step 1, 2026-11-15: two consecutive green container runs, 523/523 including 15 new architecture tests.)
3. [ ] Phase 3 extensions are additive. (Step 2 implemented and verified 2026-11-15; the horizon races H1–H4 were delivered with step 6 (ADR-064: real proxy and barrier, scaled times, H3 as 24 concurrent real races plus 1,000 model races). The Windows identity path is implemented but verified only on Linux in the container; Windows verification is pending. Step 2 follow-up after the read-only audit of `e253a65`:
   - **D1:** upstream sockets are always closed abortively;
   - **D2:** with an explicit deadline the failure screenshot is skipped or bounded by the remaining budget;
   - **D3:** XPath is rejected for row-child selectors because it can escape the row.

   Each fix has a regression test.)
   - a step timeout is one total deadline and never terminates the session;
   - uploads are content-only;
   - probes never throw for missing elements;
   - `QueryRowsAsync` is bounded and script-free;
   - the egress revocation barrier meets all four conditions; every upstream send is admitted through the write gate, so no send begins after Lr (§7.6; V1–V12, WG1–WG6); the horizon races hold (H1–H4); the connection ledger with OS identity is written before any payload; `EgressRevoked` is true only for a completed barrier;
   - private-network access comes only from trusted Development server configuration.
4. [ ] Strict, versioned parser with limits from `WorkflowLimits`; unknown properties, schema versions, loops and regular expressions rejected; non-integer numbers rejected. (Step 3 implemented 2026-11-15, pending review:
   - `StrictJsonParser` (size before parsing, BOM, comments, trailing commas and content, depth, duplicate names after unescaping, integer-only numbers including `-0`, lone surrogates, string and property-name lengths);
   - `WorkflowDefinitionParser` dispatching on `schemaVersion`, with the schemaVersion 1 binder: every property set is closed, loop/regex keys are rejected anywhere (including inside `config`), and all errors are reported with JSON paths;
   - `CanonicalJson` and `ValidatedWorkflow` (golden bytes and hash computed by an independent reference implementation); `WorkflowVersion` lifecycle rules in Domain.

   **Deliberately left to later steps:** `config` is kept as an immutable raw object and typed per action in step 5; `allowedHosts` gets structural checks only (canonical origins and templates in step 4); `sideEffect`/`onError`/idempotency placement, the side-effect timeout minimum, finalization and condition-branch step counting are step 5.

   **Hash semantics:** the hash covers the definition as written after parsing, so an explicit default such as `"timeoutSeconds": 120` is a different definition from an omitted one, while formatting, property order and escaping are not.

   **Audit follow-up (ADR-061):** M1 lazy diagnostic paths (allocation-bounded worst case); M2 `CreateForPublication` / `Load(json, recordedHash)`; M3 frozen `SchemaV1Defaults`; L1 loop/regex keys rejected only as constructs; L2 `VerifyAgainst`; L3 one hash-format definition; L4 schema-version consistency and timestamp ordering in `WorkflowVersion`; L5 escaped paths and at most 100 errors; L6 ADR-061.)
5. [x] (Step 5, ADR-063: all items below implemented with unit tests; the URL/template components come from step 4.) Hand-written validator covers, recursively for nested branches:
   - structure and all 13 action configs;
   - templates, dominance, URL grammar and canonical origins, sink rules, parameters;
   - explicit `sideEffect` declarations, side-effect placement and timeouts;
   - idempotency attestation and reconciliation structure and keys;
   - explicit finalization and terminal-outcome rules.
6. [x] `ActionCatalog` (Application) ↔ handlers (Automation) 1:1, verified at startup and by tests; the Api uses only Application. (Step 5 catalog; step 7 handlers and `ActionRegistry` startup check, ADR-065.)
7. [ ] (Step 4: template compiler, reference/sink checker, run-time resolver, URL grammar/encoder/final validation, canonical origins, parameter binding and `RecordValidator` implemented and tested, ADR-062; applied to every action config in step 5.) Variables for `record` / `steps` / `operation` / `params` / `secret` with static checking; URL values encoded by context; dot segments, empty path values and unsafe values rejected; url parameters canonicalized; final URL validated on `AbsoluteUri`; the record can never control scheme, host or port.
8. [ ] Sink rules enforced at validation and run time; sensitive extracts redacted and masked; results bounded and never sensitive; sensitive values never in logs, results, step records, journal entries or errors. Universal secret detection is **not** claimed.
9. [x] `urlMatches` / `wait.url` glob implemented without regex, with the documented semantics and rejected characters. (Step 4; Playwright-identical `**` semantics per ADR-062. The real-browser corpus comparison with `wait.url` runs in step 9.)
10. [x] (Step 5 validator; step 7 run-time rules, ADR-065.) Exactly one terminal outcome per attempt; explicit finalization on every path; refusals are not attempts; reconciliation findings distinct from attempt outcomes and record resolution.
11. [ ] (Step 6: `RepeatSafetyEvaluator` S0–N4 with E1–E42 and the E33 property test, and the guard `Admits`; reconcileMatch evaluation and the executor guard wiring are step 7.) Repeat safety:
    - reconcile attempts never run main steps;
    - `reconcileMatch` matches exactly and turns ambiguity into NeedsReview;
    - `NotFound` never authorizes a repeat by itself;
    - `RepeatSafetyEvaluator` implements rules S0–N4 with the default NeedsReview;
    - the executor guard refuses unauthorized repeats before any browser session exists.
12. [ ] (Step 6: contract, fence sources, late completion, `ConfirmFenceAsync` evidence validation, test-only in-memory journal, fault-injecting journals, fake supervisor and the shared conformance suite incl. F7–F12, F14, F15 and J2/J3/J5 at journal level; executor integration J1–J11 is step 7.) Attempt journal contract:
    - one in-flight attempt per operation, **including unfenced attempts**;
    - a durable, fenced, lease-margin-checked side-effect checkpoint before every side-effect handler;
    - abandoned attempts judged by their checkpoint;
    - fence sources and fence times as specified (§7.2), with late completion and `ConfirmFenceAsync`;
    - fence evidence validated per §7.5 (incarnation and unit match, closed method list, human evidence fields, `FencedAt` only at commit), proven by F9–F15 with a fake supervisor; supervisor tests P1–P13 specified for Phase 5;
    - a single journal clock;
    - evidence bound to operation id and definition snapshot;
    - the shared conformance suite green against the in-memory journal;
    - no production `IAttemptJournal` implementation in Phase 4.
12a. [x] (Validator: step 3; latency from the dispatch horizon: step 6, E18–E20, E28, E39, E41, H1–H4.) Authoritative reconciliation requires an `authority` block (target commit-latency contract); latency is measured from the dispatch horizon; validator rejects authoritative without it.
13. [x] (Step 7: §9 classification, R8 outcome rules X1–X10, upload checks before the checkpoint.) Errors classified with category and transience; after a side effect only declared permanent business outcomes give `Failed`, everything else gives `Indeterminate`; uploads checked for size, extension, content type and signature before the checkpoint.
14. [x] (Step 7, J1–J11.) Executor runs one attempt per call with a fresh Phase 3 session that is always disposed; request/result/evidence/journal contracts ready for Phase 5.
15. [ ] Immutability: deterministic canonicalization with golden values; hash verified at publish, load and execution; `WorkflowVersion` lifecycle; schemaVersion dispatch.
16. [x] (Step 8: operation-id search, POST counter, `+late-commit`; DemoCrm.Tests; `docs/DEMO_CRM.md` incl. the commit-latency contract.) DemoCrm fixture extensions implemented and tested; no production project depends on DemoCrm.
17. [x] (Step 8: `docs/workflows/democrm-register-customer.json`; §12.2, A1–A4, B1–B6, C1–C4 (C5 is a validator test), D1, D2, K1–K3 and F1–F6 in real Chromium with exact customer and POST counts.) DemoCrm sample and variants pass every §12.2 and §13.4 scenario in real Chromium, with exact customer **and** POST-submission counts, and zero unauthorized resubmissions.
18. [ ] Test matrix (§13) implemented; 0 warnings; all tests green in the verification container (two consecutive runs).
19. [ ] Docs:
    - `docs/WORKFLOWS.md` (authoring guide incl. explicit side effects, idempotency attestation, evidence levels and match keys, limitations and the cost of safety);
    - `docs/workflow-schema-v1.json`;
    - `docs/DEMO_CRM.md`;
    - ADRs and ROADMAP;
    - secrets audit; separate commit; clean tree.

### Risks
| Risk | Mitigation |
|---|---|
| Template/condition language creep towards scripting | Closed sets, no regex/functions in v1 (ADR-042); validator rejects unknown keys; source scan bans regex in these components |
| Incomplete taint leaks PII or secrets | Taint carried in `ResolvedValue`; sink passed into every resolution; checks at validation **and** run time; log and journal entries carry no values; leak tests over all channels |
| URL encoding gaps (dot segments, double encoding, parser differentials) | Grammar with ASCII literal set + canonical origins + per-context encoding + dot-segment and empty-segment rejection + `AbsoluteUri` round-trip check + Chromium parity tests + Phase 3 guard and proxy behind it |
| Wrong repeat decisions cause duplicates | First-match evaluator with default NeedsReview; attempt-bound snapshots; journal as the only history source; executor guard; property test over random histories; real-browser scenarios assert POST counts |
| Lost results (crash) hide a side effect | Durable checkpoint before every side-effect handler; abandoned attempts judged by checkpoint; crash scenarios K1/K2 |
| Concurrent attempts for one operation | Atomic reservation, one in flight per operation, fencing; scenario K3 |
| Authors over-claim `targetEnforced`, `authoritative` or business-key uniqueness | Explicit attestation fields, validator prerequisites (`OperationIdSent`, key consistency), documentation of what each attests, admin-only publication in hosted mode (Phase 5/6) |
| Authors forget to mark a submit as a side effect | `sideEffect` mandatory on every click/upload in `steps`; warning for navigating clicks declared `false` |
| Terminal-outcome logic errors | Single `TrySetTerminal` transition; explicit finalization validated; validator-bypass tests |
| Canonicalization drift changes hashes of published versions | Golden-value tests; integer-only numbers; duplicate keys rejected; verification at publish, load and execution |
| Conditions racing with navigation | Side-effect clicks use `waitForNavigation`; `continue` forbidden on navigating actions; probes are immediate and documented; authors add `wait` steps |
| Moving network types breaks Phase 3 | Pure move with namespace update; all 508 existing tests must stay green before continuing |
| Changing Phase 3 signatures | Optional parameters and new methods only; existing call sites unchanged |
| More records end in `NeedsReview` for non-idempotent sites | Intended (§10.6); visible in Phase 6; authors can add authoritative reconciliation where the target supports it |
| DemoCrm-specific assumptions creeping into the engine | Variants B/C/D and fixture workflows; architecture test bans DemoCrm references from production projects |
| JSON Schema drifting from the code validator | Parity tests for enums **and** every numeric bound (ADR-055) |

### Remaining architectural limitations (accepted, documented in `docs/WORKFLOWS.md`)
1. **Attestations are trusted, not proven.** The engine cannot verify that a target really deduplicates by key, that a lookup is strongly consistent, or that a business key is unique. Wrong attestations can still cause duplicates. Hosted mode restricts who may publish them.
2. **Commit-latency contracts are attestations.**
   - An authoritative `NotFound` is only as good as the target contract cited in `contractReference`.
   - If the target violates its documented bound, a duplicate is possible.
   - Targets without such a contract must use `advisory`, which never repeats automatically (ADR-057).
3. **A frozen Worker can still dispatch once.**
   - A Worker frozen after a successful checkpoint (or inside the handler) dispatches its click when it resumes. Its in-process egress proxy resumes with it, and no in-process check can close a check-then-act window.
   - **Phase 4/5 guarantee:** this stale dispatch is the operation's *only* one. No other attempt (Normal or Reconcile) is admitted while it is unfenced, and evidence counts only from a proven dispatch horizon (ADR-056, ADR-060).
   - **Consequence:** an operation whose Worker froze or partitioned during a submit waits in `AwaitingFence` until a fence is proven. Phase 5 turns that into `NeedsReview` after `FenceWaitSeconds`.
   - **Phase 7 fix:** only an out-of-process, lease-bound egress proxy (`EgressLeaseExpired`) or container termination (`IsolationTerminated`) can prevent the stale dispatch itself and allow fully automatic takeover.
   - **Human confirmation:** a `HumanConfirmed` fence is only as reliable as the operator's verification. §7.5.6 restricts it to platform operators, a closed list of methods, a mandatory evidence reference and an audited attestation.
   - **Already-dispatched bytes:**
     - neither the revocation barrier nor a process-death fence proves that segments already handed below the socket layer have physically left the host (E3), and nothing can recall bytes already sent; they may commit;
     - the dispatch horizon is a conservative upper bound after write completion (E1) and connection destruction (E2) only. Local buffering, transit, target processing and query visibility after it are covered by the **trusted** commit-latency attestation. A host pause or suspension, or transit beyond the attested latency, can break it;
     - **no OS retransmission timeout is relied on**: `TCP_USER_TIMEOUT` / `TCP_MAXRT` do not bound a slowly acknowledging peer or lingering orphaned sockets. A process-death horizon is established only by the supervisor's socket drain (§7.6.5).
     - **Platform asymmetry:**
       - **Linux** gives exact identity through namespace isolation, can destroy lingering sockets by cookie, and so drains automatically;
       - **Windows** has no per-process network namespace and no identity-targeted abort, so the supervisor never terminates a connection there, and lingering or ambiguous connections end in `NeedsReview`;
     - cookie and orphan-identity behaviour are verified empirically (P11b, P11f), not taken from documentation;
     - the Linux zero-linger abort is undocumented in `socket(7)`, so it is verified empirically (V7) on the pinned verification kernel, and must be re-verified when the kernel changes.
   - **Supervisor dependencies:** `ProcessDeathConfirmed` relies on OS containment (cgroup v2 / Job Object without breakaway) and on hosts never being restored from memory snapshots. If either is violated (e.g. a privileged process escapes the unit, or a VM is resumed from a snapshot), the proof is invalid. Both are startup and runbook preconditions (§7.5).
4. **Undetectable side effects.** A `navigate` to a state-changing GET URL, or an auto-submitting `fill`/`select`, is invisible to the engine unless the author models it as a declared side-effect `click`.
5. **Secrets displayed by websites** can only be masked if declared in `maskSelectors`.
6. **The journal's guarantees depend on its Phase 5 implementation:**
   - unique in-flight/unfenced constraint, conditional checkpoint, fencing, durable writes, database clock, process-death supervisor;
   - Phase 4 proves the contract only with test implementations;
   - Phase 5 must pass the shared conformance suite and the K and F tests against PostgreSQL before any customer execution, and the production Worker fails closed without a durable, fencing journal (§7.4, ADR-058).

---

## Decisions (all approved 2026-11-15)
| # | Decision | Status | Binding record |
|---|---|---|---|
| D1 | Pure host/IP policy types move to Application; runtime enforcement stays in Automation | Approved | §16 D1, ADR-037 |
| D2 | Step timeouts that keep the session; content-only upload; safe page probes | Approved; amended round 4 (total deadline) | §16 D2, ADR-038, ADR-053 |
| D3 | `captureResult` as the terminal outcome mechanism for `setup`/`steps` | Approved; amended rounds 2 and 4 (reconcile finalized by `reconcileMatch`; refusals) | §16 D3, ADR-039 |
| D4 | Sections `setup` / `reconcile` / `steps`; never reconcile and steps in one attempt | Approved; amended round 2 | §16 D4, ADR-040 |
| D5 | Draft → Published → Archived; immutable published definitions; deterministic hashing | Approved | §16 D5, ADR-041 |
| D6 | No loops, no regular expressions in schemaVersion 1 | Approved | §16 D6, ADR-042 |
| D7 | Typed parameters that cannot bypass host restrictions | Approved; amended rounds 2 and 4 (trusted private-network config; canonicalization) | §16 D7, ADR-043 |
| — | Sink rules (secret vs sensitive value) | Accepted (round 1) | §16 Sink rules, ADR-044 |
| R1 | Repeat safety: deterministic evaluator, no authorization from NotFound alone, executor guard | Round 2, amended round 4 (C1) — **approved 2026-11-15** | §16 R1, ADR-045 |
| R2 | URL templates, canonicalization, encoding, final URL validation, glob semantics | Round 2, amended round 4 (I6) — **approved 2026-11-15** | §16 R2, ADR-046 |
| R3 | Private-network exceptions only from trusted server configuration | Round 2 — **approved 2026-11-15** | §16 R3, ADR-047 |
| R5 | Side-effect timeouts, sensitive extracts, results, uploads, nested validation, hash verification | Round 2 — **approved 2026-11-15** | §16 R5, ADR-048 |
| R6 | Attempt journal, single in-flight attempt, durable side-effect checkpoint, abandoned attempts, refusals | Round 4 (C2, I1) — **approved 2026-11-15** | §16 R6, ADR-049 |
| R7 | Evidence bound to operation id and definition snapshot; journal clock; evidence age and skew | Round 4 (C3, I3) — **approved 2026-11-15** | §16 R7, ADR-050 |
| R8 | After a side effect only declared permanent business outcomes are `Failed` | Round 4 (C4) — **approved 2026-11-15** | §16 R8, ADR-051 |
| R9 | Exact-match reconciliation (`reconcileMatch`), reconcile failures and budget | Round 4 (C5, I2) — **approved 2026-11-15** | §16 R9, ADR-052 |
| R10 | Total step deadline, `continue` restrictions, mandatory `sideEffect` declaration | Round 4 (I4, I5) — **approved 2026-11-15** | §16 R10, ADR-053 |
| R11 | DemoCrm test-fixture extensions in Phase 4 | **Approved by the user 2026-11-15** (scope and design) | §16 R11, ADR-054 |
| R12 | Centralized `WorkflowLimits` and numeric schema parity | Round 4 (I8) — **approved 2026-11-15** | §16 R12, ADR-055 |
| R13 | Fencing: no takeover while an attempt is unfenced; fence sources; delays from the dispatch horizon | Round 5 — **approved 2026-11-15** | §16 R13, ADR-056 |
| R14 | Authoritative evidence requires a target commit-latency contract | Round 5 — **approved 2026-11-15** | §16 R14, ADR-057 |
| R15 | Journal boundary: Phase 4 test implementations only; Phase 5 durable journal must pass the shared suite; production fails closed | Round 5 — **approved 2026-11-15** | §16 R15, ADR-058 |
| R16 | Process-death supervisor and fence-confirmation contract | Round 6 — **approved 2026-11-15** | §16 R16, §7.5, ADR-059 |
| R17 | Egress revocation barrier with upstream write gate; connection ledger and supervisor socket drain; dispatch horizon | Rounds 7–8 — **approved 2026-11-15** | §16 R17, §7.6, ADR-060 |

(Round 2 numbering skipped R4: that finding, terminal outcomes, is folded into D3 / ADR-039.)

---

## Appendix A — JSON Schema outline (`docs/workflow-schema-v1.json`)
All numeric values below are generated from, and parity-tested against, `WorkflowLimits` (ADR-055).
```jsonc
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "$id": "https://automataflow.local/schemas/workflow-v1.json",
  "type": "object", "additionalProperties": false,
  "required": ["schemaVersion", "name", "allowedHosts", "steps"],
  "properties": {
    "schemaVersion": { "const": 1 },
    "name": { "type": "string", "pattern": "^[a-z][a-z0-9-]{2,63}$" },
    "description": { "type": "string", "maxLength": 500 },
    "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 3600, "default": 120 },
    "parameters": { "type": "array", "maxItems": 20, "items": { "$ref": "#/$defs/parameter" } },
    "allowedHosts": { "type": "array", "minItems": 1, "maxItems": 20, "items": { "type": "string", "maxLength": 300 } },
    "inputs": { "type": "array", "maxItems": 100, "items": { "$ref": "#/$defs/input" } },
    "secrets": { "type": "array", "maxItems": 20, "items": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" } },
    "maskSelectors": { "type": "array", "maxItems": 50, "items": { "$ref": "#/$defs/selector" } },
    "reconciliation": { "$ref": "#/$defs/reconciliation" },
    "setup": { "$ref": "#/$defs/steps" }, "reconcile": { "$ref": "#/$defs/steps" }, "steps": { "$ref": "#/$defs/steps" }
  },
  "dependentRequired": { "reconcile": ["reconciliation"], "reconciliation": ["reconcile"] },
  "$defs": {
    "steps": { "type": "array", "items": { "$ref": "#/$defs/step" } },
    "step": { "type": "object", "required": ["id", "type", "config"], "additionalProperties": false,
      "properties": { "id": { "type": "string", "pattern": "^[a-z][a-z0-9-]{0,39}$" },
        "type": { "enum": ["navigate","click","fill","select","wait","extract","screenshot","upload","download","condition","delay","captureResult","reconcileMatch"] },
        "config": { "type": "object" }, "timeoutMs": { "type": "integer", "minimum": 100, "maximum": 120000 },
        "onError": { "enum": ["fail", "continue"] }, "sideEffect": { "type": "boolean" },
        "idempotency": { "$ref": "#/$defs/idempotency" },
        "description": { "type": "string", "maxLength": 500 } },
      "allOf": [ /* one "if type == X then config: $defs/X" per action;
                    "if onError == continue then type in [wait, extract, screenshot, fill, select, condition]";
                    "if sideEffect == true then type in [click, upload] and timeoutMs >= 5000" */ ] },
    "idempotency": { "type": "object", "required": ["key", "guarantee"], "additionalProperties": false,
      "properties": { "key": { "const": "{{operation.id}}" }, "guarantee": { "const": "targetEnforced" } } },
    "reconciliation": { "type": "object", "required": ["evidence", "matchKey"], "additionalProperties": false,
      "properties": { "evidence": { "enum": ["advisory", "authoritative"] },
        "matchKey": { "enum": ["operationId", "businessKey"] },
        "authority": { "type": "object", "required": ["maxCommitLatencySeconds", "contractReference"], "additionalProperties": false,
          "properties": { "maxCommitLatencySeconds": { "type": "integer", "minimum": 1, "maximum": 3600 },
            "contractReference": { "type": "string", "minLength": 10, "maxLength": 500 },
            "maxEvidenceAgeSeconds": { "type": "integer", "minimum": 30, "maximum": 3600, "default": 300 } } } },
      "if": { "properties": { "evidence": { "const": "authoritative" } } },
      "then": { "required": ["authority"] },
      "else": { "not": { "required": ["authority"] } } },
    "selector": { "oneOf": [ /* testId | css | role(+name) | text | label | placeholder | xpath; every string maxLength 500 */ ] },
    "condition": { "oneOf": [ /* all | any | not | elementExists | elementVisible | elementCountAtLeast | textEquals | textContains | attributeEquals | urlMatches | valueEquals | valueContains | valueStartsWith | isEmpty */ ] },
    "urlPattern": { "type": "string", "maxLength": 500, "pattern": "^(https?://|\\*\\*/)[^?{}\\[\\]\\\\]*$" },
    "parameter": { "type": "object", "required": ["name", "type"], "additionalProperties": false,
      "properties": { "name": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" }, "type": { "enum": ["url", "string"] } } },
    "input": { "type": "object", "required": ["name", "type"], "additionalProperties": false,
      "properties": { "name": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" },
        "type": { "enum": ["string", "email", "integer", "decimal", "boolean", "date", "file"] },
        "required": { "type": "boolean" }, "maxLength": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 1000 },
        "sensitive": { "type": "boolean" } } },
    "navigate": { "type": "object", "required": ["url"], "additionalProperties": false,
      "properties": { "url": { "type": "string", "maxLength": 2048 },
        "acceptStatus": { "type": "array", "maxItems": 10, "items": { "type": "integer", "minimum": 400, "maximum": 599 } } } },
    "delay": { "type": "object", "required": ["milliseconds"], "additionalProperties": false,
      "properties": { "milliseconds": { "type": "integer", "minimum": 1, "maximum": 30000 } } },
    "extract": { "type": "object", "required": ["selector", "source"], "additionalProperties": false,
      "properties": { "selector": { "$ref": "#/$defs/selector" }, "source": { "enum": ["text", "attribute"] },
        "attribute": { "type": "string", "maxLength": 100 }, "sensitive": { "type": "boolean" },
        "maxLength": { "type": "integer", "minimum": 1, "maximum": 10000, "default": 1000 } } },
    "upload": { "type": "object", "required": ["selector", "file", "allowedExtensions"], "additionalProperties": false,
      "properties": { "selector": { "$ref": "#/$defs/selector" }, "file": { "type": "string" },
        "allowedExtensions": { "type": "array", "minItems": 1, "maxItems": 20, "items": { "type": "string", "pattern": "^\\.[a-z0-9]{1,10}$" } },
        "maxBytes": { "type": "integer", "minimum": 1 } } },
    "captureResult": { "type": "object", "required": ["fields"], "additionalProperties": false,
      "properties": { "fields": { "type": "object", "maxProperties": 50, "additionalProperties": { "type": "string", "maxLength": 2000 } },
        "outcome": { "$ref": "#/$defs/outcome" } } },
    "outcome": { "type": "object", "required": ["status"], "additionalProperties": false,
      "properties": {
        "status": { "enum": ["succeeded", "failed"] },
        "code": { "type": "string", "pattern": "^[A-Z][A-Za-z0-9]{0,63}$" },
        "transient": { "type": "boolean" },
        "transientWhen": { "$ref": "#/$defs/condition" } } },
    "reconcileMatch": { "type": "object", "required": ["rows", "key", "equals", "emptyIndicator"], "additionalProperties": false,
      "properties": { "rows": { "$ref": "#/$defs/selector" },
        "key": { "oneOf": [
          { "type": "object", "required": ["attribute"], "additionalProperties": false, "properties": { "attribute": { "type": "string", "maxLength": 100 } } },
          { "type": "object", "required": ["text"], "additionalProperties": false, "properties": { "text": { "$ref": "#/$defs/selector" } } } ] },
        "equals": { "type": "string", "maxLength": 2000 }, "ignoreCase": { "type": "boolean" },
        "emptyIndicator": { "$ref": "#/$defs/selector" },
        "maxRows": { "type": "integer", "minimum": 1, "maximum": 50, "default": 20 },
        "fields": { "type": "object", "maxProperties": 50, "additionalProperties": {
          "type": "object", "required": ["attribute"], "additionalProperties": false, "properties": { "attribute": { "type": "string", "maxLength": 100 } } } } } }
    /* plus one config definition per remaining action, matching §2.
       Rules JSON Schema cannot express are enforced only by the hand-written validator (§1.5). */
  }
}
```
The full file is produced in implementation step 10. Until then the hand-written validator is authoritative and the schema is for editor assistance.

---

## 16. Approval record and binding clarifications — AUTHORITATIVE
- **D1–D7** were approved on 2026-11-12.
- **Review round 1** added the sink rules.
- **Review round 2** (2026-11-12) added R1–R3 and R5 and amended D3, D4 and D7.
- **Review round 3** (2026-11-15) found 6 Critical and 8 Important issues.
- **Review round 4** (2026-11-15) resolves them with R6–R12 and amendments to D2, D3, D7, R1 and R2. The user approved the DemoCrm fixture scope (R11) on 2026-11-15.
- **Review round 5** (2026-11-15) adds R13 (fencing), R14 (authoritative evidence contract) and R15 (journal boundary), and amends R6 and R7.
- **Review round 6** (2026-11-15) adds R16 (process-death supervisor and fence-confirmation contract).
- **Review round 7** (2026-11-15) adds R17 (egress revocation barrier and dispatch horizon), after finding that Phase 3 revocation alone did not satisfy the fence conditions.
- **Review round 8** (2026-11-15) amends R16 and R17:
  - closes the check-then-write race with an upstream write gate;
  - proves the barrier horizon;
  - withdraws the retransmission-timeout horizon, which OS documentation does not support, and replaces it with a connection ledger and a supervisor socket drain.
- **Review round 9** (2026-11-15) amends R14, R16 and R17:
  - separates write completion (E1), connection destruction (E2) and physical departure (E3), and claims only E1 and E2;
  - defines the dispatch horizon as a conservative upper bound;
  - extends the latency attestation to local buffering, transit, processing and visibility;
  - adds OS-level connection identity (Linux dedicated namespace plus cookie; Windows tuple plus creation timestamp, with no termination) and a fail-closed ambiguity rule.

This section takes precedence over every other section. Status: **final approval granted by the user on 2026-11-15; all decisions D1–D7 and R1–R17 are binding; implementation authorized, step by step, with no verification waived.**

### D1: pure policy moves, enforcement stays (ADR-037)
- **What moves:** only the five pure definitions and parsers move to `AutomataFlow.Application.Network`: `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy`, `NormalizedHost`.
- **What stays in Automation, unchanged:** all **runtime enforcement**: `RequestGuard`, the route/WebSocket guards, redirect inspection, `EgressProxyServer` (DNS, IP checks, pinning, quotas), fail-closed gating, the Chromium flags and the sandbox.
- **Acceptance gate:** the full Phase 3 suite (508 tests) must pass unchanged, apart from namespaces, before any other Phase 4 work starts.

### D2: a step timeout never terminates the session (ADR-038, amended by ADR-053)
- **How timeouts are applied:** a step's `timeoutMs` reaches Playwright via `OperationOptions` as **one total deadline** for the whole step, including composite operations (`SubmitAsync` click + load). When it expires, only that step fails. The context, page, cookies and login stay intact.
- **What still closes the session:** only the session-terminating timeouts.
- **Error mapping:** a step timeout produces `Timeout` (navigation) or `ElementNotFound` (element wait), transient, with the session still usable. On a side-effect step it makes the attempt `Indeterminate` (R5, R8).
- **Uploads:** content only, from `IExecutionFileProvider` as an in-memory `FilePayload`.
- **Safe page checks:** `CountAsync`, `IsVisibleAsync` and the bounded `QueryRowsAsync` never throw for missing elements. `CurrentUrl` is read raw for conditions but always logged redacted.
- **Sensitive registration:** `RegisterSensitive` (additive) lets sensitive extracts be redacted and masked.

### D3: exactly one terminal outcome per attempt (ADR-039, amended in rounds 2 and 4)
| Rule | Definition |
|---|---|
| Outcome model | `WorkflowOutcome` is the terminal outcome of **one attempt**. Normal: `Succeeded`, `Failed`, `Indeterminate`, `Cancelled` (only without a side effect). Reconcile: `ReconciledFound`, `ReconciledNotFound`, `NeedsReview`, `Failed`, `Cancelled`. `Refused` marks a call that was **not** an attempt (§7.3). |
| Attempt outcome versus record resolution | A terminal outcome closes the attempt only. Record resolution belongs to Phase 5 and is derived from the journal history via `RepeatSafetyEvaluator`. `ReconciledNotFound` is an **intermediate finding** for the record (R1). |
| Sources of a terminal outcome | (a) a step error under `onError: "fail"`; (b) a terminal `captureResult` (`setup`, `steps`); (c) the `reconcileMatch` finding (`reconcile`); (d) a failure after `StartAsync` but before the first step (secrets, browser start); (e) cancellation or a session-terminating timeout. **There is no implicit success.** Refusals (record invalid, tampered definition, binding failure, repeat not authorized, attempt in flight, journal unavailable) are not outcomes of an attempt. |
| Explicit finalization | **Every path** through `steps` ends in a terminal `captureResult`. The `reconcile` section ends in exactly one top-level `reconcileMatch`. A run can never be finalized "by accident" by running out of steps. |
| Terminal `captureResult` | Stops execution immediately; must be the **last** step of its sequence. |
| Outcomes allowed per section | `setup`: `captureResult` `failed` only. `steps`: `succeeded` / `failed`. `reconcile`: no `captureResult`; the finding comes from `reconcileMatch`. |
| Field rules | Non-terminal `captureResult` only adds fields. A field name is written at most once on any path (static check), with a run-time `ResultOverwrite` backstop. `reconcileMatch.fields` are produced only on `Found`. Results are bounded (R5). |
| Single assignment | The executor holds the outcome in a state object with one `TrySetTerminal(...)` transition that refuses a second set. Cleanup and journal-completion failures are diagnostics only. A cancellation arriving after the terminal outcome has been set cannot change it. |
| Side effects | After a side effect started, only a declared permanent business outcome gives `Failed`; everything else gives `Indeterminate` (R8). |

### D4: sections (ADR-040, amended in round 2)
- **Order:** `setup`, then **either** `reconcile` (Intent `Reconcile`) **or** `steps` (Intent `Normal`), never both in one attempt. A reconcile attempt never continues into main steps, whatever its finding.
- **When reconciliation runs:** the engine never decides this; Phase 5 does, using `RepeatSafetyEvaluator`.
- **Side effects:** neither `setup` nor `reconcile` may contain side-effect steps.

### D5: lifecycle and deterministic hashing (ADR-041)
- **Lifecycle:** `WorkflowVersion`: `Draft` → `Published` → `Archived`, with no transitions back. Published and Archived definitions, schema versions and hashes are read-only.
- **Canonical form:**
  - UTF-8 without BOM;
  - properties sorted by ordinal UTF-16 code-unit order;
  - no insignificant whitespace;
  - minimal string escaping (`"`, `\`, control characters as `\u00XX`, and `\b \f \n \r \t`);
  - integer-only numbers in plain decimal;
  - `true` / `false` / `null` literal;
  - duplicate properties are rejected *before* canonicalization.
- **Hash:** `sha256:` + lowercase hex, verified at publish, on load (`ValidatedWorkflow.Create`) and at execution start (`WorkflowTampered` on mismatch); recorded in every attempt snapshot. Golden-value tests pin canonical bytes and hash.

### D6: no loops, no regular expressions in schemaVersion 1 (ADR-042)
The parser rejects any `loop`/`repeat`/`forEach`/`while` keys and any `regex`/`matches` predicate. The URL glob (R2) and `reconcileMatch` are implemented without regular expressions. Introducing either requires a new schemaVersion and an ADR.

### D7: typed parameters that cannot bypass host restrictions (ADR-043, amended in rounds 2 and 4)
| Type | Validation at bind time | Where usable |
|---|---|---|
| `url` | canonicalized origin (§6.2): no path (a single trailing `/` is removed), query, fragment or credentials; default port removed; host normalized; `https` only outside Development; never a private IP literal outside Development | only as the whole origin prefix of a URL template **and only if that same parameter is listed in `allowedHosts`** |
| `string` | length 1–200, no control characters | non-URL sinks (`fill`, `select`, condition operands, selector values), and URL path segments / query values (encoded per R2); never in a URL scheme or authority |

- **Source:** parameters come from the job configuration, **never from record data**.
- **Private network (R3):** a parameter can never grant private-network access.
- **Enforcement:** bound `url` parameters become `EgressPolicy` rules, after which all Phase 3 runtime enforcement applies unchanged.

### Sink rules: secret versus sensitive value (ADR-044)
- **Secrets** are accepted **only** in `fill.value`; everywhere else they are rejected at validation and at run time.
- **Sensitive inputs and sensitive extracts** are accepted in `fill.value`, `select` and condition operands. They are rejected in `navigate.url`, `captureResult.fields`, `reconcileMatch.equals` / `.fields`, selector values, `screenshot.label` and `upload.file`.
- **Taint propagates.** Logs and journal entries never contain sensitive values, and execution events contain no input or output values at all.
- **Screenshot masking** covers sensitive fills, sensitive-extract elements, the Phase 3 built-ins and declared `maskSelectors`. It does **not** detect every secret a site displays.

### R1: repeat authorization (ADR-045, amended in round 4)
- **No fall-through:** a `Reconcile` attempt runs `setup` and `reconcile` only.
- **What `NotFound` means:** "this exact-match lookup, at `ObservedAt`, found nothing". It **never authorizes a repeat side effect by itself.**
- **Deterministic evaluator:** `RepeatSafetyEvaluator` applies the §10.5 rules **in order, first match wins**: integrity (S0), in flight (S1), unfenced (S1b, R13), empty (S2), resolved (S3); then, if an ambiguity is open, A1 Found → A2 reconcile needs review → A3 broken workflow → A4 reconcile budget → A4b ambiguity budget → A5 idempotency → A6 latest NotFound → A7 reconcile next → A8 NeedsReview; otherwise N1–N3; and finally the **mandatory default N4 NeedsReview**.
- **Ambiguity first:** while an ambiguity is open, resolution and no-side-effect rules are never consulted.
- **Allowed only with explicit safety evidence:**
  - `NoPriorAttempt`;
  - `NoSideEffectStarted` (no checkpoint in any Normal attempt and the latest one failed transiently, was cancelled or abandoned);
  - `TargetEnforcedIdempotency` (every ambiguous attempt's own snapshot **and** the current version attest it and send the key);
  - `AuthoritativeNotFound` (R7 conditions).
- **Executor guard:** a `Normal` call without `Allowed` is `Refused` **before a browser session exists**; refusals are never journaled.
- **No idempotency assumed:** attestations are explicit, documented and, in hosted mode, restricted to admins (Phase 5/6).

### R2: URL templates, canonicalization, encoding and glob semantics (ADR-046, amended in round 4)
- **Grammar:** URL templates follow the §6.1 grammar. The origin is a canonical literal or a whole url parameter; placeholders are allowed only in path segments and query values. Query keys are literal and fragments are forbidden.
- **Literals:** ASCII from the defined character sets; no `\`, whitespace or control characters; no literal dot segments or encoded `/`, `\`, `.`.
- **Canonical origins:** literal origins must already be canonical; url parameters are canonicalized at bind time (§6.2).
- **Encoding:** values are encoded once with RFC 3986 encoding for their context. Dot-segment values, empty path values, control characters and lone surrogates are rejected.
- **Final URL check** on the exact `Uri.AbsoluteUri`, which must round-trip unchanged:
  - the exact canonical scheme, host and port;
  - segment-aware literal path prefix;
  - no userinfo or fragment;
  - ≤ 2,048 chars;
  - matches `allowedHosts`.

  Then Phase 3 `RequestGuard` and the egress proxy apply unchanged.
- **The record never controls the scheme, host or port.**
- **Glob:** `urlMatches` and `wait.url` use the §8.2 glob:
  - `*` (no `/`), `**` (any), everything else literal;
  - `? { } [ ] \` rejected; uppercase scheme/host in absolute patterns rejected;
  - ordinal comparison; the subject is the full URL including query and fragment;
  - no regular expressions; semantics identical to Playwright's for this subset (tested against a shared corpus).

### R3: private-network access only from trusted server configuration (ADR-047)
- `WorkflowExecutionRequest` has **no** private-network flag.
- **Where it comes from:** a rule may allow private-network destinations only if the engine runs in Development **and** its origin exactly matches an entry in `Automation:Browser:DevelopmentPrivateNetworkOrigins`. That is server configuration, validated at startup and rejected outside Development.
- **Who cannot grant it:** no workflow, job, parameter, record, request or customer can grant private-network access.

### R5: additional validation (ADR-048)
- **Side-effect step timeouts:** `timeoutMs` ≥ 5,000; a timeout of a side-effect step is ambiguous and ends the attempt `Indeterminate`; `onError: "continue"` is forbidden on side-effect steps (and, per R10, on every navigating action).
- **Sensitive extracts:** registered with the redactor and masked immediately; never in results, step records, the journal or logs; `ExtractTooLong` rather than silent truncation.
- **Results:** at most 50 fields, 2,000 chars each, 32 KB total (`ResultTooLarge`); never sensitive; never logged; persisted organization-scoped by Phase 5.
- **Uploads:**
  - content only from the organization-scoped `IExecutionFileProvider`;
  - size ≤ min(step, server) limit;
  - sanitized name with an extension in the step allow-list, which must be a subset of the server list, which excludes executables and scripts;
  - content type consistent with the extension;
  - signature sniffing for known types;
  - all checks before the side-effect checkpoint;
  - failures give `UploadRejected`.
- **Nested conditions:** expressions validated recursively (depth ≤ 5, ≤ 20 predicates, `all`/`any` 1..10 children, `not` exactly 1); branch sequences validated with the full rules of their section; step nesting ≤ 3; `then` non-empty.
- **Hash verification:** at publish, on load and at execution start (D5).

### R6: attempt journal, single in-flight attempt, durable checkpoint (ADR-049)
- **The journal is the only source of attempt history.** `WorkflowExecutionRequest` carries no history.
- **One in flight:** `ReserveAsync` atomically marks lease-expired `Started` attempts `Abandoned`, then refuses a new attempt for the same (organization, operation) while another is `Reserved`, `Started` or **unfenced** (R13).
- **Checkpoint before side effect:**
  - before every side-effect handler, the executor awaits `SideEffectStartingAsync`, which returns only after a durable, fenced write;
  - if the call fails, throws or is fenced, the handler is **not** invoked and the attempt ends `Failed`/`CheckpointFailed`;
  - if the write happened anyway (acknowledgement lost), the journal shows a checkpoint and the attempt is treated as ambiguous.
- **Abandoned attempts:** side-effect state comes from the checkpoint:
  - no checkpoint means `NotStarted` (provable, because the handler never runs without one);
  - a checkpoint means ambiguous **and unfenced until a fence is established** (R13);
  - unreadable means `Unknown`, treated as `Started` and unfenced.
- **Refusals** are released, never journaled, and cannot affect any later decision.
- **Phase 5** implements the journal in PostgreSQL with a unique in-flight constraint, fencing and lease renewal, and re-runs the J and K tests against it.

### R7: evidence binding, clock and freshness (ADR-050)
- **Binding:** every `AttemptSummary` carries its `OperationId` and an `AttemptSnapshot` (definition hash, version, `TimeoutSeconds`, `TargetEnforced`, `OperationIdSent`, reconcile policy) recorded at `StartAsync` from the version that actually ran. Every `ReconciliationEvidence` carries the policy snapshot of the version that ran the lookup.
- **Mismatches:** any history entry with another operation id makes the history invalid (`NeedsReview`/`HistoryInvalid`).
- **No retroactive claims:** idempotency is judged on each ambiguous attempt's **own** snapshot; a newer version can never vouch for an older attempt. An `operationId`-keyed `NotFound` counts only if the ambiguous attempt sent the key.
- **Clock:** all attempt timestamps and the evaluator's `now` come from the journal clock. `ObservedAt` is recorded immediately before the reconcile section starts.
- **Freshness:** an authoritative `NotFound` authorizes a repeat only if A\* is fenced (R13) and:
  - `ObservedAt ≥ DispatchHorizon(A*) + maxCommitLatencySeconds + ClockSkewAllowance` (R14, R17), and
  - `now − ObservedAt ≤ maxEvidenceAgeSeconds`.

  No time is derived from an assumed process timeout.

### R8: classification after a side effect (ADR-051)
After the first side-effect checkpoint of a Normal attempt:
- `Failed` only through a terminal `captureResult` with status `failed` whose resolved transience is `false` (a declared permanent business outcome);
- `Succeeded` only through a terminal `captureResult` `succeeded`;
- **every other end is `Indeterminate`**, keeping the original code. This covers browser, engine, technical, policy, timeout and cancellation errors, transient business outcomes, and run-time failures while evaluating the terminal `captureResult`.

### R9: exact-match reconciliation (ADR-052)
- **Structure:** the `reconcile` section ends with exactly one top-level `reconcileMatch`; `captureResult` is not allowed there.
- **Keys:** `matchKey: operationId` requires `equals` = `{{operation.id}}` and `OperationIdSent`; `businessKey` requires exactly one required, non-sensitive record input.
- **Findings:**

  | Situation | Result |
  |---|---|
  | Exactly one exact match | `ReconciledFound` |
  | Zero rows **with** the empty indicator visible, or rows present but no exact match | `ReconciledNotFound` |
  | Zero rows **without** the empty indicator | `Failed`/`LookupInconclusive` (transient) |
  | More than one exact match | `NeedsReview`/`AmbiguousMatch` |
  | More rows than `maxRows` | `NeedsReview`/`TooManyRows` |
- **Failures:**
  - permanent reconcile errors lead to `NeedsReview` (A2);
  - transient failures, cancellations and abandoned reconciles count toward `MaxReconcileFailures` (default 3), after which the decision is `NeedsReview` (A4);
  - a failed reconcile can therefore never loop indefinitely.
- **Cancellation:** a cancelled reconcile is `Cancelled`, is never a finding, and counts toward the budget.

### R10: step deadlines, `continue` and explicit side effects (ADR-053)
- `timeoutMs` is one total deadline per step and must be ≤ `timeoutSeconds × 1000`.
- `onError: "continue"` is allowed only on `wait`, `extract`, `screenshot`, `fill`, `select` and `condition`. It is forbidden on `navigate`, `click`, `upload`, `download`, `captureResult` and `reconcileMatch`, because their browser-side work can complete after the step timed out.
- `sideEffect` is **mandatory** on every `click` and `upload` in `steps`; there is a warning for navigating clicks declared `false`.
- Late server-side commits after a client timeout are covered by `Indeterminate` plus the authoritative commit-latency wait from the dispatch horizon (R14, R17), and tested with DemoCrm `+late-commit`.

### R11: DemoCrm test-fixture extensions (ADR-054)
- **Approved scope (user, 2026-11-15):** DemoCrm may be modified in Phase 4 as a test fixture:
  - exact `OperationId` search with `data-operation-id` on rows;
  - a registration POST counter (`registrationSubmissions`) in the token-protected `/demo/state`, reset by `/demo/reset`;
  - a `+late-commit` scenario that commits after the client disconnects.
- **Boundaries:** DemoCrm stays independent; no production project may reference it (ADR-027); the control token rules (ADR-028) are unchanged.
- **Test rule:** every duplicate-risk scenario asserts the POST count, so a resubmission is detected even when DemoCrm rejects it.

### R12: centralized limits (ADR-055)
- All workflow limits live in `WorkflowLimits` (Application) with the values in §1.6.
- The validator, runtime checks and documentation use them.
- Parity tests compare every numeric bound and enum in `docs/workflow-schema-v1.json` with the code.
- Server-configurable limits (`SessionSeconds`, `Uploads.MaxBytes`, `ClockSkewSeconds`, `MaxReconcileFailures`, `MaxAmbiguousAttempts`, journal `LeaseSafetyMarginSeconds`) are validated at startup, and a workflow can never exceed them.

### R13: fencing and takeover (ADR-056)
- **Fact:** a Worker that wrote a side-effect checkpoint and then froze (or lost its lease while alive) **can still dispatch the click when it resumes**.
  - Its egress proxy is in-process and resumes with it.
  - No in-process check can close the check-then-act window.
  - A third-party target cannot validate our fencing tokens.
- **Rule:** an attempt with a checkpoint is **unfenced** until a fence is established (§7.2 fencing table: executor egress revocation at completion, including late completion; abandoned without checkpoint; process-death confirmation; isolation termination; out-of-process lease-bound egress; audited human confirmation).
- **While any attempt of an operation is unfenced:**
  - the journal refuses every new reservation (Normal and Reconcile);
  - the evaluator returns `AwaitingFence` (S1b, before every rule that can allow anything).
- **Lease expiry alone never permits takeover after a checkpoint.**
- **Fence time and dispatch horizon:** all delays and evidence windows are measured from the proven dispatch horizon (R17), never from an assumed timeout.
- **Checkpoint acceptance:** the checkpoint is an atomic conditional write, accepted only with a valid fencing token and a remaining lease ≥ step timeout + `LeaseSafetyMarginSeconds`. A Worker that lost its lease before checkpointing can never invoke the handler.
- **Guarantees by phase:**

  | Phase | Guarantee |
  |---|---|
  | Phase 4/5 | At most one dispatch per checkpointed, unfenced attempt; **no second attempt while it could still dispatch** |
  | Phase 7 | Can additionally prevent the stale dispatch itself (`EgressLeaseExpired`, `IsolationTerminated`), enabling fully automatic takeover |

### R14: authoritative evidence requires a target commit-latency contract (ADR-057)
- **Contract block:** `evidence: "authoritative"` requires `reconciliation.authority` with:
  - `maxCommitLatencySeconds` (1..3600);
  - `contractReference` (10..500 chars), citing a target-specific contract from the target operator that guarantees bounded commit latency, read-your-writes lookup, visibility of pending records, and a unique key with superset search (§10.4).
- **Not a contract:** observed delays, empty searches or measured response times.
- **No contract means advisory:** if the contract cannot be established, the workflow must be `advisory`, and `NotFound` can never authorize an automatic repeat.
- **Hosted mode:** authoritative attestations are admin-only (Phase 5/6).
- **What L must cover (round 9):** everything after the dispatch horizon: local buffering below our socket layer, network transit, target processing, and query visibility. An author reasoning from upstream write admission must also include local queueing time. This is a **trusted attestation, not a TCP guarantee** (§7.6.7).
- **Delay rule:** the delay is `DispatchHorizon + maxCommitLatencySeconds + ClockSkewAllowance` (R7, R17). The former `minDelaySeconds` is removed.

### R15: journal implementation boundary (ADR-058)
- **Phase 4:** validates the `IAttemptJournal` contract and the execution contract with test implementations only (`InMemoryAttemptJournal`, fault-injecting journals; `IsDurable = false`), through the shared conformance suite and the executor, crash, fencing and concurrency tests. Phase 4 has no path for customer execution.
- **Phase 5:** supplies the durable PostgreSQL journal. It must pass the **same** conformance suite plus the crash-recovery (with a really killed Worker process), fencing and concurrency (two Worker processes) tests before any customer execution.
- **No real customer execution may rely on an in-memory or non-durable journal.**
- **Fail closed:** a production Worker fails at startup, in every environment and with no override, unless:
  - a journal is registered with `IsDurable` and `SupportsFencing`;
  - its lease length covers the longest side-effect timeout plus the safety margin;
  - the process-death fence supervisor is configured and its containment handshake succeeds (R16).

  At run time, an unreachable journal refuses every attempt.

### R16: process-death supervisor and fence confirmation (ADR-059)
- **Identity:** every attempt is bound to one registered Worker incarnation (GUID, host id, boot id, PID and start time, containment unit). The containment unit (Linux cgroup v2 unit or Windows Job Object without breakaway) contains the Worker, its in-process egress proxy, the Playwright driver and every Chromium process, orphans included.
- **Positive confirmation only:** `ProcessDeathConfirmed` requires the kernel or job accounting to report the containment unit **empty** after the supervisor killed the whole unit, or a changed host boot id.
  - **Not confirmations:** kill requests, signals, expired leases, stale heartbeats, missing PIDs, the Worker alone being gone, or timeouts.
  - **Orphans:** surviving children stay in the unit and are killed and proven gone with it.
- **Credentials and connections:**
  - credentials are unusable once the process hosting the in-process proxy no longer exists, and connections once its sockets are closed; both are covered by "unit empty";
  - after death, sockets linger and may still transmit buffered data; no OS timeout bounds that. The fence therefore also requires the supervisor's socket drain over the connection ledger (R17, §7.6.5);
  - proxy credentials are never persisted, so no other proxy instance can accept them;
  - for live attempts, the completed revocation barrier (R17); for Phase 7, the out-of-process proxy's report after its barrier completed.
- **A fence is not a failure:** it means nothing *further* can be dispatched. Already-dispatched requests may have committed. The attempt stays ambiguous, and only rules A1–A8 resolve it; the commit-latency and reconciliation rules are unchanged (§7.5.8).
- **Human confirmation:**
  - platform operators only;
  - a closed list of verification methods;
  - a mandatory evidence reference and personal attestation;
  - fully audited;
  - never "lease expired" or "kill sent".
- **Timestamps:** `FencedAt` is the journal commit time of a validated confirmation, always after observation; nothing else is ever a fence time.
- **Failure:** without proof, attempts stay `AwaitingFence` and become `NeedsReview` after `FenceWaitSeconds`; they are never retried automatically, and a late fence does not restart automation without an explicit operator requeue.
- **Scope:** a fence covers every attempt of the confirmed incarnation; confirmations with mismatched incarnation or unit, a non-listed method, or (for `ProcessDeathConfirmed` and `DrainVerified`) without a successful drain report, are rejected.
- **Topology:** `ProcessDeathConfirmed` is valid only when browser and proxy run inside the Worker's unit.
- **Operations:** Worker hosts are never restored or cloned from memory snapshots.

### R17: egress revocation barrier, write gate, socket drain and dispatch horizon (ADR-060)
- **Findings:**
  - Phase 3 `EgressSession.Dispose` only revokes credentials and closes browser-side sockets (G1–G6);
  - the round-7 "check token, then write" was not atomic (G7);
  - OS documentation does not support a retransmission-timeout horizon (§7.6.4).
- **Write gate (§7.6.1):**
  - every upstream connection creation and every payload send runs inside an admission obtained by `UpstreamWriteGate.TryEnter` under the session lock (W1);
  - only `UpstreamChannel` sends on upstream sockets, enforced by source scan (W2);
  - admitted sends are not cancelled (W3), are at most 16 KB (W4), and happen only after the connection's 4-tuple is in the connection ledger (W5).
- **Barrier (§7.6.2):**
  - **B1 = Lr:** set `revoked` under the same lock, so admission and writes close at once;
  - **B2:** wait until every write admitted before Lr has returned;
  - **B3 = Ta:** abortive close of all upstream sockets and close of all browser-side sockets, including keep-alive CONNECT tunnels;
  - **B4:** wait for every handler;
  - **B5:** `Completed`, or `TimedOut` after `RevocationTimeoutSeconds`.

  No send syscall begins after Lr, and none is executing after Ta (§7.6.3).
- **Fencing:**
  - `EgressRevoked` and `ExecutionFenced` are true only for `Completed`;
  - `CompleteAsync` comes after the barrier;
  - the barrier runs before the browser context is closed;
  - `TimedOut` leaves the attempt unfenced;
  - `Dispose()` never establishes a fence;
  - the Phase 7 proxy reports revocation only after the same barrier.
- **Process-death fences (§7.6.5):** require, besides an empty containment unit, a supervisor **socket drain** with OS-level connection identity. Ledger records carry the tuple plus identity (Linux `SO_COOKIE`; Windows `liCreateTimestamp`).
  - **Linux:** a dedicated network namespace per incarnation. Every TCP socket in it is enumerated completely, transmit-capable ones are destroyed by exact cookie, and the drain is confirmed only when every socket is safe.
  - **Windows:** matching by tuple **and** creation timestamp. **Nothing is ever terminated** (`SetTcpEntry` cannot target by identity). A same-tuple row with a different or missing identity is **ambiguous, so no fence**.
  - Any failure, incomplete enumeration, unsupported or denied destroy, silently skipped destroy, ambiguity or partial drain means **no fence** (fail-closed table).
- **Fail-fast options only:** `TCP_USER_TIMEOUT` (ms) and `TCP_MAXRT` (s) are set for fail-fast only and are **not** a guarantee; the send-buffer cap is removed.
- **Dispatch horizon (§7.6.6):**
  - H = `FencedAt` (`AbandonedAt` for `NoCheckpoint`), recorded only after **E1** (every admitted write completed) and **E2** (every connection destroyed; TCP originates nothing more) are proven;
  - it is a conservative upper bound, **not** a physical-departure time; **E3** (segments already queued below the socket layer) is never claimed;
  - `IsolationTerminated` is not accepted until Phase 7 specifies an equivalent proof.
- **Why it is safe (§7.6.7):**
  - every request's bytes were admitted, and transmitted by our TCP if at all, before H;
  - the **trusted** latency attestation covers local below-socket buffering, transit, processing and visibility after H;
  - so an authoritative `NotFound` observed at ≥ H + L + skew cannot be followed by that request committing;
  - if the attestation is wrong (e.g. a paused host, or long transit), safety can fail; test H4 and limitation 2 document this.
- **Unchanged rule:** the commit-latency contract and the reconciliation rules are unchanged, and a fence never proves that an already-dispatched side effect failed.
