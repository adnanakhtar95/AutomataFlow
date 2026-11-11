# Phase 4 — Workflow Engine and Action Registry: Implementation Plan

Status: **Proposed — awaiting approval. No Phase 4 code exists.**
Basis: Phase 3 as committed in `fd942c2`. Real interfaces reused: `IBrowserSessionFactory`, `IBrowserSession`, `ElementSelector`, `InputValue`, `SelectChoice`, `WaitCondition`, `BrowserSessionRequest`, `BrowserOperationException`/`BrowserErrorCode`, `EgressPolicy`/`AllowedHostRule`, `ExecutionScope`, `IExecutionLogSink`, `IArtifactStore`.

**Out of scope (Phase 5/6):** database persistence, job scheduling, CSV upload infrastructure, Worker implementation, retries, frontend.

Phase 4 delivers one engine call that executes one workflow version for **one record attempt**. Phase 5 calls it once per CSV row and attempt.

---

## 0. Findings from inspecting Phase 3 (gaps this plan must close)
| Gap in `IBrowserSession` (fd942c2) | Needed by | Proposal (additive, no new browser abstraction) |
|---|---|---|
| No file upload | Upload action | `UploadAsync(ElementSelector, UploadFile, OperationOptions?, ct)` using Playwright `SetInputFilesAsync(FilePayload)`. The engine passes in-memory content only, never a filesystem path. |
| No non-throwing page probes | Condition (`elementExists`, `elementVisible`, `urlMatches`) | `CountAsync(selector, ct)`, `IsVisibleAsync(selector, ct)` (immediate, no auto-wait) and `CurrentUrl` (logged redacted). |
| No per-call timeout (operations use the global `ActionSeconds`) | Step `timeoutMs`; `onError: continue` must not kill the session | Optional `OperationOptions(TimeSpan? Timeout)` on every operation, passed to Playwright as its own timeout. Today the only way to time out a single call is cancellation, which closes the whole context (by design). |
| `AllowedHostRule`/`EgressPolicy`/`HostNameNormalizer`/`IpAddressPolicy` live in Automation | Publish-time validation of `allowedHosts` belongs in Application (the Api must never reference Automation, ADR-017) | **Decision D1:** move these four pure, dependency-free types (no Playwright, no packages) to `AutomataFlow.Application.Network`. Behaviour and tests are unchanged. The proxy server, request guard and browser code stay in Automation. |

All Phase 3 protections stay in force unchanged: every navigation still goes through `RequestGuard`, the egress proxy, fail-closed behaviour, the sandbox, redaction and masking.

---

## 1. Versioned JSON workflow schema (schemaVersion 1)

### 1.1 Shape
```jsonc
{
  "schemaVersion": 1,                       // format version (§11); required, integer
  "name": "democrm-register-customer",      // ^[a-z][a-z0-9-]{2,63}$
  "description": "Registers one customer in DemoCrm.",   // optional, <= 500 chars
  "timeoutSeconds": 120,                    // whole execution, 1..session maximum
  "parameters": [                           // per-environment values supplied by the job (never by the CSV row)
    { "name": "crmBaseUrl", "type": "url" },
    { "name": "crmUser", "type": "string" }
  ],
  "allowedHosts": [                         // literal origins and/or url parameters; nothing else is reachable
    "{{params.crmBaseUrl}}"
  ],
  "inputs": [                               // record (CSV/JSON row) schema; Phase 5 validates rows against this
    { "name": "email", "type": "email", "required": true, "maxLength": 120 },
    { "name": "company", "type": "string", "required": false },
    { "name": "taxId", "type": "string", "required": false, "sensitive": true }
  ],
  "secrets": ["crmPassword"],               // names only; values come from ISecretProvider at run time
  "maskSelectors": [{ "css": "[data-testid=detail-tax-id]" }],   // extra screenshot masking for this site
  "setup":     [ /* steps run first on every attempt, e.g. login */ ],
  "reconcile": [ /* optional; run only when the caller requests reconciliation (§10) */ ],
  "steps":     [ /* main steps */ ]
}
```

### 1.2 Step shape (all actions)
```jsonc
{
  "id": "submit",                 // ^[a-z][a-z0-9-]{0,39}$, unique across the whole workflow
  "type": "click",                // one of the 12 action types
  "config": { },                  // action-specific, strictly typed (§2)
  "timeoutMs": 15000,             // optional; 100..120000; default per action
  "onError": "fail",              // "fail" (default) | "continue" (not allowed on sideEffect steps)
  "sideEffect": false,            // true = this step may change the target system (§10)
  "description": "optional text"
}
```

### 1.3 Selectors (map 1:1 to Phase 3 `ElementSelector`)
`{"testId": "x"}` · `{"css": "x"}` · `{"role": "button", "name": "Sign in"}` · `{"text": "x"}` · `{"label": "x"}` · `{"placeholder": "x"}` · `{"xpath": "//x"}`

- **Templates in selectors:** placeholders are allowed only in `testId`, `text`, `label`, `placeholder` and `name` values. They are never allowed in `css` or `xpath`, so record data cannot alter selector structure.
- **Validation:** the existing `ElementSelector` validation (length, engine prefixes, `>>` chains) is applied after resolution.

### 1.4 Templates
- **Syntax:** `{{namespace.path}}` only. No expressions, filters, functions, arithmetic or loops.
- **Namespaces:** `record`, `steps`, `operation`, `params` and `secret` (§4). A literal `{{` is a validation error in v1.

### 1.5 Formal JSON Schema
A JSON Schema (draft 2020-12) for the editor and tooling ships as `docs/workflow-schema-v1.json`; Appendix A has its outline. The **hand-written validator in Application is authoritative** (ADR-016). A test keeps the schema's action list identical to the action catalog.

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

**No loops in v1.** Unbounded control flow is out of scope (decision D6).

---

## 2. The 12 actions: contracts and classes

Each action has two halves, keeping the Api independent of Automation:

- **Application: `ActionDescriptor`** (static catalog). Type id, config parser and validator, default timeout, declared outputs, capability flags. Used for publish-time validation, by the editor (Phase 6) and by the executor.
- **Automation: `IActionHandler<TConfig>`**. Executes against `IBrowserSession` only, never Playwright directly (architecture-tested).

| Type | Config (typed record) | Phase 3 operation used | Outputs (`steps.<id>.*`) | sideEffect allowed | Default timeout |
|---|---|---|---|---|---|
| `navigate` | `url` (template; authority must be a literal allowed origin or a url param, §6) | `NavigateAsync` | `status`, `url` (redacted) | no | navigation |
| `click` | `selector`, `waitForNavigation` (bool) | `ClickAsync` or, when waiting, `SubmitAsync` | — | **yes** | action |
| `fill` | `selector`, `value` (template; sensitive taint, §5) | `FillAsync(InputValue.Plain/Sensitive)` | — | no | action |
| `select` | `selector`, one of `value` / `label` / `index` (template) | `SelectAsync(SelectChoice)` | — | no | action |
| `wait` | `for`: `visible`/`hidden`/`attached`/`detached` + `selector`, or `url` glob, or `load` | `WaitForAsync(WaitCondition)` | — | no | action |
| `extract` | `selector`, `source`: `text` or `attribute` (+ `attribute`), `sensitive` (bool), `maxLength` | `GetTextAsync` / `GetAttributeAsync` | `value` | no | action |
| `screenshot` | `label`, `fullPage` | `ScreenshotAsync` | `artifactId` | no | action |
| `upload` | `selector`, `file` (template resolving to a file reference) | **new** `UploadAsync` with content from `IExecutionFileProvider` | `fileName` | **yes** | action |
| `download` | `trigger` selector | `DownloadAsync` (session created with `AcceptDownloads` when the workflow contains a download step) | `artifactId`, `fileName`, `sha256` | no | download |
| `condition` | `if` (expression, §8), `then` steps, `else` steps | **new** `CountAsync` / `IsVisibleAsync` / `CurrentUrl`, plus `GetTextAsync` / `GetAttributeAsync` | `result` (bool) | no (the branches may) | action |
| `delay` | `milliseconds` (1..30000) | none (`Task.Delay` with the execution token) | — | no | n/a |
| `captureResult` | `fields` (name → template, non-sensitive only), optional `outcome` (§9, §10) | none | — | no | n/a |

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

- **`StepContext`** gives a handler the `IBrowserSession`, the `VariableResolver`, a per-step `OperationOptions` (timeout), the `ExecutionScope` and an output writer.
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
- **Hosts register what Phase 5 needs:** `ISecretProvider` and `IExecutionFileProvider` (new Application ports, §4/§5), plus the Phase 3 `IArtifactStore` and `IExecutionLogSink`. Phase 4 tests use in-memory doubles.
- **The Api calls only Application** (`WorkflowDefinitionParser`, `WorkflowDefinitionValidator`, `ActionCatalog`), never `AddWorkflowEngine`. The architecture test still enforces Api ↛ Automation.

---

## 4. Variable resolution
| Namespace | Source | Example | Rules |
|---|---|---|---|
| `record` | the record (one CSV/JSON row), validated against `inputs` | `{{record.email}}` | must be a declared input; `sensitive: true` inputs are tainted |
| `steps` | outputs of earlier steps | `{{steps.conf-id.value}}` | the referenced step must **dominate** this use (always executed before it on every path: an earlier step in the same or an enclosing sequence, or in `setup`); the output name must be declared by that action |
| `operation` | stable per-record id supplied by the caller (ADR-015) | `{{operation.id}}` | the only key is `id`; it never changes across attempts |
| `params` | per-environment parameters supplied by the job | `{{params.crmBaseUrl}}` | must be declared; `url` params must be valid allowed origins (§6) |
| `secret` | `ISecretProvider` at run time | `{{secret.crmPassword}}` | must be declared in `secrets`; always tainted; allowed **only** in `fill.value` |

- **Compile once:** at validation time each template is compiled into segments (literal or placeholder). All references are checked statically, so nothing is "discovered" at run time.
- **Run time:** `VariableResolver.Resolve(template, context)` returns a `ResolvedValue(string Text, bool IsSensitive)`. A missing value at run time, which is only possible for optional inputs, resolves to the empty string unless the use site requires a value (e.g. a navigate URL).
- **Context-aware encoding:** in `navigate.url`, placeholders after the authority are percent-encoded (`Uri.EscapeDataString`). Placeholders are **forbidden in the scheme and authority**, except a whole `{{params.<url>}}` origin prefix. Record data can therefore never change the destination host (no `@evil.test` or `//evil.test` injection).
- **Record typing:**
  - Inputs declare `type` (`string` | `email` | `integer` | `decimal` | `boolean` | `date` | `file`), `required`, `maxLength` and `sensitive`.
  - `RecordValidator` (Application) validates a record before execution. Phase 5 reuses it per CSV row, so no redesign is needed.

---

## 5. Secrets and sensitive values
- **Source:** `ISecretProvider` (Application port: `GetAsync(ExecutionScope scope, string name, ct)`). Values are resolved only when executing, kept in memory only for the execution's lifetime, and never written to step outputs, results or logs. Phase 5 implements encrypted, organization-scoped storage; Phase 4 uses an in-memory test double. **No secret is ever stored in the workflow JSON.**
- **Taint propagation:** a resolved value is sensitive if any segment is sensitive (`secret.*`, a `sensitive` input, or the output of `extract` with `"sensitive": true`). Sensitivity is carried by `ResolvedValue`.
- **Where sensitive values may flow:**
  - `fill.value` (passed as `InputValue.Sensitive`, so it is masked and redacted by Phase 3);
  - `select` (the Phase 3 redactor learns the value);
  - condition comparisons, where the result is logged as true/false only.

  They are rejected by the validator (and checked again at run time) in `navigate.url`, `captureResult.fields`, selector values, `screenshot.label` and upload references.
- **Logging:**
  - Step events carry ids, types, durations and error codes, **never input or output values**: even non-secret record values are customer PII.
  - The resolver registers every sensitive value with the session's redactor before use (the Phase 3 `FillAsync` mechanism, extended to all sensitive resolutions), so failure messages stay redacted.
- **Screenshots:** masking covers every element filled with a sensitive value, the Phase 3 built-ins (password and card fields, `[data-af-sensitive]`) and the workflow's `maskSelectors`.
  - **Not covered:** the platform cannot detect or mask every secret a website displays as text. Workflow authors must declare such regions in `maskSelectors`.
  - The validator emits a warning, not an error, when a workflow extracts sensitive data but declares no `maskSelectors`.

---

## 6. Allowed hosts and SSRF integration
1. **Authoring time** (Application validator, using the moved `AllowedHostRule` parser, D1):
   - Every `allowedHosts` entry is a literal origin (`https://host[:port]`) or `{{params.<url param>}}`.
   - Every `navigate.url` must begin with one of those literal origins or with a url parameter that is listed in `allowedHosts`.
   - Wildcards follow the Phase 3 rules (subdomains only).
2. **Binding time** (executor, before the browser starts):
   - Parameter values are parsed as `AllowedHostRule` and must be origins only (no path or credentials).
   - The `EgressPolicy` is built from the literal origins plus the bound url parameters.
   - `allowPrivateNetwork` is set **only** when the request explicitly asks for it **and** the engine mode is Development (the DemoCrm demo). The workflow JSON stays environment-agnostic.
   - The Phase 3 session factory then applies its existing mode rules: hosted mode is https-only with no private exceptions.
3. **Run time:** every request still passes the Phase 3 `RequestGuard` and the authoritative egress proxy:
   - DNS resolution in the proxy;
   - private, loopback, link-local and metadata denial;
   - IP pinning;
   - redirect-hop inspection;
   - fail-closed behaviour.

   **Phase 4 adds nothing that bypasses them.** A blocked navigation surfaces as `NavigationBlocked` (permanent, §9).
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
    IReadOnlyDictionary<string, string?> Record,           // raw row values; validated by RecordValidator
    IReadOnlyDictionary<string, string> Parameters,
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
 ├─ if Intent == Reconcile: run reconcile steps ─► outcome Reconciled | NotFound | NeedsReview
 │        Reconciled / NeedsReview ──────────────────────────► finish
 │        NotFound ─► continue                                │
 ├─ run  main steps  ──────────────────────────────────────────┤
 │    for each step: resolve config ─► handler.ExecuteAsync(StepContext, timeout)
 │       success ─► record outputs ─► next
 │       error   ─► classify (§9) ─► onError=continue? next : stop
 │       sideEffect step ─► mark SideEffectStarted BEFORE invoking the handler
 │       condition ─► evaluate ─► run then/else sequence (same rules, nested)
 │       captureResult with outcome ─► stop with that outcome
 ├─ log WorkflowCompleted (outcome, error code, duration)
 └─ finally: dispose session (Phase 3 cleanup), clear secret values  ─► WorkflowExecutionResult
```

```csharp
public sealed record WorkflowExecutionResult(
    WorkflowOutcome Outcome,             // Succeeded | Failed | Indeterminate | Reconciled | NeedsReview | Cancelled
    ExecutionError? Error,               // code, category, IsTransient, step id, redacted message
    bool SideEffectStarted,
    IReadOnlyDictionary<string, string> Results,     // captureResult fields (never sensitive)
    IReadOnlyList<StepRecord> Steps,                  // id, type, status, duration, error code (no values)
    IReadOnlyList<ArtifactDescriptor> Artifacts);
```

- **Steps run strictly sequentially in one session per attempt.** No parallelism inside a workflow.
- **Cancellation** (the caller's token or `timeoutSeconds`) yields `Cancelled`, or `Indeterminate` if a side effect had started.
- **Phase 5 integration is already shaped:** Phase 5 only builds the request per row and attempt, maps the result onto `JobAttempt`/`JobRecord` statuses and persists the logs and artifacts. The engine needs no redesign.

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
| Engine | `RecordInvalid`, `VariableUnresolved`, `SecretUnavailable`, `UploadRejected`, `WorkflowTampered` | Permanent / no (`SecretUnavailable`: Transient) |
| `captureResult.outcome` | author-defined (e.g. `DuplicateCustomer`) | Business / as declared |
| Cancellation | `Cancelled` | Cancelled / no |

**Rules:**
- `onError: "fail"` (the default) stops the workflow. `onError: "continue"` records the error and proceeds. It is forbidden on `sideEffect` steps and on `captureResult`.
- Later steps can read `{{steps.<id>.failed}}`, which every step exposes: `true`/`false`.
- **ADR-015 rule:** a transient or unknown error, or a cancellation, *after* a side-effect step has started yields `Indeterminate`, never a plain `Failed`. Phase 5 must reconcile before repeating.
- A permanent error after a side effect yields `Failed`: the target answered definitively, e.g. a validation error page.

---

## 10. Side effects and reconciliation (prepared for Phase 5)
- **`sideEffect: true`** is allowed on `click` and `upload`. `SideEffectStarted` is set *before* the handler is invoked, so a crash or timeout during the click is still covered. The result carries `SideEffectStarted`.
- **`{{operation.id}}`** gives a stable idempotency key. The DemoCrm workflow fills it into the form's operation reference.
- **`reconcile` section:** steps run *instead of* blind repetition when `Intent = Reconcile`. They must finish with `captureResult` carrying one of these outcomes:
  - `{ "status": "reconciled" }` (found: fields captured, outcome `Reconciled`);
  - `{ "status": "notFound" }` (safe to run the main steps);
  - `{ "status": "needsReview" }` (inconclusive: outcome `NeedsReview`).

  The validator requires every path through `reconcile` to end in one of these outcomes, and forbids side effects inside `reconcile`.
- **Phase 4 boundary:** the engine executes reconcile on request and reports. **Deciding when to reconcile, retrying and persisting `Indeterminate`/`NeedsReview` are Phase 5.**
- **`captureResult.outcome` on main steps:** `{ "status": "failed", "code": "DuplicateCustomer", "transient": false }` ends the run with a business failure, and `{ "status": "succeeded" }` ends it with success. This is decision D3; the alternative is a 13th `fail` action.

---

## 11. Version immutability and schema versioning
- **`schemaVersion`** versions the *format*. The parser dispatches on it, rejects unknown versions and keeps a parser per supported version. Future versions add an explicit, tested upgrader (`v1 → v2`) that runs in memory. Stored definitions are never rewritten.
- **Workflow versions** version the *content*. Phase 4 adds the Domain entity `WorkflowVersion` (rules only, persisted in Phase 5; decision D5):
  - `Draft` (editable) → `Publish(hash, at)` → `Published` (immutable: any change throws) → `Archive`.
  - Edits after publishing create a new version.
- **`DefinitionSha256`:** computed over the canonical JSON (sorted properties, no insignificant whitespace) at publish. The executor recomputes it and refuses a mismatch (`WorkflowTampered`).
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

**Expected behaviour with DemoCrm** (each line is an integration test):
- **Valid row:** `Succeeded`, with `customerId=C-000025` and `registration=created`.
- **Seeded email:** `Failed` with `DuplicateCustomer` (permanent) and `existingCustomerId=C-000001`. No new customer.
- **`+fail-2`:**
  - Attempt 1: `Indeterminate` (transient after the side effect).
  - Attempt 2 (`Intent=Reconcile`): `notFound`, main steps run again, `Indeterminate` again.
  - Attempt 3: `Succeeded`.
- **`+commit-fail`:**
  - Attempt 1: `Indeterminate`.
  - Attempt 2 (`Intent=Reconcile`): `Reconciled`, with the existing customer id.
  - The customer count rises by exactly **1**.
- **Invalid row** (bad email in the record): `RecordInvalid` before any browser work.

The `transientWhen` field is part of decision D3. The `outcome` may carry a condition deciding transience, so a temporary error is reported as transient (and therefore `Indeterminate` after the side effect).

---

## 13. Test matrix
**Unit — Application** (no browser, `AutomataFlow.UnitTests`):
| Area | Scenarios |
|---|---|
| Parser | unknown properties rejected at every level; missing/unknown `schemaVersion`; wrong types; size limit; duplicate keys |
| Structure | unique step ids across setup/reconcile/steps/branches; id pattern; step/nesting/predicate limits; `onError=continue` forbidden on sideEffect/captureResult; sideEffect only on click/upload; no side effects in reconcile; every reconcile path ends with a valid outcome |
| Per action (12) | valid config; each required field; bounds (timeouts, delay ≤ 30 s, maxLength); selector rules (placeholders not in css/xpath); download implies `AcceptDownloads` |
| Templates | syntax errors (`{{`, unterminated, empty, unknown namespace); undeclared input/param/secret; step reference not dominating (later step, sibling branch); undeclared output name |
| URL safety | placeholder in scheme/authority rejected; `{{record.x}}` cannot become a host; origins must be allowed; path/query encoding (`a/b?c=@evil.test` stays in the path) |
| Sensitivity | secret only in `fill.value`; taint through `sensitive` inputs and sensitive extracts; sensitive values rejected in captureResult/navigate/selectors at validation **and** at run time |
| Conditions | each predicate; all/any/not truth tables; short-circuit order; depth/count limits; no regex/script keys accepted |
| Records | each input type (email/integer/decimal/boolean/date/file), required, maxLength, unknown columns ignored vs rejected (configurable), sensitive flag |
| Versioning | canonical hash stable under property reordering/whitespace and changes with content; `WorkflowVersion` Draft→Published→Archived; edit after publish throws |
| Host rules (moved, D1) | the existing Phase 3 rule/normalizer/IP tests keep passing unchanged after the move |

**Unit — Automation** (fake `IBrowserSession`, no browser):
- **Registry:** completeness (12↔12), duplicate handler, unknown type, config-type mismatch.
- **Handlers:** each of the 12 calls exactly the expected `IBrowserSession` operation with the right `ElementSelector` / `InputValue` (Sensitive vs Plain) / `SelectChoice` / `WaitCondition` / timeout.
- **Executor:**
  - sequential order; setup → main; reconcile intents (Reconciled / NotFound / NeedsReview);
  - `onError` continue/fail; `steps.x.failed`;
  - `SideEffectStarted` set before the handler; transient after the side effect gives `Indeterminate`; permanent after the side effect gives `Failed`;
  - cancellation before/after the side effect; workflow timeout; secret-provider failure;
  - tamper check; session always disposed (even on exceptions); no values in log entries.

**Integration — real Chromium** (`AutomataFlow.AutomationTests`, verification container):
- **DemoCrm workflow** (the §12 scenarios): success, duplicate, invalid record, `fail-2` across three attempts, `commit-fail` reconciliation (exactly one customer), search-based reconcile `notFound`.
- **Fixture workflows:** download and upload round-trip (content hash), screenshot artifact, conditions against real pages (`urlMatches`, `elementVisible`, `attributeEquals`), wait/delay, `onError: continue` keeping the session usable.
- **Step timeout:** a step's `timeoutMs` times out a single step without killing the session (proves the D2 extension).

**Security:**
| Scenario | Expectation |
|---|---|
| Record value `http://evil.test@127.0.0.1/` placed in a URL path | percent-encoded; destination host unchanged |
| Workflow navigating to a host not in `allowedHosts` | rejected at validation; also `NavigationBlocked` at run time when constructed directly |
| Url parameter pointing to `169.254.169.254` / `127.0.0.1` | binding rejects (non-Development) or the Phase 3 proxy blocks; no internal hit |
| Redirect from an allowed page to an internal address | blocked by Phase 3 (re-run through the workflow path) |
| Secret value | never in log entries, ILogger output, results, step records or error messages; masked in screenshots (pixel test) |
| Script or code in JSON (`"type":"script"`, `"evaluate"`, `"javascript:"` URL, CSS engine prefixes) | rejected by parser/validator; no handler exists |
| Upload reference `../../etc/passwd` | rejected: only `IExecutionFileProvider` content is uploadable; no filesystem path ever reaches Playwright |
| Oversized/deeply nested definition, 10,000 conditions | rejected by limits before execution |
| Hosted mode with `allowPrivateNetworkInDevelopment` | ignored or rejected (`PolicyViolation`) |

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
| Domain | `Workflows/WorkflowVersion` (entity rules only, D5) |
| Application | `Workflows/Definition` (records, parser, validator, templates, conditions model, `ActionCatalog`, `RecordValidator`, canonical hash), `Workflows/Execution` (request/result/error contracts), ports `ISecretProvider` and `IExecutionFileProvider`, `Network` (moved pure policy types, D1) |
| Automation | `Workflows/` (registry, 12 handlers, `WorkflowExecutor`, `VariableResolver`, `ConditionEvaluator` runtime, `AddWorkflowEngine`); `Browser/` additive extensions (D2) |
| Tests | UnitTests (Application + Automation unit), AutomationTests (real-browser workflow tests), architecture tests extended |
| Not touched | Infrastructure, Api, Worker, DemoCrm, web |

---

## 15. Implementation steps, effort, acceptance and risks

### Steps and estimate (indicative, ADR-034)
| # | Step | Days |
|---|---|---|
| 1 | D1 move of the pure network policy types to Application (no behaviour change; all Phase 3 tests green) | 0.25 |
| 2 | D2 Phase 3 extensions: `OperationOptions`, `UploadAsync`, `CountAsync`, `IsVisibleAsync`, `CurrentUrl` (+ tests) | 0.5 |
| 3 | Definition model, strict parser, limits, canonical hash, `WorkflowVersion` domain rules | 0.75 |
| 4 | Templates (compile, references, dominance, URL authority rule, encoding, sensitivity taint) + `RecordValidator` | 0.75 |
| 5 | Action catalog (12 descriptors + config validation) and condition model/evaluator | 0.75 |
| 6 | Registry, 12 handlers, executor (lifecycle, error policy, side-effect/reconcile, logging) + unit tests with fake session | 1.0 |
| 7 | DemoCrm workflow JSON + real-browser integration and security tests | 0.75 |
| 8 | JSON Schema file, docs (WORKFLOWS.md authoring guide), ADRs, verification, commit | 0.25 |
| | **Total** | **5.0 (range 4.5–6)** |

### Acceptance checklist
1. [ ] D1–D7 decided; ADRs recorded.
2. [ ] Phase 3 extensions are additive; all existing tests green; Phase 3 security protections unchanged.
3. [ ] Strict, versioned parser with limits; unknown properties and schema versions rejected.
4. [ ] Hand-written validator covers structure, all 12 action configs, templates, dominance, URL authority, sensitivity, side effects and reconcile completeness.
5. [ ] `ActionCatalog` (Application) ↔ handlers (Automation) 1:1, verified at startup and by tests; the Api uses only Application.
6. [ ] Variables for `record`/`steps`/`operation`/`params`/`secret` with static checking and context-aware encoding.
7. [ ] Secrets only via `ISecretProvider`, only into `fill`; never in logs, results, step records or errors; masked in screenshots. Universal secret detection is **not** claimed.
8. [ ] Conditions: closed predicate set with all/any/not; no scripting or regex.
9. [ ] Errors classified with category/transience; `onError` policies; `Indeterminate` after side effects; reconcile outcomes.
10. [ ] Executor runs one record attempt per call with a fresh Phase 3 session that is always disposed; record-level request/result ready for Phase 5.
11. [ ] Immutability: canonical hash, tamper check, `WorkflowVersion` rules; schemaVersion dispatch.
12. [ ] DemoCrm workflow (pure JSON) passes all §12 scenarios in real Chromium, including duplicate-safe reconciliation.
13. [ ] Test matrix (§13) implemented; 0 warnings; all tests green in the verification container (two consecutive runs).
14. [ ] Docs: `docs/WORKFLOWS.md` (authoring guide incl. limitations), `docs/workflow-schema-v1.json`, ADRs, ROADMAP; secrets audit; separate commit; clean tree.

### Risks
| Risk | Mitigation |
|---|---|
| Template/condition language creep towards scripting | Closed sets, no regex/functions in v1, ADR; validator rejects unknown keys |
| Incomplete sensitivity taint leaks PII/secrets | Taint is part of `ResolvedValue` (cannot be dropped silently); validation **and** run-time checks; log entries carry no values at all; leak tests over all log/result channels |
| Step-dominance analysis bugs allow unresolved references | Exhaustive branch tests; run-time `VariableUnresolved` as the backstop |
| Conditions racing with navigation | Side-effect clicks use `waitForNavigation` (Phase 3 `SubmitAsync`, new-document load); probes are immediate and documented as such; authors add `wait` steps |
| Moving network types (D1) breaks Phase 3 | Pure move with namespace update; all 508 existing tests must stay green before continuing |
| Changing Phase 3 signatures (D2) | Optional parameters only; existing call sites unchanged |
| DemoCrm-specific assumptions creeping into the engine | Engine tests use fixture workflows too; architecture test bans DemoCrm references from Automation (already enforced) |
| JSON Schema drifting from the code validator | Test compares the schema's action list and required properties with `ActionCatalog` |

---

## Decisions requested
| # | Decision | Recommendation |
|---|---|---|
| D1 | Move `AllowedHostRule`, `EgressPolicy`, `HostNameNormalizer`, `IpAddressPolicy` (pure) from Automation to `Application.Network`, so publish-time validation uses the same parser without the Api depending on Automation | **Move** (alternative: keep and duplicate a weaker syntactic check in Application, which risks divergence) |
| D2 | Additive `IBrowserSession` extensions: `OperationOptions` timeout, `UploadAsync` (content only), `CountAsync`, `IsVisibleAsync`, `CurrentUrl` | **Approve** |
| D3 | Business outcomes via `captureResult.outcome` (`succeeded`/`failed` with code and transience, `reconciled`/`notFound`/`needsReview`) instead of a 13th `fail` action | **Approve** |
| D4 | Workflow sections `setup` / `reconcile` / `steps` | **Approve** |
| D5 | Add Domain `WorkflowVersion` entity rules now (no persistence until Phase 5) | **Approve** |
| D6 | No loops and no regular expressions in schemaVersion 1 | **Approve** |
| D7 | `parameters` (per-environment values, e.g. base URL and user) with `url` parameters allowed as `allowedHosts` origins | **Approve** |

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
      "properties": { "id": { "pattern": "^[a-z][a-z0-9-]{0,39}$" },
        "type": { "enum": ["navigate","click","fill","select","wait","extract","screenshot","upload","download","condition","delay","captureResult"] },
        "config": { "type": "object" }, "timeoutMs": { "minimum": 100, "maximum": 120000 },
        "onError": { "enum": ["fail", "continue"] }, "sideEffect": { "type": "boolean" }, "description": { "maxLength": 500 } },
      "allOf": [ /* one "if type == X then config: $defs/X" per action */ ] },
    "selector": { "oneOf": [ /* testId | css | role(+name) | text | label | placeholder | xpath */ ] },
    "condition": { "oneOf": [ /* all | any | not | elementExists | elementVisible | elementCountAtLeast | textEquals | textContains | attributeEquals | urlMatches | valueEquals | valueContains | valueStartsWith | isEmpty */ ] },
    "parameter": { /* name, type: string | url */ }, "input": { /* name, type, required, maxLength, sensitive */ }
    /* plus one config definition per action, matching §2 */
  }
}
```
The full file is produced in implementation step 8. Until then the hand-written validator is authoritative and the schema is for editor assistance.
