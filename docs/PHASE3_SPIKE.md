# Phase 3 spike results (2026-11-11)

**Outcome: 30/30 checks pass. No security assumption failed; two assumptions needed a safe, stricter correction (below). Implementation proceeds.**

Environment: `automataflow-verify:pw-1.64.1` (ADR-033): .NET SDK 10.0.404 on Ubuntu 24.04, Microsoft.Playwright 1.64.1, Chromium build 1248 = *Google Chrome for Testing 156.0.8078.4* (headless shell). Run as non-root uid 10001 with `--init --shm-size=1g` and the derived seccomp profile. The spike code is throwaway and was kept outside the repository; every behaviour below is re-proven by permanent Phase 3 tests.

## Corrections found by the spike
| # | Assumption in the plan | What the spike showed | Resolution (ADR) |
|---|---|---|---|
| C1 | Chromium sandbox can stay on in a container | With Docker's default seccomp profile Chromium aborts: *"No usable sandbox!"*; unprivileged user namespaces are denied (`unshare: Operation not permitted`) | **Not** `--no-sandbox`. `deploy/verify/chromium-seccomp.json` = Docker's default profile + one rule allowing `clone`/`unshare` (namespaces) without `CAP_SYS_ADMIN`. Sandbox then starts; privileged operations (mount, mount namespaces) remain denied. Hosted deployments must use the same profile (ADR-035). |
| C2 | `--host-resolver-rules="MAP * ~NOTFOUND"` stops Chromium resolving names | It also maps **IP literals**, including the proxy address `127.0.0.1`, so every proxied request failed with `ERR_PROXY_CONNECTION_FAILED` (safe: it failed closed, but unusable) | Rule becomes `MAP * ~NOTFOUND, EXCLUDE 127.0.0.1`. Chromium can then reach **only** the local egress proxy; any other name or IP literal fails `ERR_NAME_NOT_RESOLVED` (E3a, diag). ADR-036. |

## Confirmed behaviour (drives the design)
- **Route interception does not see redirect hops** (same-host or cross-host); the context `Request` event does (with `RedirectedFrom`), and **the proxy sees every hop**. ⇒ The egress proxy is the authoritative control; the request recorder detects and logs violations; route guards are an early, URL-level layer only (ADR-036).
- Route interception **does** see iframes, images, fetch/XHR and popups; `RouteWebSocketAsync` sees WebSockets; WebSockets (even `ws://`) reach the proxy as `CONNECT`.
- Chromium canonicalizes IP literals before contacting the proxy: `2130706433` and `0x7f.1` become `127.0.0.1`; `[::ffff:127.0.0.1]` becomes `[::ffff:7f00:1]` (the proxy must unwrap IPv4-mapped IPv6).
- Loopback (`127.0.0.1`, `localhost`, `[::1]`) and the container's LAN IP are sent **to the proxy**, not connected directly.
- Hostnames are forwarded unresolved (`GET http://allowed.test/`, `CONNECT secure.test:443`), so DNS is under the proxy's control.
- Per-context proxy credentials work: the first request arrives without credentials, gets 407, Chromium retries with Basic credentials; each context uses its own; wrong credentials never reach the target (407).
- **Fail-closed**: a context without its own proxy, an unreachable proxy, and a proxy that dies mid-session all fail with `ERR_PROXY_CONNECTION_FAILED`; the forbidden service received **0** connections, while a control browser without these settings **could** reach it (so the zero is meaningful).
- **Cancellation**: closing the page/context aborts a hung navigation and a waiting click within ~12 ms (`ERR_ABORTED` / `TargetClosedException`).
- **WebRTC**: with `--force-webrtc-ip-handling-policy=disable_non_proxied_udp` 0 STUN packets were sent; without it (control) 5 were.
- No unexpected background destinations reached the proxy.
- Basic lifecycle timing (warm): launch 55 ms (first launch 882 ms), new context 4 ms, close context 3 ms, close browser 38 ms.

## Not covered by the spike (covered by Phase 3 tests or later phases)
- DNS rebinding, mixed DNS answers and IP pinning are properties of **our** proxy implementation; proven by Phase 3 proxy tests with a fake resolver.
- QUIC: disabled by flag and irrelevant while all traffic is forced through an HTTP proxy; network-level UDP blocking is part of hosted isolation (Phase 7, ADR-031).

## Raw output
```
[PASS] E1 sandbox :: Chromium launched with sandbox ON as uid ? in 882 ms
[PASS] E2a allowed host via proxy :: status=200 body=ok; proxy saw name 'allowed.test' unresolved; context created in 32 ms
[PASS] E2b loopback/private via proxy http://127.0.0.1:45361/x :: status=403 body=other; proxy last saw: GET http://127.0.0.1:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://localhost:45361/x :: status=403 body=other; proxy last saw: GET http://localhost:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://[::1]:45361/x :: status=403 body=other; proxy last saw: GET http://[::1]:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://[::ffff:127.0.0.1]:45361/x :: status=403 body=other; proxy last saw: GET http://[::ffff:7f00:1]:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://2130706433:45361/x :: status=403 body=other; proxy last saw: GET http://127.0.0.1:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://0x7f.1:45361/x :: status=403 body=other; proxy last saw: GET http://127.0.0.1:45361/x user=u1 auth=ok
[PASS] E2b loopback/private via proxy http://172.17.0.2:45361/x :: status=403 body=other; proxy last saw: GET http://172.17.0.2:45361/x user=u1 auth=ok
[PASS] E2c https as CONNECT with hostname :: error=net::ERR_TUNNEL_CONNECTION_FAILED; proxy saw: CONNECT secure.test:443 user=u1 auth=ok
[PASS] E2d forbidden service never contacted directly :: B hits during E2: 0
[PASS] E3a no proxy + resolver rule: names do not resolve :: error=net::ERR_NAME_NOT_RESOLVED
       diag: no proxy + rule, non-excluded IP literal 172.17.0.2: error=net::ERR_NAME_NOT_RESOLVED
[PASS] E3c diag: 'MAP * ~NOTFOUND' without EXCLUDE also blocks IP literals (explains proxy unreachability) :: error=net::ERR_NAME_NOT_RESOLVED
[PASS] E3b control (no rule, no proxy) can resolve and reach B :: status=200 body=ok; status=200 body=B-SECRET!; proves B is reachable, so zero-hit results elsewhere are meaningful
[PASS] E4a context without per-session proxy fails closed :: error=net::ERR_PROXY_CONNECTION_FAILED; error=net::ERR_PROXY_CONNECTION_FAILED; error=net::ERR_PROXY_CONNECTION_FAILED
[PASS] E4b per-session proxy unreachable fails closed :: error=net::ERR_PROXY_CONNECTION_FAILED; error=net::ERR_PROXY_CONNECTION_FAILED
[PASS] E4c proxy dies mid-session fails closed (no direct fallback) :: before=status=200 body=ok; after: error=net::ERR_PROXY_CONNECTION_FAILED; error=net::ERR_PROXY_CONNECTION_FAILED
[PASS] E5a cross-host redirect: hop blocked by proxy :: status=403 body=other; route saw hop: False; request event saw hop: True
       route-handler URLs: http://allowed.test/redirect-to-b
       request events: http://allowed.test/redirect-to-b | http://127.0.0.1:45361/landing  (redirected from http://allowed.test/redirect-to-b)
[PASS] E5b same-host redirect followed :: status=200 body=ok; route saw /landing: False
[PASS] E5c iframe/img/fetch/popup routed and blocked :: route saw: /frame=True, /img.png=True, /api=True, /popup=True; proxy saw: /frame=True, /img.png=True, /api=True, /popup=True
[PASS] E5d websocket routed and blocked :: routeWebSocket saw: ws://127.0.0.1:45361/ws; proxy saw CONNECT for ws: True
[PASS] E5e redirect to https private target blocked :: error=net::ERR_TUNNEL_CONNECTION_FAILED
[PASS] E6a each context authenticates with its own credentials :: status=200 body=ok; status=200 body=ok
[PASS] E6b wrong credentials never reach the target :: status=407 body=other; proxy: GET http://allowed.test/?ctx=bad user=- auth=no | GET http://allowed.test/?ctx=bad user=u1 auth=no
[PASS] E6c proxy challenge/credential flow :: first request arrives without credentials, receives 407, Chromium retries with Basic credentials
[PASS] E7a hung navigation aborted by closing the page :: aborted 12 ms after close; PlaywrightException: net::ERR_ABORTED; maybe frame was detached?
[PASS] E7b waiting click aborted by closing the context :: aborted 12 ms after close; TargetClosedException: Target page, context or browser has been closed
[PASS] E8 WebRTC UDP blocked by policy flag :: STUN packets with flag=0, without flag (control)=5 to 172.17.0.2:40615
[PASS] E9 basic lifecycle timing :: launch=55 ms, newContext=4 ms, closeContext=3 ms, closeBrowser=38 ms
[PASS] E9b no unexpected background destinations at the proxy :: none
```
