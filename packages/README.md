# Workflow packages

Ready-made, verified workflows (Phase 9). Each folder contains:

| File | Purpose |
|---|---|
| `manifest.json` | id, title, category, summary and setup notes shown in the dashboard's template picker |
| `workflow.json` | the workflow definition (schemaVersion 1); it must pass the Api's validation |
| `sample.csv` | a record file with the expected columns, including rows that exercise failures |

The Api embeds these files at build time and serves them as templates (`GET /api/v1/workflow-templates`). Copying a
template into a workspace creates a DRAFT workflow; the customer sets the parameters and the secret, reviews and
publishes it. Templates contain no customer data and no secrets.

| Package | Scenario | Side effect and duplicate safety | Verified (AutomationTests, real Worker + Chromium vs DemoCrm) |
|---|---|---|---|
| `crm-customer-registration` | register customers in a CRM | submit carries the operation id; target refuses duplicates; reconcile by operation id; duplicate e-mail/phone reported | demo CSV end to end (Phase 5), dashboard journey (Phase 6), process-kill recovery (Phase 5) |
| `ecommerce-order-entry` | enter orders into an order desk | order reference = operation id (target-enforced); reconcile by order reference | `WorkflowPackageTests`: 5 orders created exactly once incl. fail-twice and commit-then-fail rows; out-of-stock and unknown-customer refused |
| `invoice-approval` | approve supplier invoices after an amount check | approval records the operation id; approving twice is impossible at the target; reconcile by that id | `WorkflowPackageTests`: 5 approvals exactly once incl. fail-before-commit and commit-then-fail; already approved, amount mismatch and not found reported |

## Adapting a package to a real system
The packages target the fictional DemoCrm portal (a test fixture). For a real system, keep the structure and replace
the selectors (`testId`, `css`, `role`, `label`, ...), URLs and result fields. The duplicate-safety pattern only holds if
the real system really refuses a second submission with the same reference (the `targetEnforced` attestation that must
be acknowledged at publication) and offers a lookup by that reference for reconciliation. If it does not, remove the
`idempotency` block: failed attempts after the side effect started then go to Needs review instead of being retried.

## Limitations
- Amounts are compared as text: the expected amount must use the target's format (two decimals, dot separator).
- No package reads files from e-mail, shared drives or APIs: records come from an uploaded CSV/JSON file.
- No healthcare, payments or personal-data-heavy package is shipped; no compliance is claimed.
