# AutomataFlow — Troubleshooting

Symptoms, likely causes and fixes. Customer-facing symptoms first, then platform operator symptoms. Never "fix" a
record that needs review by editing the database: follow docs/RUNBOOK.md §4.

## Customers
| Symptom | Likely cause | Fix |
|---|---|---|
| Signed out after every page reload | the dashboard is served over plain HTTP on a non-localhost name; browsers drop the Secure session cookie | serve it over HTTPS (docs/DEPLOYMENT.md §4) |
| "Too many attempts. Please wait a minute" at sign-in | more than 20 password attempts per minute from one address (shared office NAT, scripts) | wait a minute; operators can raise `Auth:PermitsPerMinute` |
| "The workspace is suspended." | a platform operator suspended the workspace | contact your provider |
| Invitation link says invalid/expired/used | links work once and expire after 7 days; a newer invitation replaces an older one | ask an Admin for a new invitation |
| Publish button disabled with a warning about claims | the workflow claims the target refuses duplicates (`targetEnforced`) | confirm only if true; otherwise remove the `idempotency` block |
| Upload refused: 415 / 413 / column unknown | wrong file type, file too large (default 10 MB), or a column the workflow does not declare | use .csv/.json, split the file, or tick "Ignore columns the workflow does not declare" |
| Many records Failed with `ElementNotFound` | the target page changed (selector no longer matches) | open a failed attempt's screenshot, update the selector, publish a new version |
| Records Failed with `NavigationBlocked` / egress errors | the target host is not in the workflow's allowed hosts, or resolves to a private address | add the origin to `allowedHosts`; private targets are refused in production by design |
| Records stay Pending | no Worker is running, or the workspace is suspended | check Platform → Operations "Live workers" (operators) |
| Records Needs review | an attempt may have changed the target system and its outcome could not be proven | docs/USER_GUIDE.md §4 |

## Platform operators
| Symptom | Likely cause | Fix |
|---|---|---|
| `validate-deployment.ps1` refuses the configuration | placeholder secrets, posture drift, development settings | fix the reported item; never bypass the check |
| Worker container restarts in a loop with `af-worker-launch: refused` | the container posture differs (init, capabilities, seccomp, privileged) | restore the posture from deploy/docker-compose.prod.yml |
| Worker log `Worker startup refused` | journal not durable (`fsync` off), lease too short, keys/artifacts not configured, handshake failed | fix the named problem; the Worker is fail-closed on purpose |
| Api does not start: key ring certificate could not be loaded | wrong `AF_KEYRING_CERTIFICATE_PASSWORD` or file not readable by uid 10001 | fix the password or ownership (`chown 10001:10001`, `chmod 0400`) |
| Secrets cannot be decrypted after a restore | the backup was restored with a different key ring certificate | restore with the same `deploy/.env.prod` and certificate as the backup |
| Gateway unhealthy | nginx could not render its configuration (tmpfs permissions) or the Api is down | `docker compose logs web api` |
| Operations shows a stale Worker and AwaitingFence records | a Worker host died and its supervisor could not fence it | docs/RUNBOOK.md §5 (audited operator fence) |
| Disk filling up | artifact growth (screenshots) | schedule `purge-artifacts` (docs/RUNBOOK.md §9) |
| `Cannot load library libgssapi_krb5.so.2` in Api logs | Npgsql probing for Kerberos in the minimal image | harmless; password authentication is used |
