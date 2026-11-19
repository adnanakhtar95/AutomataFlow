# AutomataFlow — Security overview

This document describes the security controls of AutomataFlow as built and tested, and the limitations that remain.
It is written for customers evaluating the product and for the operators who deploy it. Design details and decisions
are in docs/ARCHITECTURE.md (§7.1, §9) and the ADRs in docs/DECISIONS.md. No compliance certification is claimed.

## 1. What the platform does with customer data
- Workflows are **declarative, validated, versioned JSON**. There is no scripting: no customer-supplied C# or JavaScript
  is ever executed, conditions are a closed operator set without regular expressions, selectors are typed data.
- Record files (CSV/JSON) are parsed on upload, validated per row and stored as records. Inputs a workflow marks as
  **sensitive** are stored encrypted. Upload limits (size, records, columns, cell length, file type) are enforced by the Api.
- Results, logs and screenshots are produced by the Worker. Logs are redacted (secrets and sensitive values are masked)
  and never contain record inputs; results exports contain row numbers, statuses, error codes and declared result
  fields only, with spreadsheet-formula neutralization.

## 2. Tenant isolation (defense in depth)
1. Every request is authenticated (JWT, 15 minutes) and the caller's **membership and role are read from the database
   on every request**; a removed member loses access immediately.
2. Every use case takes the workspace from the verified caller and puts it in every query; another workspace's ids are
   indistinguishable from missing ones (404).
3. EF Core global query filters are a backstop, and a write guard refuses any write to another workspace's rows.
4. Foreign keys between customer tables are composite (workspace + id), so the database itself refuses cross-workspace
   references.
5. Artifacts are stored under workspace-specific keys and served only through an authorized endpoint.
6. Every customer-facing endpoint has a cross-tenant test, run twice: with the query-filter backstop and **without** it,
   to prove the application layer isolates on its own. The browser journey also checks that another workspace sees
   "not found".

## 3. Accounts and sessions
- Passwords: ASP.NET Core Identity PBKDF2 hashing, minimum 12 characters, lockout after 5 failures for 15 minutes,
  uniform error messages (no account enumeration), rate limiting on authentication endpoints per client address.
- Refresh tokens: 256 random bits, stored only as SHA-256 hashes, rotated on every use; reuse of a rotated token
  revokes the whole token family.
- Browser sessions (ADR-077): the refresh token lives only in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie scoped
  to the authentication endpoints and honoured only with a custom header (CSRF guard). The access token is kept in
  memory only. Nothing is written to browser storage.
- Roles: Viewer < Operator < Admin < Owner per workspace. Publishing workflows and requeueing records need Admin;
  platform operations (fence confirmation) need the separate platform-operator role, never a workspace role.

## 4. Browser automation isolation
- One fresh browser context per record attempt; no shared cookies or storage; contexts disposed after the attempt.
- Chromium runs with its **sandbox enabled** under a dedicated seccomp profile, as a non-root user with **no Linux
  capabilities**, inside nested user and PID namespaces supervised by a separate process (ADR-075).
- **Egress control:** all browser traffic goes through an in-process egress proxy that resolves and checks every
  destination (navigations, redirects, subresources, frames, popups, WebSockets) against the workflow's allowed hosts
  and an IP policy that blocks loopback, private, link-local (including cloud metadata), CGNAT and other special ranges.
  The engine fails closed: no browser starts without the per-session proxy credentials.
- Service workers, downloads outside the per-attempt directory and non-HTTP schemes are blocked.

## 5. Duplicate side effects
Retries never repeat a side effect blindly. Every record has a stable operation id; a durable attempt journal in
PostgreSQL records each side effect before it starts; an attempt whose outcome is uncertain is reconciled against the
target system, and a record that cannot be proven safe becomes **Needs review** instead of being repeated. A crashed
Worker is fenced (proof that its processes and connections are gone) before its work is resumed (ADR-071, ADR-075).

## 6. Secrets and keys
- Workspace secrets (e.g. a target system password) are write-only through the Api and stored as ASP.NET Core Data
  Protection payloads bound to the workspace; the Worker decrypts them only inside the attempt that uses them.
- The Data Protection key ring is **encrypted at rest** with an operator-supplied certificate (ADR-079).
- Configuration secrets come only from the environment / Compose secrets; nothing secret is committed.

## 7. Deployment hardening (ADR-078)
- Release images: chiseled, non-root Api with a read-only root filesystem and no capabilities; unprivileged nginx
  gateway with a strict Content-Security-Policy, `frame-ancestors 'none'`, `nosniff`, `no-referrer`; Worker with the
  exact ADR-075 posture. All base images pinned by digest.
- Database: the application runs as a data-only role that cannot change the schema; migrations run separately as the
  owner role; the database is on an internal network and never published.
- `scripts/validate-deployment.ps1` refuses unsafe configurations (posture drift, development settings, weak or
  placeholder secrets, exposed data) before deployment.
- TLS: terminated in front of the gateway (bundled Caddy profile with automatic certificates, or the operator's own).

## 8. Known limitations
| Limitation | Impact | Mitigation / plan |
|---|---|---|
| The Worker and its supervisor share the database role; the Worker could confirm a fence itself | a compromised Worker process could mark its own attempts fenced | the Worker is trusted platform code; untrusted content is confined to the sandboxed browser. Separate fence role: hosted-mode hardening |
| Out-of-process egress proxy with lease binding (ADR-056) and per-tenant browser isolation (ADR-031) are not delivered | in **Hosted** mode with mutually untrusted tenants, browsers of different tenants share a Worker process | deploy **SelfHosted** (one organization or trusted customers per deployment) until hosted isolation is delivered |
| No Windows supervisor | the Worker runs only on Linux containers | supported deployment is Linux / Docker |
| P12 (below-socket queueing) not verifiable in the test environment | fence proof relies on socket drain without that scenario tested | documented in docs/PHASE5_PLAN.md |
| No e-mail delivery | invitations and password resets are not sent by e-mail | invitation links are handed over by the inviting admin (Phase 8) |
| No external identity provider / MFA | password authentication only | planned with SSO (post-release) |

## 9. Reporting a vulnerability
Contact the vendor's security contact listed in the support agreement. Do not test against deployments you do not
operate.
