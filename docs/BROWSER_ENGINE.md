# Browser engine (Phase 3)

The browser engine lives in `src/AutomataFlow.Automation`. It gives the rest of the platform an isolated, policy-bound browser session per record attempt.

Related documents: ADR-029 to ADR-036, the spike results (`docs/PHASE3_SPIKE.md`) and the plan (`docs/PHASE3_PLAN.md`).

- **Consumers:** the Worker runs the engine (wired in Phase 5). The Api never references it; that is enforced by architecture tests.
- **Phase 4:** workflow actions will wrap the operations described here.

## Components

| Component | Responsibility |
|---|---|
| `BrowserHost` | Browser lifecycle only:<br>• Playwright and Chromium launch, recycling and crash replacement<br>• shutdown with escalation<br>• the fail-closed proxy gate<br>• launch timing (`LaunchCount`, `LastLaunchDuration`) |
| `IBrowserSessionFactory` | The only way to get a browser. It enforces the concurrency limit and validates the policy for the execution mode. It then creates a fresh context with **per-session proxy credentials**. |
| `IBrowserSession` | One isolated context, which is one record attempt.<br>• **Operations:** `NavigateAsync`, `ClickAsync`, `SubmitAsync` (click, then wait for the *new* document), `FillAsync`, `SelectAsync`, `WaitForAsync`, `GetTextAsync`, `GetAttributeAsync`, `ScreenshotAsync`, `DownloadAsync`.<br>• **Disposal** always cleans up: closes the context, revokes the proxy credentials, deletes the temp directory and releases the concurrency slot. |
| `Network.RequestGuard` | URL-level checks: scheme, host:port allow-list, userinfo and IP literals. Applied before navigating and to every request the browser lets us intercept (documents, subresources, frames, popups, WebSockets). |
| `Network.Proxy.EgressProxyServer` | **Authoritative** egress control (see below). It has no Playwright dependency. |
| `Network.IpAddressPolicy` / `HostNameNormalizer` | Deny-list of internal, reserved and metadata IPv4/IPv6 ranges, including embedded IPv4 forms. Canonical host parsing that rejects ambiguous forms. |

Application ports, implemented by Infrastructure in Phase 5:
- `IExecutionLogSink`, with `ExecutionLogEntry` and `ExecutionScope`.
- `IArtifactStore`, with `ArtifactSaveRequest` and `ArtifactDescriptor`.

## Configuration
Configuration is bound from `Automation:Browser` (`BrowserEngineOptions`) and `Automation:EgressProxy` (`EgressProxyOptions`). It is validated at startup by the hand-written `BrowserEngineOptionsValidator`.

| Setting | Default | Notes |
|---|---|---|
| `Mode` | `SelfHosted` | `Development`, `SelfHosted` or `HostedMultiTenant` |
| `Isolation` | `SharedBrowser` | `BrowserPerSession` is **required** in HostedMultiTenant |
| `MaxConcurrentSessions` | 4 | Allowed range 1–64 |
| `ChromiumSandbox` / `Headless` | true / true | May only be disabled in Development |
| `RecycleAfterSessions` / `RecycleAfterMinutes` | 50 / 30 | Recycling for a shared browser |
| `MaxPagesPerSession` | 5 | Extra popups are closed and logged (`PopupBlocked`) |
| `Screenshots` | `OnFailure` | `Never` disables all screenshots; `OnRequest` captures only explicit ones |
| `Timeouts` | launch 30 s, navigation 30 s, action 10 s, download 60 s, close 10 s, session 300 s | |
| `Downloads` | 25 MB, at most 10 per session, executable/script extensions blocked | |
| Egress proxy | header 10 s, DNS 5 s, connect 10 s, idle 60 s, tunnel lifetime 600 s, 32 connections and 200 MB per session | |

**Rules per execution mode:**
- **HostedMultiTenant:** sessions must use `https` rules and no private-network exceptions; otherwise creation fails with `PolicyViolation`.
- **Development:** a rule may set `allowPrivateNetwork` for an exact `host:port`. The local DemoCrm on `127.0.0.1:<port>` uses this; every other loopback port stays blocked.

## Network security model (ADR-030, ADR-036)
Never rely on the initial `Navigate` URL. Every browser connection goes through the egress proxy.

1. **Chromium is fenced in.**
   - Its launch-level proxy is the egress proxy, *without credentials*.
   - `--host-resolver-rules="MAP * ~NOTFOUND, EXCLUDE 127.0.0.1"` means Chromium cannot resolve any name or reach any IP except the proxy.
   - `<-loopback>` removes Chromium's implicit loopback bypass.
   - QUIC is disabled, WebRTC is limited to proxied UDP, and service workers, background networking and extensions are disabled.
2. **Per-session credentials.** Each context authenticates to the proxy with random credentials. The proxy applies *that session's* policy. Unknown or revoked credentials get 407.
3. **DNS and IP decisions in the proxy.**
   - The proxy resolves names itself.
   - It rejects the destination if **any** resolved address is private, loopback, link-local, metadata or reserved. A mixed answer counts as rejected.
   - It connects to the exact address it validated (pinning), so DNS rebinding cannot redirect an existing decision.
   - IPv4-mapped, NAT64 and 6to4 IPv6 forms are unwrapped.
4. **Redirect hops.** Route interception does not see redirect hops (spike). The proxy sees every hop, and the engine also inspects `Request.RedirectedFrom`. A navigation whose chain contains a denied hop fails with `NavigationBlocked`.
5. **Fail-closed.** No session is created unless the proxy accepts a connection right now. A proxy that dies mid-session makes requests fail with `EgressUnavailable`; there is never a direct fallback. A stopped proxy cannot be restarted in-process. A context created outside the factory has no credentials and reaches nothing.
6. **Strict proxy parsing.**
   - Printable ASCII only.
   - No obsolete line folding, no `Transfer-Encoding` on plain HTTP, no duplicate or conflicting `Content-Length`.
   - The `Host` header must match the target.
   - Only `http://` absolute-form is accepted; HTTPS must use `CONNECT`.
   - One request per plain-HTTP connection (`Connection: close`).
   - Head size, header count, timeouts, byte quotas and connection limits are enforced.

## Policy enforcement versus network isolation (ADR-031)
**Development and self-hosted** (the in-process proxy inside the Worker):
- What it gives: policy enforcement and auditing of all browser traffic.
- What it is **not**: a hard network boundary. A fully compromised browser process sharing the Worker's network namespace could open sockets directly.
- When it's acceptable: trusted, single-customer deployments.

**Hosted multi-tenant (required before untrusted workloads; Phase 7)** must add container-level isolation:
1. **Network:**
   - The browser runs in a container attached only to a Docker network with `internal: true`, whose single reachable peer is the egress-proxy container.
   - The database, Api, Worker internals and the host are **not** on that network.
   - The egress-proxy container is on the internal network and an egress network. Its own firewall drops RFC 1918, loopback, link-local (including `169.254.169.254`), CGNAT and ULA destinations as a second net beneath the proxy's IP policy.
   - No UDP egress from the browser network, which also blocks QUIC and WebRTC at the network level.
2. **Browser process:**
   - One browser process per session (`BrowserPerSession`, enforced by the validator).
   - Chromium sandbox on, non-root user, and the seccomp profile `deploy/verify/chromium-seccomp.json` (ADR-035). Never `--privileged`, `CAP_SYS_ADMIN` or `--no-sandbox`.
3. **Container hardening:**
   - Read-only root filesystem except temp and download directories.
   - CPU, memory, `pids` and `/dev/shm` limits; `--init`.
   - No secrets in the browser container's environment.
4. **Patching:** Playwright and Chromium are updated regularly. Each update is a version bump of the pin in `Directory.Packages.props` plus a rebuild of the image.

## Credentials and artifacts (ADR-032)
- **Sensitive input:** values passed as `InputValue.Sensitive(...)` are never logged (only their length) and are redacted from every message and exception. The raw Playwright exception is not attached as an inner exception.
- **URLs in logs:** logged without userinfo, with query values replaced by `***` and no fragment.
- **Screenshot masking:** screenshots mask (in `#FF00FF`):
  - sensitive-filled elements,
  - `input[type=password]`,
  - `[autocomplete^="cc-"]`,
  - `[data-af-sensitive]`,
  - `ExtraMaskSelectors` and per-session `MaskSelectors`.

  Verified by a pixel-level test.
- **Limitation, stated plainly:** secrets that a website renders as ordinary text are **not** detected automatically. Workflows must declare mask selectors for such areas.
- **Downloads:** saved under generated names in a per-session temp directory, size- and type-checked, hashed (SHA-256), stored through `IArtifactStore`, and never opened or executed. The suggested file name is sanitized and used as metadata only.
- **Tracing:** not implemented, and therefore off. If it is added later, trace files must be classified as sensitive artifacts.
- **Logs:** proxy credentials never appear in logs (tested).

## Error codes
`BrowserOperationException.Code` plus `IsTransient` feed Phase 5 retries:

| Code | Transient? |
|---|---|
| `Timeout`, `SessionTimeout` | yes |
| `ElementNotFound` | yes (the locator wait timed out) |
| `ElementNotUnique` | no (strict mode) |
| `NavigationBlocked` | no |
| `EgressUnavailable` | yes |
| `NetworkError` | yes |
| `BrowserCrashed`, `SessionClosed` | yes |
| `DownloadRejected`, `PolicyViolation`, `InvalidSelector` | no |
| `BrowserNotInstalled` | no |

Caller cancellation throws `OperationCanceledException` and closes the session, which aborts in-flight browser calls within milliseconds (spike E7).

## Verification image (ADR-033): build and reproduce
- **Run everything:** `scripts/verify-in-container.ps1` is the standard way to build and test, including real-browser tests. It builds the image automatically when missing; `-RebuildImage` forces a rebuild.
- **Manual build:**
  ```
  docker build --build-arg PLAYWRIGHT_VERSION=<Microsoft.Playwright version from Directory.Packages.props> \
               -t automataflow-verify:pw-<version> deploy/verify
  ```
- **Base image:** `mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb` (Ubuntu 24.04, SDK 10.0.404).
- **Chromium:** installed with the CLI bundled in the pinned Microsoft.Playwright package (`deploy/verify/installer`, `install --with-deps chromium`). Playwright 1.64.1 means Chromium build 1248, "Google Chrome for Testing 156.0.8078.4". The exact versions are written to `/ms-playwright/VERSIONS` in the image.
- **Runtime constraints:**
  - Tests run as the non-root user `verify` (uid 10001).
  - Containers run with `--init --shm-size=1g --security-opt seccomp=deploy/verify/chromium-seccomp.json`, so the Chromium sandbox is **on**.
- **Drift check:** `VersionLockTests` fails if the package, the central pin and the image disagree.
- **Never at runtime:** browsers are not downloaded at runtime. A missing browser fails with `BrowserNotInstalled`.
- **Windows hosts:** Smart App Control stays enabled. Chromium and the tests run only inside this image.

## Test coverage (all in the verification container)
- **Unit tests** (`tests/AutomataFlow.UnitTests/Automation`):
  - IP policy, host normalization, rules and wildcards, request guard.
  - Proxy parser, credential registry, redaction and file-name sanitization.
  - Selectors, error classification, options validation, launch arguments.
  - Source rules (no script evaluation or injection APIs; no Playwright in `Network`; Playwright only in Automation).
- **Proxy tests** (raw sockets):
  - Authentication and revocation, policy, private/mixed/rebinding DNS, internal IP literals.
  - Smuggling-shaped requests, oversized heads, slow headers, byte quota, connection limit.
  - Forwarding without proxy credentials, and fail-closed stopping.
- **Real Chromium tests:**
  - Lifecycle: reuse, recycle, per-session browsers, crash replacement, no orphaned processes, concurrency limit.
  - Session behaviour: operations, auto-wait, strict mode, timeouts, cancellation, session deadline, dialogs, popup cap, context isolation, service-worker blocking.
  - Artifacts: screenshots (pixel-level masking), downloads.
  - **SSRF matrix:**
    - localhost, 127.0.0.1 and `::1`;
    - private IPv4, IPv6 ULA and link-local;
    - `169.254.169.254`;
    - IPv4-mapped, NAT64 and decimal/hex IPv4 encodings;
    - public pages redirecting to private targets (http and https);
    - mixed DNS answers and DNS rebinding;
    - iframes, images, fetch, popups and WebSockets;
    - the proxy itself as a target;
    - WebRTC UDP;
    - non-web schemes.
  - Fail-closed: proxy unavailable, proxy lost mid-session, a context without credentials.
  - Credential protection.
  - DemoCrm end to end: login, registration, validation, duplicate, `fail-2`, `commit-fail` replay with a single customer, search and details, logout.
