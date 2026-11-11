# Phase 3 — Playwright Browser Engine: Technical Implementation Plan

Status: **Approved 2026-11-11 (ADR-029)**, with additional mandatory requirements A–E (ADR-030 to ADR-032). Spike results: docs/PHASE3_SPIKE.md.
Scope: a reusable, secure browser execution engine (lifecycle, isolated sessions, low-level page operations, artifacts, logging, SSRF/egress control) plus real-browser tests against DemoCrm. The workflow engine and action registry (Phase 4) and Worker job orchestration (Phase 5) are out of scope; they will consume this engine.

Items marked **[spike]** are behaviours I am confident about but will prove with a test in the first half-day spike before building on them. If a spike disproves an assumption, I stop and report before changing the design.

---

## 1. Playwright .NET integration
- Package: `Microsoft.Playwright` **1.64.1** (Apache-2.0), referenced **only** by `AutomataFlow.Automation`. Api never references it (existing architecture test plus a new package-level check).
- Playwright types never leave `AutomataFlow.Automation.Browser`. The rest of the system (Phase 4 action handlers, Worker) sees AutomataFlow abstractions (`IBrowserSession`, `ElementSelector`, ...), so a Playwright upgrade or replacement stays local.
- `AutomataFlow.Automation.Network` (SSRF policy and egress proxy) has **no Playwright dependency**, which keeps it unit-testable and reusable by a future standalone proxy host. Enforced by a reflection test.
- Application gains small, package-free **ports** that Phase 5 implements in Infrastructure (Infrastructure cannot reference Automation): `IExecutionLogSink`, `ExecutionLogEntry`, `ExecutionScope`, `IArtifactStore`, `ArtifactDescriptor`, `ArtifactKind`.

## 2. Chromium installation and browser lifecycle
**Installation (pinned, never at runtime in production):**
- Browser binaries come from the driver bundled in the pinned NuGet package (`.playwright/package/cli.js install --with-deps chromium`, run with the bundled node). No `pwsh` needed, since this machine has only PowerShell 5.1. **[spike]**
- **Verification and CI:** a dedicated image `deploy/verify/Dockerfile` (based on `mcr.microsoft.com/dotnet/sdk:10.0`, Playwright-supported distro) bakes Chromium plus OS dependencies in at build time. It is tagged with the Playwright version (`automataflow-verify:pw-1.64.1`) and cached by Docker.
- **Production Worker image (Phase 7):** same install step at image build time. At runtime `BrowserInstallation.EnsureAvailable()` checks that the executable exists and fails fast with an actionable message. Hosted mode never auto-downloads, for supply-chain control and reproducibility.
- **Windows host:** Chromium is **not** installed or run on the host (see section 14).
- **Version lock:** a test asserts that the Playwright package version equals the version the verify image was built for (mismatches cause "executable doesn't exist" failures).

**Lifecycle (`BrowserHost`, singleton, `IAsyncDisposable`):**
- **Startup:** `IPlaywright` is created lazily once per process. Chromium is launched with hardened options (section 3), routed through the egress proxy (section 10).
- **Health:** subscribes to `Browser.Disconnected`. A crashed or disconnected browser is marked faulted. In-flight sessions fail with `BrowserCrashed` (transient), and the next session triggers a relaunch.
- **Recycling:** in shared mode the browser is relaunched after `RecycleAfterSessions` (default 50) or `RecycleAfterMinutes` (default 30), draining active sessions first. This bounds memory growth and limits exposure from a long-lived process.
- **Shutdown:** stops issuing sessions, waits up to `ShutdownGrace` for active sessions, closes contexts, closes the browser with a timeout, then disposes Playwright, which ends the driver and browser processes. A watchdog escalates when a close hangs. **[spike]** I will verify the escalation path that guarantees no orphaned `chrome`/`node` processes (process-count assertion in tests).
- **Worker integration (Phase 5):** a thin hosted service in the Worker starts and stops `BrowserHost`. The engine itself does not depend on the Worker.

## 3. Browser factory and configuration
- `BrowserEngineOptions`, bound from `Automation:Browser`, validated at startup by the hand-written `BrowserEngineOptionsValidator` (`IValidateOptions` + `ValidateOnStart`):
  - `Mode`: `Development` | `SelfHosted` | `HostedMultiTenant`.
  - `Isolation`: `SharedBrowser` | `BrowserPerSession` (section 11).
  - `MaxConcurrentSessions`, `Headless` (always true outside Development), `ChromiumSandbox` (default true).
  - Timeouts (section 7), `RecycleAfterSessions`/`Minutes`, `ScreenshotPolicy`, `MaxScreenshotBytes`, `Downloads` limits, `MaxPagesPerSession`, `TracingEnabled` (default false), `Viewport`, `Locale`, `TimeZone`.
- **Validation rules that matter for security:**
  - `HostedMultiTenant` requires `BrowserPerSession`, `ChromiumSandbox=true`, https-only targets, and no loopback/private exceptions.
  - The startup validator rejects any configuration that disables the egress proxy; there is no option to do so.
- `ChromiumLaunchOptionsBuilder` (pure, unit-tested) produces the launch arguments:
  - Proxy to the egress proxy, plus `--proxy-bypass-list=<-loopback>`, so Chromium also proxies localhost and 127.0.0.1, which it otherwise bypasses. **[spike]**
  - `--host-resolver-rules="MAP * ~NOTFOUND"`, so Chromium cannot resolve names itself (the proxy address is an IP literal). **[spike]**
  - `--disable-quic`, `--force-webrtc-ip-handling-policy=disable_non_proxied_udp`.
  - `--disable-background-networking`, `--disable-component-update`, `--disable-sync`, `--disable-extensions`, `--no-first-run`, `--disable-features=...` (DNS-over-HTTPS, network prediction, translate). **[spike]**
  - Sandbox kept enabled.
- `IBrowserSessionFactory.CreateAsync(BrowserSessionRequest, CancellationToken)` is the only way to get a browser. A request contains:
  - `ExecutionScope` (org/job/record/attempt ids).
  - `EgressPolicy` (allowed host rules from the workflow).
  - Optional bounded overrides (timeouts, viewport, downloads on/off).

## 4. Browser context isolation and cleanup
- **One fresh `IBrowserContext` per session (= per record attempt).** No persistent profile, no `storageState` reuse, no shared cookies, cache or localStorage between sessions.
- **Context options:**
  - `ServiceWorkers = Block`, so service workers cannot bypass routing or persist.
  - `AcceptDownloads` only when requested.
  - `IgnoreHTTPSErrors = false`, `BypassCSP = false`, no granted permissions.
  - `JavaScriptEnabled = true`: target sites need their own scripts, but *we* never inject customer script (section 9).
  - Per-session proxy credentials (section 10).
- **Guards attached before the first navigation:**
  - Route guard: `RouteAsync("**/*")` and `RouteWebSocketAsync`.
  - Popup cap.
  - Dialog auto-dismiss, which is logged; this prevents hangs.
  - Request/response recorder and the crash handler.
- **Cleanup in `DisposeAsync`, always run in `finally` and idempotent:**
  1. Close the context (with a timeout).
  2. Revoke the proxy credentials, so late connections get 407.
  3. Delete the per-session temp directory (downloads).
  4. Release the concurrency slot.
  5. In `BrowserPerSession` mode, close the browser.
  6. Escalate to browser recycle if close hangs or the session faulted.
- **Honest limit:** contexts isolate *state*. They are **not a security boundary** against a malicious page exploiting Chromium, because all contexts of a shared browser live in the same browser process. Real isolation layers are listed in section 11.

## 5. Navigation, click, fill, select and wait
These are **engine operations**, not workflow actions. Phase 4 action handlers wrap them.
- `NavigateAsync(Uri, ct)`:
  - Pre-check with `RequestGuard` (scheme, host, port, userinfo).
  - `GotoAsync` with `WaitUntil=Load` and the navigation timeout.
  - Returns status, final URL and the redirect chain.
  - Fails with `NavigationBlocked` if any hop was blocked, and with `HttpError` (status) when configured.
- `ClickAsync`, `FillAsync`, `SelectAsync`, `WaitForAsync` use **Playwright locators with auto-wait**. There are no fixed sleeps.
- `ElementSelector` is typed data, not code: `TestId`, `Css`, `Role(role, name)`, `Text`, `Label`, `Placeholder`, `XPath`.
  - It maps to `GetByTestId`, `Locator`, `GetByRole`, and so on.
  - Length is capped, and `Css`/`XPath` strings are rejected if they contain Playwright engine prefixes we do not support. Custom selector engines are never registered.
  - Strict mode is on, so an ambiguous selector fails instead of clicking the wrong element.
- `InputValue` is `Plain(string)` or `Sensitive(string)`. Sensitive values are never logged (only their length) and the filled element is added to the screenshot mask set.
- `SelectOption` is by value, label or index. `WaitCondition` covers element visible, hidden, attached, detached; URL matches; load state; and response status for a URL pattern.
- `GetTextAsync` / `GetAttributeAsync` exist for tests and the Phase 4 Extract action, read through the locator API, never via `EvaluateAsync`.

## 6. Screenshots and downloads
**Screenshots:**
- **Policy:** `ScreenshotPolicy` = `Never` | `OnFailure` (default) | `OnRequest`. PNG, viewport by default and full page on request. Size is capped, and over-cap images are discarded and logged.
- **Masking:** Playwright's `Mask` option covers every element we filled with a `Sensitive` value, plus `input[type=password]`, `[autocomplete^="cc-"]`, `[data-af-sensitive]`, and workflow-declared mask selectors.
  - **Honest limit:** secrets the *site itself* renders as plain text are not auto-detected. Workflows declare such areas. The docs and Phase 4 validator will flag this.
- **Storage and tracing:** stored through `IArtifactStore` with metadata (kind, size, SHA-256, step, session). Tracing is **off by default**, because traces contain DOM snapshots and typed values. If it's enabled for debugging, the trace is classified as a sensitive artifact.

**Downloads:**
- Enabled per session only when requested. `DownloadAsync(trigger)` clicks and awaits the download, with a timeout.
- The file is saved to a per-session temp directory under a **generated name**. The original filename is kept only as sanitized metadata (`FileNameSanitizer`: no path segments, control characters or reserved names; length capped).
- Size limit, extension deny-list (executables, scripts) and allow-list option. SHA-256 is recorded, the file goes to `IArtifactStore`, and it is **never opened or executed**.
- **Byte quota:** Playwright cannot cap a download in flight, so the egress proxy's per-session byte quota (section 10) is the hard bound. Oversized files are rejected after save and deleted.
- Uploads (`SetInputFiles`) arrive with the Phase 4 Upload action. Paths will be restricted to the artifact store; that is noted here, not built.

## 7. Timeouts and cancellation
- **Layered defaults** (all configurable within validator bounds):
  - Launch 30 s, navigation 30 s, action 10 s, download 60 s, context close 10 s, whole session 5 min.
  - `Page.SetDefaultTimeout` and `SetDefaultNavigationTimeout` are set from these.
- **`OperationRunner`** wraps every operation:
  - Linked `CancellationTokenSource(callerToken, sessionDeadline, operationTimeout)`.
  - Playwright .NET calls take no `CancellationToken`, so on cancellation the runner **closes the page/context**, which aborts the in-flight call. It then throws `OperationCanceledException` (caller cancellation) or `BrowserOperationException(Timeout)`. Partially completed waits never leak. **[spike]** I will verify the abort-by-close semantics.
- **Error model:** `BrowserOperationException` with a `BrowserErrorCode` and `IsTransient`, which feeds Phase 5 retries.
  - Codes: `Timeout`, `NavigationBlocked`, `ElementNotFound`, `ElementNotUnique`, `HttpError`, `NetworkError`, `BrowserCrashed`, `DownloadRejected`, `PolicyViolation`, `Cancelled`.
  - `ErrorClassifier` maps Playwright exceptions to codes; it is unit-tested with recorded messages.
- Worker shutdown (host token) cancels sessions cooperatively. Phase 5 then releases records back to Pending.

## 8. Structured execution logging
- Every session runs inside `ILogger.BeginScope(ExecutionScope)` (OrganizationId, JobId, JobRecordId, AttemptNumber, SessionId) and writes `ExecutionLogEntry` events to `IExecutionLogSink`. Phase 5 persists them to `ExecutionLogs`; Phase 3 tests use an in-memory sink.
- **Event types:**
  - Session: `SessionStarted`, `SessionEnded` (with duration and outcome).
  - Navigation: `NavigationStarted`, `NavigationCompleted` (status, final URL, redirect count).
  - Actions: `OperationStarted`, `OperationCompleted`, `OperationFailed` (selector, duration, error code).
  - Security: `RequestBlocked` (layer, host, port, resolved IP, reason).
  - Artifacts and page events: `DownloadSaved`, `DownloadRejected`, `ScreenshotSaved`, `DialogDismissed`, `PopupBlocked`, `BrowserCrashed`, `BrowserRecycled`.
- **Redaction (`LogRedactor`):**
  - URLs are logged without userinfo and with **query string values redacted by default**, because tokens often travel in queries.
  - `Sensitive` input values are never logged.
  - Exception messages from Playwright, which can echo `fill` values, are passed through the redactor.
  - Request/response bodies and headers (cookies, authorization) are never logged.
- Message templates only (structured), consistent with Serilog in Api and Worker.

## 9. SSRF protection for all browser network traffic
**Principle: never rely on the initial `Navigate` URL check.** Three independent layers; the network layer is authoritative.

| Layer | Sees | Enforces |
|---|---|---|
| 1. `RequestGuard` (pre-navigate + context route on `**/*` + `RouteWebSocketAsync`) | full URL of every request the route intercepts: documents, subresources, XHR/fetch, frames, popups, WebSockets | scheme allow-list (`https`, `http` only where the rule allows it); host + port must match an `AllowedHostRule`; no userinfo; IP-literal hosts canonicalized and checked against `IpAddressPolicy`; blocks `file:`, `ftp:`, `chrome:`, `view-source:`, `javascript:` navigations, `data:` documents; fast, clear `RequestBlocked` events |
| 2. **Egress proxy** (all Chromium traffic is forced through it, section 10) | every TCP connection: `CONNECT host:port` for https/wss, absolute-form requests for http, so **every redirect hop to a new host, every frame, every subresource connection** | host/port allow-list per session; **DNS resolution done by the proxy**; every resolved address checked; connection made to the **validated IP (pinned)**; private, loopback, link-local and metadata ranges denied |
| 3. Network isolation (hosted mode, section 10 and 11) | packets | the browser's network namespace can reach **only** the proxy; everything else is dropped by Docker networking |

Why layer 2 is mandatory, even in Development:
- **Redirects:** Playwright's route handler is invoked for the original request but not reliably for each redirect hop. The proxy sees every new connection, so a redirect to `169.254.169.254` is stopped there. The recorder also inspects `Request.RedirectedFrom` chains and fails the session on any violation. **[spike: exact redirect/route interaction]**
- **DNS rebinding:** a check-then-connect in the browser is racy, because Chromium resolves names independently. In this design **Chromium never resolves target names**: `--host-resolver-rules` makes direct resolution fail, and with an HTTP proxy Chromium sends the hostname to the proxy. The proxy resolves **once per connection**, rejects the host if **any** returned address is disallowed (defeats mixed public/private record sets), and connects to that exact address. A later re-resolution to a private IP is never used.
- **Channels outside HTTP:**
  - WebRTC UDP is disabled by flag and, in hosted mode, unreachable by network policy.
  - QUIC is disabled.
  - Service workers are blocked.
  - Prefetch/preconnect go through the proxy anyway and are additionally disabled by flags.

**`IpAddressPolicy` deny list:**
- **IPv4:** 0.0.0.0/8, 10.0.0.0/8, 100.64.0.0/10, 127.0.0.0/8, 169.254.0.0/16, 172.16.0.0/12, 192.0.0.0/24, 192.0.2.0/24, 192.88.99.0/24, 192.168.0.0/16, 198.18.0.0/15, 198.51.100.0/24, 203.0.113.0/24, 224.0.0.0/4, 240.0.0.0/4, 255.255.255.255.
- **IPv6:** ::/128, ::1/128, fe80::/10, fc00::/7, ff00::/8, 2001:db8::/32, 100::/64.
- **Embedded IPv4** is unwrapped and re-checked for ::ffff:0:0/96 (mapped), 64:ff9b::/96 (NAT64), 2002::/16 (6to4) and 2001::/32 (Teredo).
- **Metadata hostnames** (`metadata.google.internal`, `metadata`, `instance-data`, and so on) are denied by name as defence in depth. Their IPs (169.254.169.254, fd00:ec2::254) are already denied by range. 168.63.129.16 (Azure platform) is denied explicitly.

**Host normalization (`HostNameNormalizer`):**
- Lower-casing, trailing-dot removal, IDN to punycode.
- Rejection of non-canonical IPv4 forms (`2130706433`, `0x7f.1`, `0177.0.0.1`), IPv6 zone IDs and backslash/userinfo tricks.
- Wildcard rules (`*.example.com`) match subdomains only, never `example.com.evil.test`.

**Development/test exceptions:** loopback targets such as DemoCrm are allowed only as explicit `host:port` exceptions, only in `Development`/test mode, and the validator rejects them in `HostedMultiTenant`. Every other loopback port (DemoCrm's own control API on another port, Postgres on 5433, the proxy itself) stays blocked, and tests prove it.

**No customer JavaScript:**
- The engine has no operation that evaluates caller-supplied script. Selectors are typed data.
- `EvaluateAsync`, `AddInitScriptAsync`, `ExposeFunctionAsync` and similar are banned in `AutomataFlow.Automation` except in one reviewed internal file, if one is ever needed. A source-scan test enforces this.

## 10. Egress proxy architecture
**Component:** `EgressProxyServer` in `AutomataFlow.Automation.Network`. It is a small forward proxy on `System.Net.Sockets` with no third-party package.

**Protocols:**
- `CONNECT host:port`, used for https and wss. The tunnel is opaque after the policy check; we never terminate TLS.
- Absolute-form `http://` requests, allowed only for rules that permit http (DemoCrm in dev).
- Plain HTTP is handled defensively:
  - **One request per connection** (`Connection: close`).
  - Strict request-line and header parser, with a 32 KB header limit.
  - Requests are rejected on `Host` / absolute-URI mismatch and on conflicting `Content-Length`/`Transfer-Encoding`, which prevents request smuggling.
- Anything else gets 405.

**Per-session authentication and policy:**
- Every browser session gets random proxy credentials (128-bit username and password), passed through Playwright's per-context proxy option. **[spike: Chromium per-context proxy requires a launch-level proxy placeholder]**
- `EgressSessionRegistry` maps credentials to that session's `EgressPolicy`. Unknown or revoked credentials get 407. The proxy therefore applies *each session's own* allowed hosts.
- Other local processes cannot use the proxy as an open relay.

**Decision per connection:**
1. Authenticate the session.
2. Normalize the host.
3. Check the host:port rule.
4. Resolve through `IDnsResolver` (system resolver in production, a fake in tests), with a timeout.
5. Reject if any address is denied.
6. Connect to the first allowed address (pinned).
7. Log a `SecurityEvent`.

**Limits** (resource protection and an exfiltration bound): connect timeout, idle timeout, maximum tunnel lifetime, maximum concurrent tunnels per session, and maximum bytes per session (upload + download).

**Deployment modes:**
- **Development / SelfHosted (MVP):** in-process with the Worker, listening on `127.0.0.1:<ephemeral>`.
  - This gives policy, IP pinning, DNS control and auditing for all browser traffic.
  - **It is not a hard network boundary:** a fully compromised browser process in the same network namespace could open sockets directly. That is acceptable for trusted, single-tenant client projects and is documented as such.
- **HostedMultiTenant (implemented in Phase 7, designed now):** the same code in its own container (thin host project added in Phase 7, an ADR at that time).
  - The browser runs in a container attached only to a Docker network with `internal: true`, whose only reachable peer is the proxy.
  - The proxy container sits on that internal network and the egress network, and has its own firewall rules dropping RFC 1918, link-local and metadata destinations as a second net.
  - The database is **not** on the browser network.
  - Session policies reach the sidecar through an authenticated control channel (designed in Phase 7).

```
 [Chromium]  --(only route)-->  [Egress proxy]  --resolve+check+pin-->  allowed public host
  internal-only network            policy, quotas, audit           X  10/8, 172.16/12, 192.168/16, 127/8,
  (hosted mode)                                                       169.254/16 (metadata), fc00::/7, ...
```

## 11. Browser process isolation and resource limits
**Isolation modes:**
- `SharedBrowser`: one Chromium per Worker, a new context per session. It's efficient, and it's the default for Development and SelfHosted (a single trusted customer).
- `BrowserPerSession`: a dedicated Chromium process per session, so nothing is shared across records or tenants. It costs roughly 0.3–1 s of launch per session **[spike: measure]**. It is **required** for HostedMultiTenant.
- Future: remote browsers per tenant through Playwright `ConnectAsync`, i.e. a browser server inside a per-tenant sandbox container. Designed for now, not built.

**Layers of real isolation (hosted target state):**
- Chromium sandbox **on**.
- Non-root user.
- Container with a seccomp profile compatible with the Chromium sandbox.
- Read-only root filesystem except temp and download directories.
- No secrets in the browser container's environment.
- Network namespace limited to the proxy.
- Short-lived browser processes, and a Playwright/Chromium patch cadence (version bump ADR per release).

**Resource limits:**
- **Engine:** `MaxConcurrentSessions` semaphore; `MaxPagesPerSession` (default 5; extra popups are closed and logged); session deadline; download count and size limits; screenshot size cap; dialog auto-dismiss; browser recycling.
- **Proxy:** byte, connection and duration quotas.
- **Container (Phase 7 and verification):**
  - CPU, memory, `pids` and `/dev/shm` limits (`--shm-size=1g`; no `--disable-dev-shm-usage` unless needed).
  - `--init`, so zombie processes are reaped.
  - Memory exhaustion by a page is contained by the container memory limit. Chromium crash leads to a `BrowserCrashed` (transient) error and a relaunch.

## 12. Unit and integration test strategy
**Unit tests (no browser, in `AutomataFlow.UnitTests`), table-driven:**
- `IpAddressPolicy`: every range boundary, IPv4-mapped, NAT64, 6to4, Teredo, zone IDs.
- `HostNameNormalizer`: decimal, octal and hex IPv4; trailing dots; IDN; uppercase; backslashes; userinfo.
- `RequestGuard` (schemes, ports, wildcard rules), `EgressPolicy`, and the proxy request parser (malformed lines, oversized headers, CL/TE conflicts, absolute/Host mismatch).
- `LogRedactor`, `FileNameSanitizer`, `ElementSelector` validation, `ErrorClassifier`, `OperationRunner` (timeout vs cancellation semantics with fakes).
- `BrowserEngineOptionsValidator` (hosted-mode rules) and `ChromiumLaunchOptionsBuilder` (required flags present, proxy never absent).
- Source-scan test: no script-evaluation APIs.
- Reflection test: Network namespace has no Playwright types.

**Proxy integration tests (real sockets, no browser, in `AutomationTests`):**
- Upstream echo servers on loopback and a fake DNS resolver. The fake can return private, mixed and rebinding sequences (public first, then private).
- Cases: CONNECT allowed/denied, port denial, 407 for unknown or revoked credentials, pinning (only the first resolved IP is ever dialled), quotas, idle and lifetime timeouts, smuggling attempts.

**Real-browser tests (`AutomationTests`, Chromium, container only):**
- **Lifecycle:** launch, crash detection, recycle, shutdown with no leftover processes.
- **Context isolation:** cookies, localStorage, cache and service workers do not cross sessions.
- **Operations, screenshots, downloads, timeouts and cancellation:**
  - A hung page is aborted within the deadline.
  - Caller cancellation is honoured.
  - The popup cap holds.
  - Dialog dismissal prevents hangs.

**Hostile SSRF matrix** (a local Kestrel "hostile fixture" server plus the fake DNS; every case must be blocked and produce a `RequestBlocked` event):
- **Redirects:** 302 to a disallowed host; a redirect chain ending on 127.0.0.1, 169.254.169.254 or `[::ffff:127.0.0.1]`.
- **Embedded and scripted requests:** iframe to a disallowed host; image, script or CSS from a disallowed host; `fetch`/XHR from page script; `window.open` and `target=_blank` popups to a disallowed host; WebSocket to a disallowed host.
- **URL tricks:** `file:///etc/passwd`, `data:` document, `javascript:`; decimal, octal and hex IP literals; uppercase or trailing-dot hosts.
- **DNS tricks:** a name resolving to a private IP, a mixed record set, a rebinding sequence.
- **Other loopback ports:** DemoCrm's other ports, Postgres 5433 and the proxy's own port.
- **Positive controls:** the allowed fixture host and DemoCrm still work.

**Credential protection tests:**
- A `Sensitive` fill value never appears in any log entry or exception.
- A screenshot taken after filling a password field has the masked region painted with the mask colour (pixel check).
- Query-string tokens are redacted in logged URLs.

## 13. Real browser tests against DemoCrm
- `AutomationTests` gains the approved `ProjectReference` to DemoCrm (ADR-027) **only** to build it.
- Tests **launch DemoCrm as a real child process** (`dotnet DemoCrm.dll`, Kestrel on `127.0.0.1:<free port>`, `DemoCrm__ControlApi__Token` = random). `WebApplicationFactory`'s in-memory TestServer is not reachable by a real browser.
- A fixture waits for readiness, resets data through the control API between tests, and kills the process tree on dispose.
- **Scenarios** (driven only through the engine's operations and selectors, exactly as Phase 4 will):
  - Login, then registration, then the confirmation id is captured.
  - Validation errors are visible (422 banner and field messages).
  - Duplicate (409 banner with the existing id).
  - `+fail-2` shows two temporary-error banners before success.
  - `+commit-fail` with the same operation reference ends in "already processed" with a single customer (control API count).
  - Search, then the details page.
  - Screenshot on a deliberately failing selector.
  - Logout.
- DemoCrm runs under the egress policy with its port as the only loopback exception, which proves the exception mechanism is port-precise.

## 14. Windows Smart App Control compatibility
- Smart App Control stays enabled. **No Chromium, Playwright driver or test binary is executed on the Windows host.**
- `scripts/verify-in-container.ps1` is extended:
  - It builds or reuses `automataflow-verify:pw-<version>`.
  - It runs with `--init --shm-size=1g` as a **non-root** user (needed for the Chromium sandbox).
  - It copies the source in, builds with warnings as errors, and runs **all** test projects, including browser tests, against the compose PostgreSQL.
- Browser tests do not skip silently. If Chromium is missing, they fail with an actionable message, consistent with the database-test rule.
- The Windows host remains for editing. Building is fine there; running is not reliable. CLAUDE.md is updated accordingly.

---

## Proposed classes and interfaces
**Application (ports, no packages):**
- `ExecutionScope` (record: OrganizationId, JobId?, JobRecordId?, AttemptNumber, SessionId).
- `ExecutionLogEntry` and `IExecutionLogSink`.
- `ArtifactKind` (Screenshot, Download, Trace), `ArtifactDescriptor` and `IArtifactStore`.

**Automation.Browser (Playwright):**
- `IBrowserHost` / `BrowserHost`, `BrowserInstallation`, `ChromiumLaunchOptionsBuilder`.
- `BrowserEngineOptions`, `BrowserEngineOptionsValidator`.
- `IBrowserSessionFactory` / `BrowserSessionFactory`, `BrowserSessionRequest`, `IBrowserSession` / `BrowserSession`.
- `ElementSelector`, `LocatorResolver`, `InputValue`, `SelectOption`, `WaitCondition`, `NavigationResult`, `DownloadResult`, `ScreenshotRequest`.
- `OperationRunner`, `BrowserOperationException`, `BrowserErrorCode`, `ErrorClassifier`.
- `SessionGuards` (route, WebSocket, popup, dialog, crash), `SessionEventRecorder`, `ScreenshotService`, `DownloadHandler`, `FileNameSanitizer`, `SensitiveValueTracker`, `LogRedactor`.
- `DependencyInjection.AddBrowserEngine(IServiceCollection, IConfiguration)`.

**Automation.Network (no Playwright):**
- `EgressPolicy`, `AllowedHostRule`, `IpAddressPolicy`, `HostNameNormalizer`, `RequestGuard`, `RequestDecision`.
- `IDnsResolver` / `SystemDnsResolver`.
- `EgressProxyServer`, `EgressProxyOptions`, `ProxyRequestParser`, `ProxyConnectionHandler`.
- `IEgressSessionRegistry` / `EgressSessionRegistry`, `SecurityEvent`.

```csharp
public interface IBrowserSessionFactory
{
    Task<IBrowserSession> CreateAsync(BrowserSessionRequest request, CancellationToken cancellationToken);
}

public interface IBrowserSession : IAsyncDisposable
{
    Guid Id { get; }
    Task<NavigationResult> NavigateAsync(Uri url, CancellationToken cancellationToken);
    Task ClickAsync(ElementSelector selector, CancellationToken cancellationToken);
    Task FillAsync(ElementSelector selector, InputValue value, CancellationToken cancellationToken);
    Task SelectAsync(ElementSelector selector, SelectOption option, CancellationToken cancellationToken);
    Task WaitForAsync(WaitCondition condition, CancellationToken cancellationToken);
    Task<string?> GetTextAsync(ElementSelector selector, CancellationToken cancellationToken);
    Task<ArtifactDescriptor> ScreenshotAsync(ScreenshotRequest request, CancellationToken cancellationToken);
    Task<DownloadResult> DownloadAsync(ElementSelector trigger, CancellationToken cancellationToken);
}
```

## Project / file structure
```
src/AutomataFlow.Application/Execution/        ExecutionScope, ExecutionLogEntry, IExecutionLogSink,
                                               ArtifactKind, ArtifactDescriptor, IArtifactStore
src/AutomataFlow.Automation/
  DependencyInjection.cs
  Browser/        BrowserHost, BrowserInstallation, ChromiumLaunchOptionsBuilder, BrowserEngineOptions(+Validator),
                  BrowserSessionFactory, BrowserSession, BrowserSessionRequest, SessionGuards, SessionEventRecorder,
                  OperationRunner, Errors/, Selectors/, Artifacts/ (ScreenshotService, DownloadHandler, FileNameSanitizer),
                  Redaction/ (LogRedactor, SensitiveValueTracker)
  Network/        EgressPolicy, AllowedHostRule, IpAddressPolicy, HostNameNormalizer, RequestGuard, IDnsResolver,
                  SystemDnsResolver, Proxy/ (EgressProxyServer, ProxyRequestParser, ProxyConnectionHandler,
                  EgressSessionRegistry, EgressProxyOptions), SecurityEvent
tests/AutomataFlow.UnitTests/Automation/        policy, normalizer, guard, parser, redactor, sanitizer, options,
                                                launch-args, classifier, runner, source-scan, namespace tests
tests/AutomataFlow.AutomationTests/             Support/ (DemoCrmProcess, HostileFixtureServer, FakeDnsResolver,
                                                InMemoryArtifactStore, CollectingLogSink, BrowserFixture)
                                                Proxy/, Browser/, Security/, DemoCrm/
deploy/verify/Dockerfile                        verification image with pinned Chromium
scripts/verify-in-container.ps1                 extended (image build/reuse, --init, --shm-size, non-root)
docs/BROWSER_ENGINE.md                          operator + security documentation (written at the end of Phase 3)
```
Not touched: Domain, Infrastructure, Api, Worker (Worker wiring is Phase 5), DemoCrm (used as-is).

## NuGet dependencies
| Package | Version | Licence | Project |
|---|---|---|---|
| Microsoft.Playwright | 1.64.1 | Apache-2.0 | Automation |
| Microsoft.Extensions.Options | 10.0.15 | MIT | Automation |
| Microsoft.Extensions.Logging.Abstractions | 10.0.15 | MIT | Automation |
| Microsoft.Extensions.DependencyInjection.Abstractions | 10.0.15 | MIT | Automation |
| Microsoft.Extensions.Configuration.Binder | 10.0.15 | MIT | Automation (options binding) |

- **AutomationTests:** `FrameworkReference Microsoft.AspNetCore.App` (hostile fixture server; part of the SDK, not a package) plus the existing xUnit packages.
- **Not used:** no proxy library, no Polly, no paid packages, no Redis, RabbitMQ or Kubernetes.
- **Versions:** re-verified on nuget.org at implementation time and pinned in `Directory.Packages.props`.

## Execution lifecycle
```
Caller (tests now, Worker in Phase 5)
  |
  | CreateAsync(request: scope, egress policy, overrides)
  v
BrowserSessionFactory -- wait for slot (MaxConcurrentSessions) -------------------------------+
  |                                                                                           |
  | BrowserHost.GetBrowserAsync()  (launch / relaunch after crash / recycle; per-session mode) |
  | EgressSessionRegistry.Register(creds -> policy)                                           |
  | browser.NewContextAsync(proxy creds, SW blocked, downloads?, viewport, no permissions)    |
  | attach guards: route(**/*) + websocket route, popup cap, dialog dismiss, recorder, crash  |
  v                                                                                           |
IBrowserSession ----- NavigateAsync / ClickAsync / FillAsync / ... ---------------------------+
  |   OperationRunner(timeout, deadline, ct)                                                  |
  |     -> Playwright -> Chromium network service -> Egress proxy (auth, rule, DNS, IP check, |
  |        pin) -> target                                                                     |
  |   events -> IExecutionLogSink ; on failure (policy) -> masked screenshot -> IArtifactStore|
  v                                                                                           |
DisposeAsync (always, idempotent): close context (timeout) -> revoke proxy creds ->           |
  delete temp dir -> release slot -> per-session: close browser -> on hang/fault: recycle <---+
```

## Security threat model (browser engine)
| # | Threat | Vector | Mitigation | Verified by |
|---|---|---|---|---|
| T1 | SSRF to internal services / cloud metadata | workflow URL, redirects, iframes, subresources, fetch, popups, WebSockets | route guard + mandatory egress proxy (IP policy on every connection) + network isolation (hosted) | hostile SSRF matrix |
| T2 | DNS rebinding / mixed records | attacker DNS returns public then private | proxy resolves once, rejects any private address, connects to pinned IP; Chromium cannot resolve | proxy pinning + browser rebinding tests |
| T3 | URL parser differentials | `0x7f.1`, decimal IP, userinfo, backslash, trailing dot, IDN, zone IDs, IPv4-mapped IPv6 | canonicalization + strict rejection, at both layers | normalizer/guard unit tests + browser cases |
| T4 | Proxy abuse | other local processes, request smuggling, open relay | per-session credentials, loopback bind, strict parser, one request per connection, CONNECT port rules | proxy integration tests |
| T5 | Non-HTTP channels | WebRTC UDP, QUIC, service workers, prefetch | flags, SW block, network isolation | launch-args tests + browser tests |
| T6 | Local file / browser internals | `file://`, `chrome://`, `view-source:`, downloads written outside the temp dir | scheme block, generated filenames, per-session dir | browser + sanitizer tests |
| T7 | Credential leakage | logs, exceptions, screenshots, traces, URLs | `Sensitive` values, redactor, masks, tracing off, query redaction | credential protection tests |
| T8 | Arbitrary script execution | customer-supplied JS or selector engines | no evaluate APIs, typed selectors, source-scan test | source-scan + selector tests |
| T9 | Cross-session/tenant contamination | shared cookies, cache, storage, SW, shared browser process | fresh context per session, SW blocked, `BrowserPerSession` required for hosted multi-tenant | isolation tests + options validator |
| T10 | Browser exploit by malicious page | renderer/browser RCE | sandbox on, non-root, container + seccomp, network to proxy only, short-lived processes, patch cadence | config/validator tests; container hardening in Phase 7 |
| T11 | Resource exhaustion | popup storms, huge downloads, memory bombs, hung pages, dialog traps | session deadline, popup cap, byte/size quotas, dialog auto-dismiss, recycle, container limits | resource-limit tests |
| T12 | Exfiltration to an allowed host | page script posting data to an allowed but hostile host | out of the engine's control (the customer chose the host); per-session byte quota bounds volume; audit log | documented residual risk |
| T13 | Supply chain | unpinned browser or runtime download | pinned package + image-baked browser, no runtime download in hosted mode, version-lock test | version-lock test |

Residual risks (stated, not hidden):
- In-process proxy mode is not a hard network boundary (T10).
- Text secrets rendered by the site itself are not auto-masked (T7).
- Allowed-host exfiltration can only be bounded, not prevented (T12).

## Phase 3 acceptance checklist
1. [ ] Spike results recorded; any disproved assumption reported before continuing.
2. [ ] Playwright 1.64.1 only in Automation. Api/Worker/Infrastructure/Domain unchanged except new Application ports. Architecture tests (including the Network-namespace Playwright check) green.
3. [ ] Verify image with pinned Chromium; `verify-in-container.ps1` runs all tests including browser tests as non-root; version-lock test green.
4. [ ] BrowserHost: launch, crash detection, relaunch, recycle, graceful shutdown with no orphaned processes (tested).
5. [ ] Fresh isolated context per session; isolation tests for cookies/storage/cache/service workers green.
6. [ ] Navigate/click/fill/select/wait/getText operations with typed selectors and auto-wait; strict mode.
7. [ ] Screenshots (OnFailure default, masking verified by pixel test); downloads (generated names, limits, hash, temp-dir cleanup).
8. [ ] Timeouts and cancellation: hung operations aborted within deadline; caller cancellation honoured; error codes and `IsTransient` classified.
9. [ ] Structured execution events with scope; sensitive values never logged; query strings redacted.
10. [ ] Egress proxy: per-session credentials, host/port rules, DNS resolution + IP policy + pinning, quotas, strict parsing.
11. [ ] Chromium forced through the proxy (including loopback), cannot resolve names itself; WebRTC/QUIC/SW disabled.
12. [ ] Hostile SSRF matrix: every case blocked with a `RequestBlocked` event; positive controls pass.
13. [ ] No script-evaluation APIs (source-scan test); selectors are data.
14. [ ] Options validator enforces hosted-mode rules (BrowserPerSession, sandbox, https-only, no loopback exceptions).
15. [ ] DemoCrm end-to-end browser scenarios green (registration, validation, duplicate, fail-2, commit-fail replay, search, details, logout).
16. [ ] Full solution builds with 0 warnings; all existing tests stay green.
17. [ ] `docs/BROWSER_ENGINE.md`, ARCHITECTURE section 9.1 cross-check, ADRs for spike outcomes, ROADMAP updated.
18. [ ] Secrets audit clean; separate Phase 3 commit; working tree clean; stop for approval.

## Estimated effort
| Work item | Days |
|---|---|
| Spike (Chromium install in image, proxy flags, redirect/route semantics, per-context proxy, abort-by-close, per-session launch cost) | 0.5 |
| Application ports, options + validator, DI, launch-args builder | 0.5 |
| BrowserHost + session factory + isolation + cleanup + recorder | 1.0 |
| Operations, selectors, screenshots (masking), downloads, OperationRunner, error classification | 1.0 |
| Network policy: IP policy, normalizer, request guard (+ unit tests) | 0.75 |
| Egress proxy server (+ proxy integration tests) | 1.0 |
| Hostile fixture + SSRF browser matrix + credential tests | 0.75 |
| DemoCrm child-process fixture + end-to-end browser tests | 0.5 |
| Verify image + script + docs + ADRs + review | 0.5 |
| **Total** | **6.5 (range 5.5–7.5)** |

The original roadmap allotted 2 days to Phase 3. The difference is the mandatory egress proxy and the hostile test matrix. This moves the overall estimate to roughly 20.5–22 working days, above the 16–20 range, consistent with "quality over deadline". Options to reduce scope without lowering security are listed in "Decisions requested".

## Decisions requested before implementation
1. **Mandatory egress proxy in every mode,** including Development and tests, built in-process now. The hosted sidecar container and Docker network isolation are built in Phase 7.
2. **Isolation defaults:** `SharedBrowser` for Development/SelfHosted; `BrowserPerSession` enforced for HostedMultiTenant.
3. **Verification image:** custom `deploy/verify/Dockerfile` on `dotnet/sdk:10.0` with pinned Chromium. The alternative is the official `mcr.microsoft.com/playwright/dotnet:v1.64.1-noble` image (exists; .NET 10 SDK presence unverified).
4. **DemoCrm launched as a child process** for browser tests.
5. **New Application ports** (`IExecutionLogSink`, `IArtifactStore` and records) added in Phase 3 so Phase 5 can implement them in Infrastructure.
6. **Logging defaults:** redact URL query strings, tracing off, screenshots on failure only.
7. **Timeline:** accept 6.5 days for Phase 3. Alternatively defer the remote-browser design and per-session launch benchmarking to Phase 7 (saves about 0.5 day).
