# Phase 4 — Workflow Engine and Action Registry: Implementation Plan

Status: **Decisions D1–D7 approved 2026-11-12. §16 is the authoritative binding record (ADR-037 to ADR-044). Implementation awaits final approval. No Phase 4 code exists.**
Basis: Phase 3 as committed in `fd942c2`. Real interfaces reused: `IBrowserSessionFactory`, `IBrowserSession`, `ElementSelector`, `InputValue`, `SelectChoice`, `WaitCondition`, `BrowserSessionRequest`, `BrowserOperationException`/`BrowserErrorCode`, `EgressPolicy`/`AllowedHostRule`, `ExecutionScope`, `IExecutionLogSink`, `IArtifactStore`.

**Out of scope (Phase 5/6):** database persistence, job scheduling, CSV upload infrastructure, Worker implementation, retries, frontend.

Phase 4 delivers one engine call that executes one workflow version for **one record attempt**. Phase 5 calls it once per CSV row and attempt.

**Precedence:** if any earlier section is ambiguous or appears to conflict with §16, §16 wins.

---

## Terminology (used consistently throughout)
| Term | Meaning |
|---|---|
| **Record** | One input row (CSV/JSON) for one execution; validated against `inputs`. |
| **Parameter** | A per-environment value supplied by the job configuration (`{{params.x}}`), never by a record. Types `url` and `string`. |
| **Secret** | A value obtained only from `ISecretProvider` via `{{secret.x}}`. |
| **Sensitive input** | A record field declared with `"sensitive": true`. |
| **Sensitive extract** | The output of an `extract` step declared with `"sensitive": true`. |
| **Sensitive value** | Any secret, sensitive input or sensitive extract, and any resolved template that contains one (taint propagation). |
| **Sink** | A place a resolved value is used (a `fill` value, a `select` choice, a condition operand, a URL, a result field, and so on). §5 defines which sinks accept which values. |
| **Side-effect step** | A step marked `"sideEffect": true`, which may change data in the target system (`click`, `upload`). |
| **Terminal outcome** | The single final outcome of an execution (§16 D3). |
| **Step timeout** | A step's `timeoutMs`; never terminates the session (§16 D2). |
| **Session-terminating timeout** | The workflow `timeoutSeconds`, the Phase 3 session deadline, or caller cancellation. |

---

## 0. Findings from inspecting Phase 3 (gaps this plan must close)
| Gap in Phase 3 (`fd942c2`) | Needed by | Resolution (additive, no new browser abstraction) |
|---|---|---|
| `IBrowserSession` has no file upload | `upload` action | `UploadAsync(ElementSelector, UploadFile, OperationOptions?, ct)` using Playwright `SetInputFilesAsync(FilePayload)`. In-memory content only, never a filesystem path (D2). |
| No non-throwing page probes | `condition` (`elementExists`, `elementVisible`, `urlMatches`) | `CountAsync`, `IsVisibleAsync` (immediate, no auto-wait, never throw for missing elements) and `CurrentUrl` (logged redacted) (D2). |
| No per-call timeout (operations use the global `ActionSeconds`) | Step `timeoutMs`; `onError: "continue"` | Optional `OperationOptions(TimeSpan? Timeout)` on every operation, passed to Playwright as that call's own timeout. A step timeout never terminates the session (D2). |
| The pure host/IP policy types live in Automation | Publish-time validation of `allowedHosts` and `url` parameters belongs in Application (the Api must never reference Automation, ADR-017) | Move the five pure types `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy` and `NormalizedHost` to `AutomataFlow.Application.Network`. Runtime enforcement stays in Automation (D1). |

All Phase 3 protections stay in force unchanged: every navigation still goes through `RequestGuard`, the egress proxy, fail-closed behaviour, the sandbox, redaction and masking.

---

## 1. Versioned JSON workflow schema (schemaVersion 1)

### 1.1 Shape
```jsonc
{
  "schemaVersion": 1,                       // format version (§11); required, integer
  "name": "democrm-register-customer",      // ^[a-z][a-z0-9-]{2,63}$
  "description": "Registers one customer in DemoCrm.",   // optional, <= 500 chars
  "timeoutSeconds": 120,                    // session-terminating; 1..session maximum
  "parameters": [                           // supplied by the job configuration, never by a record (§16 D7)
    { "name": "crmBaseUrl", "type": "url" },
    { "name": "crmUser", "type": "string" }
  ],
  "allowedHosts": [                         // literal origins and/or url parameters; nothing else is reachable
    "{{params.crmBaseUrl}}"
  ],
  "inputs": [                               // record schema; Phase 5 validates rows against this
    { "name": "email", "type": "email", "required": true, "maxLength": 120 },
    { "name": "company", "type": "string", "required": false },
    { "name": "taxId", "type": "string", "required": false, "sensitive": true }   // sensitive input
  ],
  "secrets": ["crmPassword"],               // names only; values from ISecretProvider; usable only in fill (§5)
  "maskSelectors": [{ "css": "[data-testid=detail-tax-id]" }],   // extra screenshot masking for this site
  "setup":     [ /* steps run first on every attempt, e.g. login (no side effects) */ ],
  "reconcile": [ /* optional; run only when the request asks for reconciliation (§10) */ ],
  "steps":     [ /* main steps */ ]
}
```

### 1.2 Step shape (all actions)
```jsonc
{
  "id": "submit",                 // ^[a-z][a-z0-9-]{0,39}$, unique across the whole workflow
  "type": "click",                // one of the 12 action types
  "config": { },                  // action-specific, strictly typed (§2)
  "timeoutMs": 15000,             // optional step timeout; 100..120000; default per action; never ends the session
  "onError": "fail",              // "fail" (default) | "continue" (not allowed on side-effect steps or captureResult)
  "sideEffect": false,            // true only on click/upload; not allowed in setup or reconcile (§10)
  "description": "optional text"
}
```

### 1.3 Selectors (map 1:1 to Phase 3 `ElementSelector`)
`{"testId": "x"}` · `{"css": "x"}` · `{"role": "button", "name": "Sign in"}` · `{"text": "x"}` · `{"label": "x"}` · `{"placeholder": "x"}` · `{"xpath": "//x"}`

- **Templates in selectors:** placeholders are allowed only in `testId`, `text`, `label`, `placeholder` and `name` values. They are never allowed in `css` or `xpath`, so record data cannot alter selector structure.
- **No sensitive values in selectors** (§5).
- **Validation:** the existing `ElementSelector` validation (length, engine prefixes, `>>` chains) is applied after resolution.

### 1.4 Templates
- **Syntax:** `{{namespace.path}}` only. No expressions, filters, functions, arithmetic or loops.
- **Namespaces:** `record`, `steps`, `operation`, `params` and `secret` (§4). A literal `{{` is a validation error in v1.

### 1.5 Formal JSON Schema
A JSON Schema (draft 2020-12) for the editor and tooling ships as `docs/workflow-schema-v1.json`; Appendix A has its outline.
- The **hand-written validator in Application is authoritative** (ADR-016).
- Rules that JSON Schema cannot express, such as sink rules, dominance, per-section outcomes and overwrite prevention, exist only in the validator.
- A test keeps the schema's action list, outcome statuses and parameter/input types identical to the code.

### 1.6 Limits (validator-enforced)
| Limit | Value |
|---|---|
| Definition size | 256 KB |
| Steps (total, including setup, reconcile and branches) | 200 |
| Branch nesting depth | 3 |
| Condition expression depth / predicate count | 5 / 20 |
| String fields | 2,000 chars (selectors 500, per Phase 3) |
| Inputs / parameters / secrets | 100 / 20 / 20 |
| Delay | 30 s per step; total runtime bounded by `timeoutSeconds` |

**No loops and no regular expressions in v1** (§16 D6).

---

## 2. The 12 actions: contracts and classes
Each action has two halves, keeping the Api independent of Automation:

- **Application: `ActionDescriptor`** (static catalog). Type id, config parser and validator, default timeout, declared outputs, capability flags. Used for publish-time validation, by the editor (Phase 6) and by the executor.
- **Automation: `IActionHandler`**. Executes against `IBrowserSession` only, never Playwright directly (architecture-tested).

| Type | Config (typed record) | Phase 3 operation used | Outputs (`steps.<id>.*`) | Side effect allowed | Default timeout |
|---|---|---|---|---|---|
| `navigate` | `url` (template; authority must be a literal allowed origin or a listed url parameter, §6) | `NavigateAsync` | `status`, `url` (redacted) | no | navigation |
| `click` | `selector`, `waitForNavigation` (bool) | `ClickAsync`, or `SubmitAsync` when waiting | — | **yes** | action |
| `fill` | `selector`, `value` (template) | `FillAsync(InputValue.Plain` or `InputValue.Sensitive)` | — | no | action |
| `select` | `selector`, one of `value` / `label` / `index` (template) | `SelectAsync(SelectChoice)` | — | no | action |
| `wait` | `for`: `visible` / `hidden` / `attached` / `detached` + `selector`, or `url` glob, or `load` | `WaitForAsync(WaitCondition)` | — | no | action |
| `extract` | `selector`, `source`: `text` or `attribute` (+ `attribute`), `sensitive` (bool), `maxLength` | `GetTextAsync` / `GetAttributeAsync` | `value` | no | action |
| `screenshot` | `label`, `fullPage` | `ScreenshotAsync` | `artifactId` | no | action |
| `upload` | `selector`, `file` (template resolving to a file reference) | **new** `UploadAsync` with content from `IExecutionFileProvider` | `fileName` | **yes** | action |
| `download` | `trigger` selector | `DownloadAsync` (session created with `AcceptDownloads` when the workflow contains a download step) | `artifactId`, `fileName`, `sha256` | no | download |
| `condition` | `if` (expression, §8), `then` steps, `else` steps | **new** `CountAsync` / `IsVisibleAsync` / `CurrentUrl`, plus `GetTextAsync` / `GetAttributeAsync` | `result` (bool) | no (the branches may) | action |
| `delay` | `milliseconds` (1..30000) | none (`Task.Delay` with the execution token) | — | no | n/a |
| `captureResult` | `fields` (name → template; no sensitive values), optional `outcome` (§16 D3) | none | — | no | n/a |

Every step additionally exposes `steps.<id>.failed` (`true`/`false`; §9).

```csharp
// Application (no packages)
public sealed record ActionDescriptor(
    string Type,
    Type ConfigType,
    Func<JsonElement, ActionConfigParseContext, ActionConfig?> Parse,   // strict: unknown properties rejected
    TimeSpan DefaultTimeout,
    IReadOnlyList<string> Outputs,
    bool SideEffectAllowed);

public abstract record ActionConfig;                    // one sealed record per action, e.g. FillConfig
public static class ActionCatalog                        // the single list of the 12 descriptors
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

Handlers: `NavigateHandler`, `ClickHandler`, `FillHandler`, `SelectHandler`, `WaitHandler`, `ExtractHandler`, `ScreenshotHandler`, `UploadHandler`, `DownloadHandler`, `ConditionHandler`, `DelayHandler`, `CaptureResultHandler`.

- **`StepContext`** gives a handler the `IBrowserSession`, the `VariableResolver`, a per-step `OperationOptions` (step timeout), the `ExecutionScope` and an output writer.
- **`StepOutcome`** is a set of outputs, or an `ExecutionError` (§9).

---

## 3. Action registry and dependency injection
- `AddWorkflowEngine()`, a new extension in Automation alongside `AddBrowserEngine()`, registers the 12 handlers as `IActionHandler` singletons. They are stateless; all state lives in `StepContext`.
- It also registers `ActionRegistry`, `WorkflowExecutor` (`IWorkflowExecutor`) and `VariableResolver`.
- **`ActionRegistry` checks at construction, failing startup on any mismatch:**
  - every `ActionCatalog` type has exactly one handler;
  - no handler exists for an unknown type;
  - each handler's config type matches its descriptor.
- **Adding an action** means one descriptor in Application, one handler in Automation, and tests. The executor never changes.
- **Hosts register what Phase 5 needs:** `ISecretProvider` and `IExecutionFileProvider` (new Application ports), plus the Phase 3 `IArtifactStore` and `IExecutionLogSink`. Phase 4 tests use in-memory doubles.
- **The Api calls only Application** (`WorkflowDefinitionParser`, `WorkflowDefinitionValidator`, `ActionCatalog`), never `AddWorkflowEngine`. The architecture test still enforces Api ↛ Automation.

---

## 4. Variable resolution
| Namespace | Source | Example | Rules |
|---|---|---|---|
| `record` | the record, validated against `inputs` | `{{record.email}}` | must be a declared input; a sensitive input yields a sensitive value |
| `steps` | outputs of earlier steps | `{{steps.conf-id.value}}` | the referenced step must **dominate** this use (always executed before it on every path: an earlier step in the same or an enclosing sequence, or in `setup`); the output name must be declared by that action; a sensitive extract yields a sensitive value |
| `operation` | stable per-record id supplied by the caller (ADR-015) | `{{operation.id}}` | the only key is `id`; it never changes across attempts |
| `params` | parameters from the job configuration | `{{params.crmBaseUrl}}` | must be declared; usage limited by type (§16 D7) |
| `secret` | `ISecretProvider` at run time | `{{secret.crmPassword}}` | must be declared in `secrets`; **only** allowed in `fill.value` (§5) |

- **Compile once:** at validation time each template is compiled into segments (literal or placeholder). All references are checked statically, so nothing is "discovered" at run time.
- **Run time:** `VariableResolver.Resolve(template, context)` returns a `ResolvedValue(string Text, bool IsSensitive, bool ContainsSecret)`.
  - A missing value at run time, which is only possible for optional inputs, resolves to the empty string unless the sink requires a value (e.g. a navigate URL).
  - Unexpected misses raise `VariableUnresolved` (the backstop).
- **Context-aware encoding:**
  - In `navigate.url`, placeholders after the authority are percent-encoded (`Uri.EscapeDataString`).
  - Placeholders are **forbidden in the scheme and authority**, except a whole `{{params.<url parameter>}}` origin prefix (§16 D7).
  - Record data can therefore never change the destination host (no `@evil.test` or `//evil.test` injection).
- **Record typing:**
  - Inputs declare `type` (`string` | `email` | `integer` | `decimal` | `boolean` | `date` | `file`), `required`, `maxLength` and `sensitive`.
  - `RecordValidator` (Application) validates a record before execution. Phase 5 reuses it per CSV row, so no redesign is needed.

---

## 5. Secrets, sensitive values and sinks
- **Secrets:** obtained only from `ISecretProvider` (Application port: `GetAsync(ExecutionScope scope, string name, ct)`).
  - Resolved only when executing and held in memory only for the execution's lifetime.
  - Never written to step outputs, results or logs.
  - Phase 5 implements encrypted, organization-scoped storage; Phase 4 uses an in-memory test double.
  - **No secret is ever stored in the workflow JSON.**
- **Taint propagation:** a resolved value is a *sensitive value* if any segment is a secret, a sensitive input or a sensitive extract. `ResolvedValue` carries both `IsSensitive` and `ContainsSecret`, so neither can be dropped silently.

### Sink rules (authoritative; enforced by the validator at authoring time **and** again at run time)
| Sink | Secret | Sensitive input / sensitive extract | Non-sensitive value |
|---|---|---|---|
| `fill.value` | **allowed** (`InputValue.Sensitive`) | allowed (`InputValue.Sensitive`) | allowed (`InputValue.Plain`) |
| `select` value / label | rejected | allowed (registered with the redactor) | allowed |
| condition operands (`valueEquals`, `textEquals`, …) | rejected | allowed (only the boolean result is logged) | allowed |
| `navigate.url` | rejected | rejected | allowed (path/query only, encoded) |
| `captureResult.fields` | rejected | rejected | allowed |
| selector values | rejected | rejected | allowed (`testId` / `text` / `label` / `placeholder` / `name` only) |
| `screenshot.label` | rejected | rejected | allowed |
| `upload.file` reference | rejected | rejected | allowed |

**Why secrets are `fill`-only:** using a secret in a condition or a selector would let a workflow probe the secret's value through page behaviour, a side channel. Typing it into a field is the only legitimate use.

### Logging
- Step events carry ids, types, durations and error codes, **never input or output values**: even non-secret record values are customer PII.
- Every sensitive value is registered with the session's redactor before use (the Phase 3 mechanism, extended to all sensitive resolutions), so failure messages stay redacted.

### Screenshots
- Masking covers every element filled with a sensitive value, the Phase 3 built-ins (password and card fields, `[data-af-sensitive]`) and the workflow's `maskSelectors`.
- **Limitation, stated plainly:** the platform cannot detect or mask every secret a website displays as text. Workflow authors must declare such regions in `maskSelectors`.
- The validator emits a **warning**, not an error, when a workflow has sensitive extracts but declares no `maskSelectors`.

---

## 6. Allowed hosts and SSRF integration
1. **Authoring time** (Application validator, using the moved `AllowedHostRule` parser, §16 D1):
   - Every `allowedHosts` entry is a literal origin (`https://host[:port]`) or `{{params.<url parameter>}}`.
   - Every `navigate.url` must begin with one of those literal origins or with a url parameter that is listed in `allowedHosts`.
   - Wildcards follow the Phase 3 rules (subdomains only).
2. **Binding time** (executor, before the browser starts, §16 D7):
   - Each url parameter value must be an origin only (no path, query, fragment or credentials), `https` outside Development, and not a private IP literal outside Development.
   - The `EgressPolicy` is built from the literal origins plus the bound url parameters.
   - `allowPrivateNetwork` is set **only** when the request explicitly asks for it **and** the engine mode is Development (the DemoCrm demo). The workflow JSON stays environment-agnostic.
   - The Phase 3 session factory then applies its existing mode rules: hosted mode is https-only with no private exceptions.
3. **Run time:** every request still passes the Phase 3 `RequestGuard` and the authoritative egress proxy:
   - DNS resolution in the proxy;
   - private, loopback, link-local and metadata denial;
   - IP pinning;
   - redirect-hop inspection;
   - fail-closed behaviour.

   **Phase 4 adds nothing that bypasses them.** A blocked navigation surfaces as `NavigationBlocked` (policy error, §9).
4. **Hosted mode (Phase 5):** an organization-level approved-host list is intersected with the workflow's hosts. The port is reserved, not built.

---

## 7. Execution lifecycle and context
```csharp
public interface IWorkflowExecutor   // Automation
{
    Task<WorkflowExecutionResult> ExecuteAsync(WorkflowExecutionRequest request, CancellationToken cancellationToken);
}

public sealed record WorkflowExecutionRequest(          // Application (Phase 5 builds one per record attempt)
    ExecutionScope Scope,                                  // org, job, record, attempt
    Guid OperationId,                                      // stable per record (ADR-015)
    ValidatedWorkflow Workflow,                            // parsed + validated definition, version, hash
    IReadOnlyDictionary<string, string?> Record,           // raw record values; validated by RecordValidator
    IReadOnlyDictionary<string, string> Parameters,        // from job configuration only
    ExecutionIntent Intent)                                // Normal | Reconcile
{
    public bool AllowPrivateNetworkInDevelopment { get; init; }
}
```

```
ExecuteAsync(request, ct)
 ├─ verify workflow hash (tamper check) ── validate record (RecordValidator) ──► RecordInvalid (permanent) if bad
 ├─ bind parameters ─► EgressPolicy ─► policy/mode check
 ├─ resolve secrets (ISecretProvider)                         [values in memory only]
 ├─ IBrowserSessionFactory.CreateAsync(scope, policy, maskSelectors, acceptDownloads)
 │     (Phase 3: proxy gate, fresh context, per-session credentials)
 ├─ log WorkflowStarted (workflow id/version/hash, operation id, intent)
 ├─ run  setup   steps ───────────────┐
 ├─ if Intent == Reconcile: run reconcile steps ─► reconciled | notFound | needsReview
 │        reconciled / needsReview ─► terminal outcome ──────► finish
 │        notFound ─► discard reconcile fields ─► continue    │
 ├─ run  main steps  ──────────────────────────────────────────┤
 │    for each step: resolve config (sink rules) ─► handler.ExecuteAsync(StepContext, step timeout)
 │       success ─► record outputs ─► next
 │       error   ─► classify (§9) ─► onError=continue? next : terminal outcome ─► stop
 │       side-effect step ─► mark SideEffectStarted BEFORE invoking the handler
 │       condition ─► evaluate ─► run then/else sequence (same rules, nested)
 │       terminal captureResult ─► terminal outcome ─► stop
 │    end of main steps without a terminal outcome ─► Succeeded
 ├─ log WorkflowCompleted (outcome, error code, duration)
 └─ finally: dispose session (Phase 3 cleanup), clear secret values  ─► WorkflowExecutionResult
```

```csharp
public sealed record WorkflowExecutionResult(
    WorkflowOutcome Outcome,             // Succeeded | Failed | Indeterminate | Reconciled | NeedsReview | Cancelled
    ExecutionError? Error,               // code, category, IsTransient, step id, redacted message
    bool SideEffectStarted,
    IReadOnlyDictionary<string, string> Results,     // captureResult fields (never sensitive values)
    IReadOnlyList<StepRecord> Steps,                  // id, type, status, duration, error code (no values)
    IReadOnlyList<ArtifactDescriptor> Artifacts);
```

- **Steps run strictly sequentially in one session per attempt.** No parallelism inside a workflow.
- **A step timeout never ends the session** (§16 D2). A session-terminating timeout or cancellation yields `Cancelled`, or `Indeterminate` if a side effect had started (§9).
- **Phase 5 integration is already shaped:** Phase 5 only builds the request per record and attempt, maps the result onto `JobAttempt`/`JobRecord` statuses and persists the logs and artifacts. The engine needs no redesign.

---

## 8. Condition evaluation (no scripting)
```jsonc
{ "id": "check", "type": "condition", "config": {
    "if": { "any": [
      { "urlMatches": "**/Customers/Confirmation/**" },
      { "all": [
          { "elementVisible": { "testId": "form-error" } },
          { "not": { "attributeEquals": { "selector": { "testId": "form-error" }, "attribute": "data-error-kind", "value": "validation" } } }
      ] }
    ] },
    "then": [ /* steps */ ],
    "else": [ /* steps */ ] } }
```

| Group | Predicates |
|---|---|
| Combinators | `all` (AND), `any` (OR), `not` (NOT) |
| Page | `elementExists`, `elementVisible`, `elementCountAtLeast` (selector + n), `textEquals`, `textContains`, `attributeEquals`, `urlMatches` (glob, the same syntax as Phase 3 `WaitCondition.Url`) |
| Values | `valueEquals`, `valueContains`, `valueStartsWith`, `isEmpty` (template operands; ordinal; optional `ignoreCase`) |

- **No scripting:** no regular expressions in v1 (no ReDoS surface), no arithmetic, no user functions, no JavaScript and no C#.
- **Bounds:** depth and predicate count are bounded (§1.6), and evaluation is short-circuit.
- **Operands** follow the sink rules (§5): sensitive inputs and sensitive extracts are allowed, secrets are rejected.
- **Implementation:** a closed set of sealed records (`ConditionExpr`) and a pure evaluator over an `IPageProbe` interface, which the session implements via `CountAsync` / `IsVisibleAsync` / `CurrentUrl` / `GetTextAsync` / `GetAttributeAsync`.
- **Testing:** the evaluator is unit-tested with a fake probe and needs no browser.
- **Logging:** only the boolean result is logged, never the compared values.

---

## 9. Errors, retryability and continue/stop
```csharp
public sealed record ExecutionError(
    string Code,                 // stable code, see table
    ErrorCategory Category,      // Transient | Permanent | Policy | Business | Cancelled
    bool IsTransient,            // retry hint for Phase 5 (Phase 4 never retries)
    string? StepId,
    string Message);             // redacted
```

| Source | Code(s) | Category / transient |
|---|---|---|
| Phase 3 `BrowserErrorCode` | `Timeout`, `SessionTimeout`, `ElementNotFound`, `NetworkError`, `EgressUnavailable`, `BrowserCrashed`, `SessionClosed` | Transient / yes (as in Phase 3) |
| Phase 3 `BrowserErrorCode` | `ElementNotUnique`, `InvalidSelector`, `HttpError`, `DownloadRejected` | Permanent / no |
| Phase 3 `BrowserErrorCode` | `NavigationBlocked`, `PolicyViolation` | Policy / no |
| Engine | `RecordInvalid`, `VariableUnresolved`, `UploadRejected`, `WorkflowTampered`, `ResultOverwrite` | Permanent / no |
| Engine | `SecretUnavailable` | Transient / yes |
| `captureResult.outcome` | author-defined (e.g. `DuplicateCustomer`) | Business / as declared |
| Cancellation or session-terminating timeout | `Cancelled` | Cancelled / no |

**Rules:**
- `onError: "fail"` (the default) makes the error the terminal outcome and stops. `onError: "continue"` records the error and proceeds. It is forbidden on side-effect steps and on `captureResult`.
- Later steps can read `{{steps.<id>.failed}}`.
- **ADR-015 rule:** a transient or unknown error, a cancellation, or a transient business failure *after* a side-effect step has started yields `Indeterminate`, never a plain `Failed`. Phase 5 must reconcile before repeating.
- A permanent error after a side effect yields `Failed`: the target answered definitively, e.g. a validation error page.

---

## 10. Side effects and reconciliation (prepared for Phase 5)
- **`sideEffect: true`** is allowed only on `click` and `upload`, and only in `steps` (never in `setup` or `reconcile`). `SideEffectStarted` is set *before* the handler is invoked, so a crash or timeout during the click is still covered. The result carries `SideEffectStarted`.
- **`{{operation.id}}`** gives a stable idempotency key. The DemoCrm workflow fills it into the form's operation reference.
- **`reconcile` section:** steps run *instead of* blind repetition when `Intent = Reconcile`. Every path must end with a terminal `captureResult` whose outcome is one of:
  - `{ "status": "reconciled" }`: found, fields captured, outcome `Reconciled`;
  - `{ "status": "notFound" }`: safe to run the main steps; reconcile fields are discarded;
  - `{ "status": "needsReview" }`: inconclusive, outcome `NeedsReview`.
- **Main-step outcomes:** `{ "status": "succeeded" }` or `{ "status": "failed", "code": "...", "transient": true|false }`, optionally with `transientWhen` (a condition). Section rules and overwrite prevention are in §16 D3.
- **Phase 4 boundary:** the engine executes reconcile on request and reports. **Deciding when to reconcile, retrying and persisting `Indeterminate`/`NeedsReview` are Phase 5.**
- **Deliberate cost of safety:** after a side-effect step, the engine cannot tell "the target failed before saving" from "the target saved but the response was lost". Every transient failure after a side effect is therefore `Indeterminate` and requires a reconciliation pass before any repeat, even when nothing was saved (as in DemoCrm `+fail-N`). This costs one extra reconcile run per such failure. It is the intended trade-off: one extra lookup is cheaper than a duplicate business record (ADR-015, ADR-039).

---

## 11. Version immutability and schema versioning
- **`schemaVersion`** versions the *format*. The parser dispatches on it, rejects unknown versions and keeps a parser per supported version. Future versions add an explicit, tested upgrader (`v1 → v2`) that runs in memory. Stored definitions are never rewritten.
- **Workflow versions** version the *content* with the Domain entity `WorkflowVersion` (rules only; persisted in Phase 5):
  - lifecycle `Draft` → `Published` → `Archived`, with no transitions back;
  - Published and Archived definitions are immutable, and edits create a new version (§16 D5).
- **`DefinitionSha256`:** computed over the deterministic canonical form defined in §16 D5, stored on publish, and recomputed by the executor before every run (`WorkflowTampered` on mismatch).
- **`ValidatedWorkflow`** is the in-memory parsed form and is deeply immutable (records, read-only collections). Jobs (Phase 5) reference a specific `WorkflowVersion` id, never a "latest" pointer.

---

## 12. DemoCrm workflow (pure JSON, no workflow-specific code)
Stored as `samples/workflows/democrm-register-customer.json` and executed by the generic engine in the tests.

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
    { "name": "country", "type": "string", "required": true },
    { "name": "customerType", "type": "string", "required": true }
  ],
  "setup": [
    { "id": "open-login", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Login" } },
    { "id": "user", "type": "fill", "config": { "selector": { "testId": "login-email" }, "value": "{{params.crmUser}}" } },
    { "id": "password", "type": "fill", "config": { "selector": { "testId": "login-password" }, "value": "{{secret.crmPassword}}" } },
    { "id": "sign-in", "type": "click", "config": { "selector": { "testId": "login-submit" }, "waitForNavigation": true } },
    { "id": "signed-in", "type": "wait", "config": { "for": "visible", "selector": { "testId": "current-user" } } }
  ],
  "reconcile": [
    { "id": "find", "type": "navigate", "config": { "url": "{{params.crmBaseUrl}}/Customers/Search?Field=Email&Term={{record.email}}" } },
    { "id": "found", "type": "condition", "config": {
        "if": { "elementExists": { "testId": "search-result-row" } },
        "then": [
          { "id": "existing-id", "type": "extract", "config": { "selector": { "testId": "search-result-row" }, "source": "attribute", "attribute": "data-customer-id" } },
          { "id": "reconciled", "type": "captureResult", "config": { "fields": { "customerId": "{{steps.existing-id.value}}" }, "outcome": { "status": "reconciled" } } }
        ],
        "else": [
          { "id": "not-found", "type": "captureResult", "config": { "fields": {}, "outcome": { "status": "notFound" } } }
        ] } }
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
    { "id": "terms", "type": "click", "config": { "selector": { "testId": "input-accept-terms" } } },
    { "id": "submit", "type": "click", "sideEffect": true, "timeoutMs": 30000,
      "config": { "selector": { "testId": "register-submit" }, "waitForNavigation": true } },
    { "id": "outcome", "type": "condition", "config": {
        "if": { "urlMatches": "**/Customers/Confirmation/**" },
        "then": [
          { "id": "customer-id", "type": "extract", "config": { "selector": { "testId": "confirmation-customer-id" }, "source": "text" } },
          { "id": "result-kind", "type": "extract", "config": { "selector": { "testId": "confirmation-message" }, "source": "attribute", "attribute": "data-result" } },
          { "id": "confirmation", "type": "screenshot", "config": { "label": "confirmation" } },
          { "id": "done", "type": "captureResult", "config": {
              "fields": { "customerId": "{{steps.customer-id.value}}", "registration": "{{steps.result-kind.value}}" },
              "outcome": { "status": "succeeded" } } }
        ],
        "else": [
          { "id": "error-kind", "type": "extract", "config": { "selector": { "testId": "form-error" }, "source": "attribute", "attribute": "data-error-kind" } },
          { "id": "classify", "type": "condition", "config": {
              "if": { "valueEquals": { "left": "{{steps.error-kind.value}}", "right": "duplicate" } },
              "then": [
                { "id": "existing", "type": "extract", "config": { "selector": { "testId": "form-error" }, "source": "attribute", "attribute": "data-existing-customer-id" } },
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

How this example satisfies the rules:
- **Secret:** used only in `fill.value` (`password`).
- **Setup:** no side effects (`sign-in` is a login click without `sideEffect`).
- **Single side effect:** the only side-effect step is `submit`, in `steps`.
- **Reconcile:** every path ends with a reconcile outcome.
- **Terminal results:** every terminal `captureResult` is the last step of its sequence, and no result field is written twice on any path.
- **Engine-defined result:** the main sequence can never finish without a terminal outcome, because every branch of `outcome` terminates. Implicit `Succeeded` therefore cannot occur here.

**Expected behaviour with DemoCrm** (each line is an integration test):
- **Valid record:** `Succeeded`, with `customerId=C-000025` and `registration=created`.
- **Seeded email:** `Failed` with `DuplicateCustomer` (permanent) and `existingCustomerId=C-000001`. No new customer.
- **`+fail-2`** (nothing is ever saved by the two failures, but the engine cannot know that; see §10):
  - Attempt 1: `Indeterminate` (transient `RegistrationRejected` after the side effect).
  - Attempt 2 (`Intent=Reconcile`): reconcile `notFound`, main steps run again, `Indeterminate` again.
  - Attempt 3 (`Intent=Reconcile`): reconcile `notFound`, main steps run again, `Succeeded`.
- **`+commit-fail`:**
  - Attempt 1: `Indeterminate`.
  - Attempt 2 (`Intent=Reconcile`): `Reconciled`, with the existing customer id.
  - The customer count rises by exactly **1**.
- **Invalid record** (bad email): `RecordInvalid` before any browser work.

---

## 13. Test matrix
**Unit — Application** (no browser, `AutomataFlow.UnitTests`):
| Area | Scenarios |
|---|---|
| Parser | unknown properties rejected at every level (including `loop` / `forEach` / `regex` / `matches`, §16 D6); missing/unknown `schemaVersion`; wrong types; non-integer numbers; size limit; duplicate keys |
| Structure | unique step ids across setup/reconcile/steps/branches; id pattern; step/nesting/predicate limits; `onError=continue` forbidden on side-effect steps and captureResult; side effects only on click/upload and only in `steps` |
| Terminal outcomes (§16 D3) | terminal captureResult must be last in its sequence; per-section allowed statuses (setup `failed`; reconcile `reconciled`/`notFound`/`needsReview` on every path; steps `succeeded`/`failed`); a result field written twice on one path is rejected; written once per branch is accepted; `transientWhen` must be a valid condition |
| Per action (12) | valid config; each required field; bounds (timeouts, delay ≤ 30 s, maxLength); selector rules (placeholders not in css/xpath); download implies `AcceptDownloads` |
| Templates | syntax errors (`{{`, unterminated, empty, unknown namespace); undeclared input/param/secret; step reference not dominating (later step, sibling branch); undeclared output name |
| Sink rules (§5) | the full matrix: a secret is accepted only in `fill.value` and rejected in select / condition / navigate / captureResult / selector / screenshot label / upload; sensitive inputs and sensitive extracts accepted in fill / select / condition and rejected elsewhere; taint through mixed templates (`"x{{secret.a}}y"`); checks at validation **and** at run time |
| URL safety | placeholder in scheme/authority rejected; `{{record.x}}` cannot become a host; origins must be allowed; path/query encoding (`a/b?c=@evil.test` stays in the path) |
| Parameters (§16 D7) | `url` with path/query/fragment/credentials rejected; `http` rejected outside Development; private IP literal rejected outside Development; url parameter not listed in `allowedHosts` cannot prefix `navigate.url`; `string` parameter length/control characters; `string` parameter in scheme/authority rejected; records cannot supply or override parameters |
| Conditions | each predicate; all/any/not truth tables; short-circuit order; depth/count limits |
| Records | each input type (email/integer/decimal/boolean/date/file), required, maxLength, unknown columns handling, sensitive flag |
| Versioning (§16 D5) | canonical bytes and hash stable under property reordering, whitespace and escaping variants; changed by any semantic change; golden-value test for the DemoCrm sample; duplicate keys rejected before canonicalization; `WorkflowVersion` Draft→Published→Archived, no way back, mutation after publish throws |
| Host rules (moved, §16 D1) | the existing Phase 3 rule/normalizer/IP tests keep passing unchanged after the move |

**Unit — Automation** (fake `IBrowserSession`, no browser):
- **Registry:** completeness (12↔12), duplicate handler, unknown type, config-type mismatch.
- **Handlers:** each of the 12 calls exactly the expected `IBrowserSession` operation with the right `ElementSelector` / `InputValue` (Sensitive vs Plain) / `SelectChoice` / `WaitCondition` / `OperationOptions` step timeout.
- **Executor:**
  - sequential order; setup → main; reconcile intents (`reconciled` / `notFound` with field discard / `needsReview`);
  - `onError` continue/fail; `steps.x.failed`;
  - `SideEffectStarted` set before the handler; transient (including transient business) after a side effect gives `Indeterminate`; permanent after a side effect gives `Failed`;
  - terminal outcome set exactly once (`TrySetTerminal`); a second attempt and a run-time field overwrite raise `ResultOverwrite`, tested by bypassing the validator; cancellation arriving after the terminal outcome does not change it;
  - cancellation before/after the side effect; session-terminating timeout; secret-provider failure;
  - tamper check; session always disposed (even on exceptions); no values in log entries.

**Integration — real Chromium** (`AutomataFlow.AutomationTests`, verification container):
- **DemoCrm workflow** (all §12 scenarios): success, duplicate, invalid record, `+fail-2` across three attempts, `+commit-fail` reconciliation with exactly one customer.
- **Fixture workflows:** download and upload round-trip (content hash), screenshot artifact, conditions against real pages (`urlMatches`, `elementVisible`, `attributeEquals`), wait/delay.
- **Step timeouts** (§16 D2): a step timeout followed by further successful steps in the same session (`onError: "continue"`); a session-terminating `timeoutSeconds` does end the run with `Cancelled` / `Indeterminate`.

**Security:**
| Scenario | Expectation |
|---|---|
| Record value `http://evil.test@127.0.0.1/` placed in a URL path | percent-encoded; destination host unchanged |
| Workflow navigating to a host not in `allowedHosts` | rejected at validation; also `NavigationBlocked` at run time when constructed directly |
| Url parameter pointing to `169.254.169.254` / `127.0.0.1` | binding rejects (outside Development) or the Phase 3 proxy blocks; no internal hit |
| Url parameter whose public name later resolves to a private IP | blocked by the Phase 3 egress proxy |
| Redirect from an allowed page to an internal address | blocked by Phase 3 (exercised through the workflow path) |
| Secret value | never in log entries, ILogger output, results, step records or error messages; masked in screenshots (pixel test); rejected in every non-fill sink |
| Secret used in a condition to probe its value | rejected at validation (sink rules) |
| Script or code in JSON (`"type":"script"`, `"evaluate"`, `"javascript:"` URL, CSS engine prefixes) | rejected by parser/validator; no handler exists |
| Upload reference `../../etc/passwd` | rejected: only `IExecutionFileProvider` content is uploadable; no filesystem path ever reaches Playwright |
| Oversized/deeply nested definition, 10,000 conditions | rejected by limits before execution |
| Hosted mode with `AllowPrivateNetworkInDevelopment` | ignored or rejected (`PolicyViolation`) |

**Architecture** (`DependencyGraphTests`, `BrowserEngineSourceTests`):
- Api still ↛ Automation, and still has no Playwright package.
- Application stays package-free, including the moved Network types.
- No `Microsoft.Playwright` in `Automation/Workflows` (handlers use `IBrowserSession` only).
- No script/eval APIs (existing scan).
- Every `ActionCatalog` type has a handler and a schema entry.
- Domain has no new references.

---

## 14. Dependencies and project references
- **No new projects, no new packages.** `System.Text.Json` is in the BCL.
- **Project references are unchanged:** Domain ← Application ← Automation ← Worker; the Api stays independent of Automation.

| Project | Phase 4 additions |
|---|---|
| Domain | `Workflows/WorkflowVersion` (entity rules only) |
| Application | `Workflows/Definition` (records, parser, validator, templates, sink rules, conditions model, `ActionCatalog`, `RecordValidator`, canonicalizer and hash), `Workflows/Execution` (request/result/error contracts), ports `ISecretProvider` and `IExecutionFileProvider`, `Network` (the five moved pure policy types) |
| Automation | `Workflows/` (registry, 12 handlers, `WorkflowExecutor`, `VariableResolver`, `ConditionEvaluator` runtime, terminal-outcome state, `AddWorkflowEngine`); `Browser/` additive extensions (`OperationOptions`, `UploadAsync`, `CountAsync`, `IsVisibleAsync`, `CurrentUrl`) |
| Tests | UnitTests (Application + Automation unit), AutomationTests (real-browser workflow tests), architecture tests extended |
| Not touched | Infrastructure, Api, Worker, DemoCrm, web |

---

## 15. Implementation steps, effort, acceptance and risks

### Steps and estimate (indicative, ADR-034)
| # | Step | Days |
|---|---|---|
| 1 | Move the five pure network policy types to Application (no behaviour change; all 508 Phase 3 tests green before continuing) | 0.25 |
| 2 | Phase 3 extensions: `OperationOptions`, `UploadAsync`, `CountAsync`, `IsVisibleAsync`, `CurrentUrl` (+ tests, including step timeout keeps the session) | 0.5 |
| 3 | Definition model, strict parser, limits, canonicalizer and hash (golden values), `WorkflowVersion` domain rules | 0.75 |
| 4 | Templates (compile, references, dominance, URL authority rule, encoding), taint and sink rules, parameter binding rules, `RecordValidator` | 0.75 |
| 5 | Action catalog (12 descriptors + config validation), terminal-outcome validation, condition model and evaluator | 0.75 |
| 6 | Registry, 12 handlers, executor (lifecycle, terminal-outcome state, error policy, side effects and reconcile, logging) + unit tests with a fake session | 1.0 |
| 7 | DemoCrm workflow JSON + real-browser integration and security tests | 0.75 |
| 8 | JSON Schema file, `docs/WORKFLOWS.md` authoring guide, ADR updates, verification, commit | 0.25 |
| | **Total** | **5.0 (range 4.5–6)** |

### Acceptance checklist
1. [x] D1–D7 decided and recorded (ADR-037 to ADR-043); sink rules recorded (ADR-044).
2. [ ] The five pure policy types moved to Application; runtime enforcement unchanged in Automation; all 508 Phase 3 tests green after the move.
3. [ ] Phase 3 extensions are additive; a step timeout never terminates the session; uploads are content-only; probes never throw for missing elements.
4. [ ] Strict, versioned parser with limits; unknown properties, schema versions, loops and regular expressions rejected; non-integer numbers rejected.
5. [ ] Hand-written validator covers structure, all 12 action configs, templates, dominance, URL authority, sink rules, parameters, side effects and terminal-outcome rules.
6. [ ] `ActionCatalog` (Application) ↔ handlers (Automation) 1:1, verified at startup and by tests; the Api uses only Application.
7. [ ] Variables for `record` / `steps` / `operation` / `params` / `secret` with static checking and context-aware encoding.
8. [ ] Sink rules enforced at validation and run time: secrets only in `fill.value`; sensitive inputs and extracts only in fill / select / condition operands. Sensitive values never in logs, results, step records or errors. Screenshot masking as specified; universal secret detection is **not** claimed.
9. [ ] Conditions: closed predicate set with all/any/not; no scripting or regex.
10. [ ] Exactly one terminal outcome per run (set once, never overwritten; static and run-time overwrite prevention; per-section statuses; reconcile fields discarded on `notFound`).
11. [ ] Errors classified with category and transience; `onError` policies; `Indeterminate` after side effects (including transient business failures).
12. [ ] Executor runs one record attempt per call with a fresh Phase 3 session that is always disposed; record-level request/result ready for Phase 5.
13. [ ] Immutability: deterministic canonicalization with golden-value tests, tamper check, `WorkflowVersion` lifecycle; schemaVersion dispatch.
14. [ ] Typed parameters bound with origin-only / https / private-IP rules; usable only as listed `allowedHosts` prefixes; never from records.
15. [ ] DemoCrm workflow (pure JSON) passes all §12 scenarios in real Chromium, including duplicate-safe reconciliation.
16. [ ] Test matrix (§13) implemented; 0 warnings; all tests green in the verification container (two consecutive runs).
17. [ ] Docs: `docs/WORKFLOWS.md` (authoring guide incl. limitations and the reconciliation cost), `docs/workflow-schema-v1.json`, ADRs, ROADMAP; secrets audit; separate commit; clean tree.

### Risks
| Risk | Mitigation |
|---|---|
| Template/condition language creep towards scripting | Closed sets, no regex/functions in v1 (ADR-042); validator rejects unknown keys |
| Incomplete taint leaks PII or secrets | Taint carried in `ResolvedValue` (cannot be dropped silently); sink rules checked at validation **and** run time; log entries carry no values; leak tests over all log/result channels |
| Step-dominance analysis bugs allow unresolved references | Exhaustive branch tests; run-time `VariableUnresolved` as the backstop |
| Terminal-outcome logic errors | Single `TrySetTerminal` transition; validator rules; exhaustive unit tests including validator-bypass tests |
| Canonicalization drift changes hashes of published versions | Golden-value tests on canonical bytes and hash; integer-only numbers; duplicate keys rejected |
| Conditions racing with navigation | Side-effect clicks use `waitForNavigation` (Phase 3 `SubmitAsync`, new-document load); probes are immediate and documented as such; authors add `wait` steps |
| Moving network types breaks Phase 3 | Pure move with namespace update; all 508 existing tests must stay green before continuing |
| Changing Phase 3 signatures | Optional parameters only; existing call sites unchanged |
| Extra reconcile runs for failures before commit (§10) | Accepted trade-off (ADR-039); documented for workflow authors; bounded by Phase 5 retry limits |
| DemoCrm-specific assumptions creeping into the engine | Engine tests use fixture workflows too; architecture test bans DemoCrm references from Automation (already enforced) |
| JSON Schema drifting from the code validator | Test compares the schema's action list, statuses and types with the code |

---

## Decisions (resolved)
All decisions are approved. §16 holds the binding text, and the ADRs hold the permanent record.

| # | Decision | Status | Binding record |
|---|---|---|---|
| D1 | Pure host/IP policy types move to Application; runtime enforcement stays in Automation | Approved | §16 D1, ADR-037 |
| D2 | Step timeouts that keep the session; content-only upload; safe page probes | Approved | §16 D2, ADR-038 |
| D3 | `captureResult` as the terminal outcome mechanism | Approved | §16 D3, ADR-039 |
| D4 | Sections `setup` / `reconcile` / `steps`; Phase 5 decides when to reconcile | Approved | §16 D4, ADR-040 |
| D5 | Draft → Published → Archived; immutable published definitions; deterministic hashing | Approved | §16 D5, ADR-041 |
| D6 | No loops, no regular expressions in schemaVersion 1 | Approved | §16 D6, ADR-042 |
| D7 | Typed parameters that cannot bypass host restrictions | Approved | §16 D7, ADR-043 |
| — | Sink rules (secret vs sensitive value) | Recorded with this plan revision | §16 Sink rules, ADR-044 |

---

## Appendix A — JSON Schema outline (`docs/workflow-schema-v1.json`)
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
    "timeoutSeconds": { "type": "integer", "minimum": 1, "maximum": 3600 },
    "parameters": { "type": "array", "maxItems": 20, "items": { "$ref": "#/$defs/parameter" } },
    "allowedHosts": { "type": "array", "minItems": 1, "items": { "type": "string" } },
    "inputs": { "type": "array", "maxItems": 100, "items": { "$ref": "#/$defs/input" } },
    "secrets": { "type": "array", "maxItems": 20, "items": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" } },
    "maskSelectors": { "type": "array", "items": { "$ref": "#/$defs/selector" } },
    "setup": { "$ref": "#/$defs/steps" }, "reconcile": { "$ref": "#/$defs/steps" }, "steps": { "$ref": "#/$defs/steps" }
  },
  "$defs": {
    "steps": { "type": "array", "items": { "$ref": "#/$defs/step" } },
    "step": { "type": "object", "required": ["id", "type", "config"], "additionalProperties": false,
      "properties": { "id": { "type": "string", "pattern": "^[a-z][a-z0-9-]{0,39}$" },
        "type": { "enum": ["navigate","click","fill","select","wait","extract","screenshot","upload","download","condition","delay","captureResult"] },
        "config": { "type": "object" }, "timeoutMs": { "type": "integer", "minimum": 100, "maximum": 120000 },
        "onError": { "enum": ["fail", "continue"] }, "sideEffect": { "type": "boolean" }, "description": { "type": "string", "maxLength": 500 } },
      "allOf": [ /* one "if type == X then config: $defs/X" per action */ ] },
    "selector": { "oneOf": [ /* testId | css | role(+name) | text | label | placeholder | xpath */ ] },
    "condition": { "oneOf": [ /* all | any | not | elementExists | elementVisible | elementCountAtLeast | textEquals | textContains | attributeEquals | urlMatches | valueEquals | valueContains | valueStartsWith | isEmpty */ ] },
    "parameter": { "type": "object", "required": ["name", "type"], "additionalProperties": false,
      "properties": { "name": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" }, "type": { "enum": ["url", "string"] } } },
    "input": { "type": "object", "required": ["name", "type"], "additionalProperties": false,
      "properties": { "name": { "type": "string", "pattern": "^[A-Za-z][A-Za-z0-9]{0,63}$" },
        "type": { "enum": ["string", "email", "integer", "decimal", "boolean", "date", "file"] },
        "required": { "type": "boolean" }, "maxLength": { "type": "integer", "minimum": 1, "maximum": 10000 },
        "sensitive": { "type": "boolean" } } },
    "captureResult": { "type": "object", "required": ["fields"], "additionalProperties": false,
      "properties": { "fields": { "type": "object", "additionalProperties": { "type": "string" } },
        "outcome": { "$ref": "#/$defs/outcome" } } },
    "outcome": { "type": "object", "required": ["status"], "additionalProperties": false,
      "properties": {
        "status": { "enum": ["succeeded", "failed", "reconciled", "notFound", "needsReview"] },
        "code": { "type": "string", "pattern": "^[A-Z][A-Za-z0-9]{0,63}$" },
        "transient": { "type": "boolean" },
        "transientWhen": { "$ref": "#/$defs/condition" } } }
    /* plus one config definition per remaining action, matching §2.
       Rules JSON Schema cannot express (per-section statuses, last-in-sequence, overwrite prevention,
       sink rules, dominance, parameter usage) are enforced only by the hand-written validator. */
  }
}
```
The full file is produced in implementation step 8. Until then the hand-written validator is authoritative and the schema is for editor assistance.

---

## 16. Approval record (2026-11-12) and binding clarifications — AUTHORITATIVE
All seven decisions were approved by the user. This section turns their explicit conditions into binding design rules, and it takes precedence over every other section. Status: **plan approved in principle; implementation awaits final approval.**

### D1: pure policy moves, enforcement stays (ADR-037)
- **What moves:** only the five pure definitions and parsers move to `AutomataFlow.Application.Network`: `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy`, `NormalizedHost`.
- **What stays in Automation, unchanged:** all **runtime enforcement**: `RequestGuard`, the route/WebSocket guards, redirect inspection, `EgressProxyServer` (DNS, IP checks, pinning, quotas), fail-closed gating, the Chromium flags and the sandbox.
- **Acceptance gate:** the full Phase 3 suite (508 tests) must pass unchanged, apart from namespaces, before any other Phase 4 work starts.

### D2: a step timeout never terminates the session (ADR-038)
- **How timeouts are applied:** a step's `timeoutMs` reaches Playwright as **that call's own timeout** via `OperationOptions`. When it expires, Playwright raises a timeout for that call only. The context, page, cookies and login stay intact, so `onError: "continue"` steps can proceed.
- **What still closes the session:** only the session-terminating timeouts (the caller's cancellation token, the workflow `timeoutSeconds` and the Phase 3 session deadline).
- **Error mapping:** a step timeout produces `Timeout` (navigation) or `ElementNotFound` (element wait), transient, **with the session still usable**.
- **Tests:** a step timeout followed by further successful steps in the same session (real Chromium), and a session-terminating timeout test proving that one does end the run.
- **Uploads:** content only, from `IExecutionFileProvider` as an in-memory `FilePayload`. A filesystem path can never reach Playwright.
- **Safe page checks:** `CountAsync` and `IsVisibleAsync` are immediate (no auto-wait) and never throw for missing elements. `CurrentUrl` is read raw for conditions but always logged redacted.

### D3: terminal outcome semantics (ADR-039)
A run ends with **exactly one** terminal outcome, which **cannot be changed once set**.

| Rule | Definition |
|---|---|
| Sources of a terminal outcome | (a) a step error under `onError: "fail"`; (b) a terminal `captureResult` (one with an `outcome`); (c) normal completion of `steps` without a terminal `captureResult` (implicit `Succeeded`); (d) a reconcile outcome of `reconciled` or `needsReview`; (e) cancellation or a session-terminating timeout |
| Terminal `captureResult` | Stops execution immediately; no later step runs. Must be the **last** step of its sequence (validator: no unreachable steps after it). |
| Outcomes allowed per section | `setup`: `failed` only (a setup cannot "succeed" the run). `reconcile`: `reconciled` / `notFound` / `needsReview` only, and mandatory on every path. `steps`: `succeeded` / `failed`. |
| Non-terminal `captureResult` (no `outcome`) | Only adds result fields. |
| Overwrite prevention, static | A result field name may be written **at most once on any execution path**. Two `captureResult` steps that can both run on one path and write the same field is a validation error. |
| Overwrite prevention, run time | Writing an existing field, or setting a second terminal outcome, raises `ResultOverwrite` (permanent). This backstop should be unreachable after validation, and is tested by bypassing the validator. |
| Reconcile fields | If reconcile ends `notFound`, its fields are discarded before the main steps run, so results never mix. `reconciled` / `needsReview` keep theirs. |
| Interaction with side effects | A `failed` outcome whose transience is true (`transient: true` or `transientWhen` evaluating true), declared after a side-effect step started, is reported as `Indeterminate`, preserving the declared code. Permanent failures stay `Failed`. |
| Late cancellation | A cancellation that arrives after the terminal outcome has been set cannot change it; it only shortens how long session cleanup waits. |
| Consequence (accepted) | Because a transient failure after a side effect is always `Indeterminate`, failures that happened before the target saved anything (e.g. DemoCrm `+fail-N`) also cost one reconciliation pass before the next attempt. This is intended (§10). |
| Implementation | The executor holds the outcome in a small state object with a single `TrySetTerminal(...)` transition that refuses a second set; unit-tested exhaustively. |

### D4: sections (ADR-040)
- **Order:** `setup` → (`reconcile` only when the request's `Intent = Reconcile`) → `steps`.
- **When reconciliation runs:** the engine never decides this; Phase 5 does.
- **Side effects:** neither `setup` nor `reconcile` may contain side-effect steps; login is not a side effect on the target's business data.

### D5: lifecycle and deterministic hashing (ADR-041)
- **Lifecycle:** `WorkflowVersion`: `Draft` → `Published` → `Archived`, with no transitions back. In `Published` and `Archived` the definition, schema version and hash are read-only; any mutation throws. Edits create a new version number.
- **Canonical form** (inspired by RFC 8785, JSON Canonicalization Scheme, implemented by hand with System.Text.Json):
  - UTF-8 without BOM;
  - object properties sorted by ordinal UTF-16 code-unit order;
  - no insignificant whitespace;
  - strings with minimal escaping (`"`, `\`, control characters as `\u00XX`, and the short escapes `\b \f \n \r \t`);
  - numbers restricted to integers in the schema (timeouts, indexes, limits) and written in plain decimal form; non-integer numbers are rejected by the parser, so no floating-point formatting ambiguity exists;
  - `true` / `false` / `null` literal.
- **Duplicate properties** are rejected *before* canonicalization; a duplicate key never silently wins.
- **Hash:** SHA-256 over the canonical bytes, lowercase hex, stored on publish. The executor recomputes it and refuses a mismatch (`WorkflowTampered`).
- **Tests:** the same hash under property reordering, whitespace and escaping variants; a different hash on any semantic change; golden-value tests pinning the exact canonical bytes and hash for the DemoCrm sample, so canonicalization can never drift silently.

### D6: no loops, no regular expressions in schemaVersion 1 (ADR-042)
The parser rejects any `loop`/`repeat`/`forEach`/`while` keys and any `regex`/`matches` predicate as unknown properties. Introducing either requires a new schemaVersion and an ADR.

### D7: typed parameters that cannot bypass host restrictions (ADR-043)
| Type | Validation at bind time | Where usable |
|---|---|---|
| `url` | parsed with the moved `AllowedHostRule` parser; **origin only** (scheme, host, port: no path, query, fragment or credentials); `https` only outside Development; rejected if the host is an IP literal in a private range and the mode is not Development | only as a whole `{{params.x}}` prefix of `navigate.url` **and only if that same parameter is listed in `allowedHosts`**; never inside another URL's path or authority |
| `string` | length 1–200, no control characters | template values in non-URL sinks (`fill`, `select`, condition operands, selector values); never in a URL scheme or authority |

- Parameters come from the job configuration (Phase 5), **never from record data**. Records cannot set or override parameters.
- The bound `url` parameters become `EgressPolicy` rules, after which **all Phase 3 runtime enforcement applies unchanged**. Even a parameter pointing at a public name that later resolves to a private IP is stopped by the egress proxy.
- **Tests:** an origin with a path or credentials is rejected; `http` in hosted mode is rejected; private IP literals are rejected outside Development; a parameter not in `allowedHosts` used in `navigate` is rejected; a parameter whose DNS later resolves privately is blocked by the proxy; a record field named like a parameter cannot influence it.

### Sink rules: secret versus sensitive value (ADR-044)
These rules were clarified during the plan review (2026-11-12). The full matrix is in §5; this is the binding summary:
- **Secrets** (`{{secret.x}}`) are accepted **only** in `fill.value`. Everywhere else they are rejected at validation and again at run time, including condition operands and selectors, which would allow probing a secret through page behaviour.
- **Sensitive inputs and sensitive extracts** are accepted in `fill.value`, `select` and condition operands. They are rejected in `navigate.url`, `captureResult.fields`, selector values, `screenshot.label` and `upload.file`.
- **Taint propagates:** any resolved template that contains a secret or a sensitive value is itself treated as such.
- **Logs never contain sensitive values,** and execution events contain no input or output values at all.
- **Screenshot masking** covers sensitive fills, the Phase 3 built-ins and declared `maskSelectors`. It does **not** detect every secret a site displays.
