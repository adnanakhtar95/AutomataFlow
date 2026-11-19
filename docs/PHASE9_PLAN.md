# Phase 9 — Commercial automation capabilities

**Status:** authorized 2026-11-20 (Phases 6–10 authorization). Starts when Phase 8 meets its acceptance criteria.

## Objective
The engine serves several genuinely different business workflows without engine changes per customer, and the product
ships reusable, verified workflow packages.

## Approach
1. Inventory the 13 implemented actions against three concrete business scenarios; add a capability only when a
   scenario cannot be expressed or would be unsafe without it. Every addition stays declarative (no scripting, no
   regular expressions, ADR rules on the expression language).
2. Candidate additions, each justified by a scenario before it is built: workflow templates (import a package into a
   workspace), API-triggered jobs with service credentials, job-completion webhooks (allow-listed, signed, SSRF-checked),
   per-workspace execution quotas, usage reports.
3. Three workflow packages, each with a definition, documented inputs, credentials and allowed hosts, a controlled test
   target (DemoCrm extensions or new fictional fixtures), verified execution, failure and recovery behaviour, and known
   limitations:
   - CRM customer registration and updates;
   - e-commerce order processing;
   - invoice / administrative record processing.
   No healthcare workflow is shipped; no compliance is claimed.

## Acceptance criteria
Three packages run end to end against controlled targets through the release stack, including a failure-and-recovery
scenario each, with no engine change specific to one package.
