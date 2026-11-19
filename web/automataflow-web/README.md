# AutomataFlow dashboard

The customer-facing React application (Phase 6, ADR-076, ADR-077). It talks only to the AutomataFlow Api, same-origin.

## Development
```powershell
cd web/automataflow-web
npm ci
$env:AF_API_URL = 'http://127.0.0.1:5000'   # the running Api (Vite proxies /api to it)
npm run dev                                 # http://localhost:5173
```
Sign-in uses the Api's browser-session transport: the refresh token is an HttpOnly, Secure cookie, which browsers keep
on `localhost` without HTTPS. Use `localhost`, not a LAN address, during development.

## Checks
| Command | What it does |
|---|---|
| `npm run lint` | ESLint, zero warnings; forbids `dangerouslySetInnerHTML` and web storage |
| `npm run typecheck` | `tsc --noEmit` (strict) |
| `npm test` | Vitest unit and component tests (fake network, real router/session/query client) |
| `npm run build` | production bundle in `dist/` |
| `npm run check:api` | fails if `src/api/schema.d.ts` is stale against `openapi/automataflow-api.json` |
| `..\..\scripts\e2e-dashboard.ps1` | real-browser journey against the release images (Api, Worker, gateway, DemoCrm fixture) |

`scripts/verify-in-container.ps1` runs lint, type check, tests, build and the API-type check in the pinned Node image.

## The API contract
`openapi/automataflow-api.json` is a committed snapshot of the Api's OpenAPI document; `OpenApiContractTests` fails when
the Api drifts from it. After changing an Api contract, run `scripts/update-openapi.ps1` (regenerates the snapshot and
`src/api/schema.d.ts` in the pinned containers), review both diffs, and commit them with the change.

## Rules
- API text is rendered as text, never as HTML. Validation messages come from the Api.
- No web storage. The access token lives in memory; the refresh token only in the HttpOnly cookie.
- Artifacts and exports are fetched with the bearer header and handed over as object URLs; tokens never go into URLs.
- Role checks in the UI only hide controls; the Api enforces every permission.
- The workflow editor offers only the actions listed by `GET /api/v1/workflow-catalog`.
