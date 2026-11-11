# DemoCrm — fictional target application

DemoCrm is an ASP.NET Core Razor Pages portal that AutomataFlow will automate with Playwright (Phase 3 onward). It is a **separate application**: it references no AutomataFlow project or package, never calls the AutomataFlow API, and nothing in `src/` references it. All data is fictional (`@example.test` addresses, `555-01xx`-style numbers).

> DemoCrm is a demonstration target with known local credentials. Run it only on localhost or a private test network; never expose it publicly.

## Running it
```
.\scripts\run-demo-crm.ps1            # foreground, http://localhost:5080 (Ctrl+C to stop)
.\scripts\run-demo-crm.ps1 -Detach    # background; stop with: docker stop democrm-demo
```
The script runs DemoCrm in the official .NET 10 SDK Linux container (Smart App Control blocks local unsigned builds on the dev machine). The port is published on loopback only. Development environment settings apply, including the control token below.

## Credentials (local demonstration only)
| Email | Password |
|---|---|
| `agent@democrm.test` | `DemoPass-2026!` |

Configured in `src/DemoCrm/appsettings.json` under `DemoCrm:Users`. They are fictional and are not secrets; real customer credentials never belong in this application.

## Pages and stable selectors
All interactive elements carry `data-testid` hooks so Playwright selectors do not depend on layout.

| Page | URL | Notable test ids |
|---|---|---|
| Login | `/Login` | `login-email`, `login-password`, `login-submit`, `login-error` |
| Home | `/` | `customer-count`, `current-user` |
| Customer list | `/Customers?Q=&P=` | `filter-input`, `customer-row` (`data-customer-id`), `result-summary`, `pager-next` |
| Search | `/Customers/Search?Field=&Term=` | `search-field`, `search-term`, `search-submit`, `search-result-row`, `search-no-results` |
| Register | `/Customers/Register` | `input-first-name`, `input-last-name`, `input-email`, `input-phone`, `input-company`, `input-country`, `input-customer-type`, `input-operation-id`, `input-accept-terms`, `register-submit`, `error-<field>`, `form-error` |
| Confirmation | `/Customers/Confirmation/{id}?result=created\|replayed` | `confirmation-message` (`data-result`), `confirmation-customer-id` |
| Details | `/Customers/Details/{id}` | `detail-id`, `detail-email`, `detail-phone`, `detail-operation-id`, ... |
| Admin | `/Admin` | `reset-button`, `reset-message`, `scenario-table` |

Search fields: `Any`, `Email`, `Name`, `Phone`, `Id` (accepts `C-000012`, `c-12` or `12`).

## Registration rules (server-side only; the form is `novalidate`)
| Field | Rule |
|---|---|
| First / last name | required, letters, spaces, `'`, `-`, `.`; max 60 |
| Email | required, valid format, max 120; stored lower-case |
| Phone | required, digits with `+ ( ) - . space`, 7-15 digits |
| Company | optional, max 80 |
| Country | required, one of Australia, Canada, Germany, India, United Kingdom, United States |
| Customer type | required, `Standard` or `Premium` |
| Terms | checkbox must be ticked |
| Operation reference | optional GUID (see idempotency) |

Messages are stable strings (e.g. `Enter a valid email address.`), so workflows can assert on them.

## HTTP status semantics
| Status | Meaning |
|---|---|
| 302 -> `/Customers/Confirmation/{id}?result=created` | registered |
| 302 -> `...?result=replayed` | same operation already processed (idempotent replay) |
| 422 | validation failed; `form-error` has `data-error-kind="validation"` |
| 409 | duplicate email or phone (`data-error-kind="duplicate"`, `data-existing-customer-id`), or operation reference reused with different data (`operation-conflict`) |
| 503 + `Retry-After: 1` | simulated temporary failure (`data-error-kind="temporary"`) |
| 404 | unknown customer/page, friendly page |
| 400 | missing/invalid antiforgery token |

## Duplicate detection
- **Email**, case-insensitive. **Phone**, compared by digits only, so `+1 (555) 0100` equals `1-555-0100`.
- The error names the existing customer (`C-000001`) so reconciliation can use it.
- Checks and insert happen atomically under one lock, so concurrent submissions cannot create two customers (covered by a 50-way parallel test).

## Idempotency contract (ARCHITECTURE section 5.6, ADR-015)
Send an **operation reference** (GUID) with each registration: form field `Input.OperationId` or header `Idempotency-Key` (the field wins if both are present).
- Same reference + same email, already processed: no new customer; 302 to the confirmation with `result=replayed` and the original customer id.
- Same reference + different data: 409 `operation-conflict`.
- Same data + a different (or no) reference after a commit: 409 duplicate; this is the case a plain "retry" would hit, and why AutomataFlow persists a stable `OperationId` per record.
- A browser user gets a fresh reference prefilled in the form, so accidental double submits are idempotent.
- Lookup for reconciliation: search by email (`Field=Email`) or open the details page, which shows the stored operation reference.

## Predictable failure scenarios
Triggered by a plus-tag in the email local part (valid emails only; validation runs first and does not consume attempts). Attempts are counted per email address; reset clears the counters. Disable everything with `DemoCrm:Simulation:Enabled=false`.

| Email example | Behaviour |
|---|---|
| `name+fail-1@example.test` ... `+fail-5` | first N submissions return 503 **before** anything is stored; the next succeeds |
| `name+commit-fail@example.test` | first submission is **stored**, then 503 is returned (lost response); later submissions behave normally (replay with the same operation reference, duplicate error without) |
| `name+slow@example.test` | succeeds after `Simulation:SlowDelayMs` (default 3000 ms) |

## Resetting the demo data
- **UI:** `/Admin` -> "Reset demo data" (signed-in, antiforgery-protected POST). Shows a confirmation message.
- **Machine:** `POST /demo/reset` with header `X-Demo-Control-Token`; `GET /demo/state` returns `{customerCount, seededCount, simulatedAddresses}`. Both return 404 unless `DemoCrm:ControlApi:Token` is configured (only `appsettings.Development.json` sets one: `local-demo-control-token`) and 401 for a wrong token (constant-time comparison). State exposes counts only, never customer data.
- Reset restores the 24 seed customers (ids `C-000001`..`C-000024`) and clears simulation counters. Restarting the process has the same effect, since the store is in memory.

## Storage
Thread-safe in-memory store behind `ICustomerStore` (ADR-024). No database, no native dependencies, byte-identical seed on every start.

## Security notes
Cookie authentication (HttpOnly, SameSite=Strict), constant-time credential comparison, generic login errors, antiforgery on every POST, local-only return URLs, HTML encoding of all output, request body limit 64 KB, `X-Content-Type-Options`/`X-Frame-Options`/`Referrer-Policy` headers, host filtering (`localhost`, `127.0.0.1`, `[::1]`), credentials never logged.
