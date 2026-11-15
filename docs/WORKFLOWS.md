# Authoring workflows (schemaVersion 1)

This guide is for people who write AutomataFlow workflow definitions. The binding specification is
`docs/PHASE4_PLAN.md` (§1–§11) and ADR-037 to ADR-066. The JSON Schema `docs/workflow-schema-v1.json` helps editors with
completion and early errors, but the server's hand-written validator is authoritative. Some rules exist only in the
validator: sinks, dominance, finalization, URL grammar, side-effect placement and reconciliation keys.

A complete, tested example is `docs/workflows/democrm-register-customer.json`. It registers one customer in the
fictional Demo CRM.

## 1. What a workflow is
A workflow is a declarative JSON document. It never contains code: there are no scripts, no JavaScript URLs, no
regular expressions and no loops (ADR-003, ADR-042). The Worker runs it once per record attempt in a fresh, isolated
Chromium session, behind the egress proxy (Phase 3).

```
{ "schemaVersion": 1, "name": "…", "timeoutSeconds": 120,
  "parameters": [...], "allowedHosts": [...], "secrets": [...], "inputs": [...], "maskSelectors": [...],
  "reconciliation": {...}, "setup": [...], "reconcile": [...], "steps": [...] }
```

| Section | Runs | Purpose |
|---|---|---|
| `setup` | in every attempt | sign in, open the start page; side effects are not allowed |
| `steps` | Normal attempts | the actual work; the only place for side effects |
| `reconcile` | Reconcile attempts only | find out, read-only, whether an earlier ambiguous attempt took effect |

An attempt runs **either** `steps` **or** `reconcile` after `setup`, never both (ADR-040).

## 2. Declarations
- **`parameters`** (`url` or `string`) are set per job by the operator, never by a record. A `url` parameter is
  canonicalized at bind time and can only fill the origin of a URL (ADR-043).
- **`allowedHosts`** are canonical origins (`https://crm.example.test`, no path, no trailing slash) or
  `{{params.x}}`. Navigation to any other host fails at validation, and also at run time (`NavigationBlocked`).
  Private and loopback addresses are reachable only in Development, and only for origins listed in the trusted server
  setting `DevelopmentPrivateNetworkOrigins` (ADR-047). No workflow, parameter, record or request field can grant
  such access.
- **`secrets`** are names only. Values come from the organization's secret store at run time. A secret may be used
  only as a `fill` value.
- **`inputs`** declare the record's columns: type (`string`, `email`, `integer`, `decimal`, `boolean`, `date`,
  `file`), `required`, `maxLength`, `sensitive`. An invalid record is refused before anything runs (`RecordInvalid`).
- **`maskSelectors`** are page regions that are painted over in every screenshot. **A website that displays a secret
  or personal data can only be masked if you list that region here.** Password fields, `autocomplete="cc-*"`
  fields, `[data-af-sensitive]` elements, fields filled from a secret or sensitive input, and sensitive extracts are
  masked automatically.

## 3. Templates
`{{record.<input>}}`, `{{params.<name>}}`, `{{secret.<name>}}`, `{{steps.<id>.<output>}}` and `{{operation.id}}`.
References are checked statically: the name must be declared, and a `steps.` reference must come from a step that
has always run earlier (dominance). Step outputs:

| Action | Outputs |
|---|---|
| `extract` | `value` |
| `screenshot` | `artifactId` |
| `download` | `artifactId`, `fileName`, `sha256` |
| `upload` | `fileName` |
| `condition` | `result` (`true`/`false`) |
| `reconcileMatch` | its declared `fields` |

**Sink rules (ADR-044).** Every template is checked against the place it is used.
- Secrets are allowed only in `fill`.
- Sensitive inputs and sensitive extracts may be filled, selected and compared in conditions (only the boolean result
  is exposed).
- Neither may appear in URLs, results, screenshot labels, outcome codes or selectors.

These rules are checked when you publish and again at run time.

## 4. URLs
- A URL template starts with a canonical origin or `{{params.<url>}}`. The record can never choose the scheme, the
  host or the port.
- Values placed in the path or the query are encoded for that position.
- A value that would produce an empty path segment, `.` or `..` in any spelling is rejected (`UnsafeUrlValue`).
- Literal `%2F`, `%5C`, `%2E` and control characters in a template are rejected.
- The final `AbsoluteUri` is validated against `allowedHosts`.
- Real-browser parity test U8: the URL Chromium reports has the same origin and path segments as the validated URI,
  for values containing spaces, `+`, `/`, `?`, `#`, `%`, `;`, `'` and non-ASCII text.

### URL patterns (`urlMatches`, `wait` with `url`)
- Patterns start with `https://…`, `http://…` or `**/`.
- `*` matches any characters except `/`; `**` matches any characters including `/`.
- Matching is case-sensitive and checks the whole URL, including the query string.
- `?`, `{`, `}`, `[`, `]` and `\` are rejected, so the URL's literal `?` cannot appear in a pattern; use `*` before
  the query part, e.g. `**/*page=2`.
- The semantics are identical to Playwright's own URL wait; the step 9 parity test checks this on real pages.

## 5. Actions
`navigate`, `click`, `fill`, `select`, `wait`, `extract`, `screenshot`, `upload`, `download`, `condition`, `delay`,
`captureResult`, `reconcileMatch`. See the schema for every property. Points to note:
- **Selectors** are typed data (`testId`, `css`, `role`+`name`, `text`, `label`, `placeholder`, `xpath`) and must match
  exactly one element. Prefer `testId`.
- **Timeouts:** `timeoutMs` (100–120,000) is ONE total deadline for the whole step. For a submitting click, this
  covers both the click and the page load. A step timeout never ends the browser session. The workflow's
  `timeoutSeconds` is the session deadline, and the server's `SessionSeconds` caps it (otherwise the attempt is
  `Refused`, `WorkflowTimeoutExceedsServerLimit`).
- **`onError: "continue"`** is allowed only on `wait`, `extract`, `screenshot`, `fill`, `select` and `condition`, never
  on a side effect. The step is then recorded as `FailedContinued` and its outputs are unavailable.
- **Conditions** probe immediately: they never wait. Put a `wait` step first if the page may still be loading.
- **Uploads** come from the organization's file store, never from a filesystem path. They are checked before
  anything is sent: size (step and server limits), extension (step and server lists, a subset of `.pdf .png .jpg
  .jpeg .docx .xlsx .csv .txt`), declared content type, and the file signature. A failure is `UploadRejected` and
  never counts as a started side effect. File names are sanitized.
- **Downloads** are stored as artifacts with their SHA-256 hash. Sessions accept downloads only when the workflow has
  a `download` step.

## 6. Finishing an attempt
Every path through `setup` and `steps` must end in a `captureResult` with an `outcome`
(`succeeded` or `failed` + `code` + `transient` / `transientWhen`); the validator rejects a path that can fall off the
end. A `reconcile` section is finished by its `reconcileMatch`. Exactly one terminal outcome is set per attempt (the
first wins).

Result fields are bounded: at most 50 fields, 2,000 characters each and 32 KiB in total. They are never sensitive.

## 7. Side effects, and why the engine is careful about them
A **side effect** is any action that changes the target system, typically the submit click. Rules:
- **`sideEffect` is mandatory** (`true` or `false`) on every `click` and `upload` in `steps`. A click that waits for
  navigation but is declared `false` produces a warning: check it, because it is usually a submit.
- **Undetectable side effects.** The engine cannot see a state-changing GET `navigate`, or a `fill`/`select` that
  auto-submits. Model such actions as a declared side-effect `click`, or do not automate them.
- Side-effect steps need `timeoutMs` ≥ 5,000.
- **Durable checkpoint.** Immediately before dispatching a side effect, the Worker writes a durable, fenced checkpoint
  to the attempt journal. If the attempt then ends in any way other than a success or a declared permanent business
  failure, it is **`Indeterminate`**: the action may or may not have happened. Examples are a timeout, a crash, a
  cancellation and a transient error. In this case:
  - only a `captureResult` with `status: failed` and `transient: false` gives `Failed`;
  - everything else gives `Indeterminate`, keeping its error code (ADR-051).

### Repeating after an ambiguous attempt
The `RepeatSafetyEvaluator` decides what may happen next for an operation, using only the attempt journal. Its
default is `NeedsReview` (ADR-045). A Normal repeat after an `Indeterminate` attempt is allowed only in one of these
cases:
1. **Idempotency attestation:** every side-effect step declares
   `"idempotency": { "key": "{{operation.id}}", "guarantee": "targetEnforced" }`, and the operation id is actually sent
   to the target (for example filled into a field). You attest that **the target** deduplicates on that key. The engine
   cannot verify this attestation.
2. **Authoritative reconciliation:** a reconcile attempt proves `NotFound` with authoritative evidence, observed late
   enough after the earlier attempt's dispatch horizon (see below).

Otherwise the operation must be reconciled. An `advisory` `NotFound` never authorizes a repeat on its own: it ends in
`NeedsReview`.

### Reconciliation
```
"reconciliation": { "evidence": "advisory" | "authoritative", "matchKey": "operationId" | "businessKey",
                    "authority": { "maxCommitLatencySeconds": 10, "contractReference": "…", "maxEvidenceAgeSeconds": 60 } }
```
**`reconcileMatch`** reads at most `maxRows` rows (1–50) and compares the key exactly (optionally ignoring case).
- One match gives `Found`.
- Zero rows give `NotFound`, but only if the empty indicator is visible; otherwise the lookup is inconclusive and
  transient.
- Several matches, or more rows than `maxRows`, give `NeedsReview`.

**Match keys:**
- `operationId` is the strongest key: the target stores the id you sent.
- `businessKey` must be exactly one required, non-sensitive input that the target treats as unique (for example an
  e-mail address). This is another attestation.

**Evidence levels:**
- `advisory` evidence can confirm a `Found` but never authorizes a repeat.
- `authoritative` evidence requires an `authority` block citing the target's **commit-latency contract**: the maximum
  time between a request leaving the Worker and its result being visible in the lookup. A `NotFound` counts only if it
  was observed more than `maxCommitLatencySeconds` after the earlier attempt's dispatch horizon, and it expires after
  `maxEvidenceAgeSeconds` (ADR-057).
- If the target has no documented contract, use `advisory`.

## 8. The cost of safety
These are intended behaviours, not defects:
- Non-idempotent sites with advisory reconciliation send more records to `NeedsReview`. Phase 6 makes this visible.
  Add authoritative reconciliation where the target really supports it.
- Fencing has a cost. If a Worker froze or lost its network during a submit, the operation waits (`AwaitingFence`)
  until it is proven that the old attempt can no longer dispatch. Phase 5 turns a long wait into `NeedsReview`.
- Each attempt is a fresh browser session with its own sign-in.
- An authoritative `NotFound` must wait out the commit latency, so a repeat cannot start earlier than that.

## 9. Limitations (accepted, docs/PHASE4_PLAN.md "Remaining architectural limitations")
1. **Attestations are trusted, not proven.** These include `targetEnforced`, a business key's uniqueness, a
   lookup's consistency and a commit-latency contract. A wrong attestation can still cause duplicates. Hosted mode
   will restrict who may publish them.
2. **A frozen Worker can still dispatch once.** Its stale dispatch is then the operation's only one: nothing else is
   admitted while it is unfenced. Only Phase 7's out-of-process egress or container termination can prevent the
   dispatch itself.
3. **Bytes already sent cannot be recalled.** The dispatch horizon is a conservative bound after the write completes
   and the connection is destroyed. Transit and target processing are covered only by the trusted latency contract.
4. **Undetectable side effects** (section 7) and **secrets displayed by websites** (masked only via
   `maskSelectors`) depend on the author.
5. **Journal guarantees depend on the Phase 5 PostgreSQL implementation.** Phase 4 proves the contract with test
   implementations only. The production Worker fails closed without a durable, fencing journal.
6. **There is no universal secret detection.** Only declared secrets, sensitive inputs and sensitive extracts are
   redacted.

## 10. Limits
All limits live in `WorkflowLimits`, and the schema is parity-tested against them.

| Item | Limit |
|---|---|
| Definition size | 256 KiB (262,144 UTF-8 bytes) |
| JSON depth | 32 |
| String length | 2,048 |
| Steps | 200 in total, branches included |
| Branch nesting | 3 |
| Condition depth | 5 |
| Predicates per condition | 20 |
| Children per `all`/`any` | 10 |
| Inputs | 100 |
| Parameters | 20 |
| Secrets | 20 |
| `allowedHosts` | 20 |
| `maskSelectors` | 50 |
| `timeoutSeconds` | 1–3,600 (also capped by the server) |
| `timeoutMs` | 100–120,000 (side effects: at least 5,000) |
| `delay` | 1–30,000 ms |
| `extract.maxLength` | at most 10,000 |
| Result fields | 50 fields, 2,000 characters each, 32 KiB in total |
| `reconcileMatch.maxRows` | 1–50 |
| Validation errors per definition | at most 100 |

## 11. Publishing and versions
The canonical form (sorted keys, minimal escaping, integers only) is hashed with SHA-256. The hash is checked when a
version is published, when it is loaded, and at the start of every execution; a mismatch is `WorkflowTampered`.
Published versions are immutable (Draft → Published → Archived). Formatting and property order do not change the
hash. An explicit default value does: `"timeoutSeconds": 120` written out is a different definition from leaving it
out.
