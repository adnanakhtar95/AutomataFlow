#!/bin/bash
# AutomataFlow Worker container launcher (ADR-075). Runs ONCE, as root, at container start, as PID 1 (no tini: see ADR-075).
#
#  1. Refuses to start unless the container posture is exactly the approved one: capability bounding set within
#     NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT (so --privileged or CAP_SYS_ADMIN is caught) and seccomp filtering.
#  2. Creates the unit: a user namespace OWNED by the supervisor's uid (inner uid 1000 -> the Worker's kernel uid) plus a
#     PID namespace whose init (af-unit-init.sh) runs the Worker with NO capability in any set and no_new_privs.
#  3. exec's into the supervisor: its own uid, CAP_NET_ADMIN only (bounding set included), no_new_privs. The supervisor is
#     therefore the unit wrapper's parent and PID 1 (it receives docker stop's SIGTERM directly).
#
# Environment (required): AF_WORKER_COMMAND, AF_SUPERVISOR_COMMAND (both word-split), plus the platform configuration
# (ConnectionStrings__Default, ...) which both processes receive. Optional: AF_WORKER_UID (10001), AF_SUPERVISOR_UID
# (10002), AF_RUN_DIRECTORY (/tmp).
set -euo pipefail

SUP_UID="${AF_SUPERVISOR_UID:-10002}"
WORKER_UID="${AF_WORKER_UID:-10001}"
INNER_UID=1000
CAP_SETGID=$((1 << 6)); CAP_SETUID=$((1 << 7)); CAP_SETPCAP=$((1 << 8)); CAP_NET_ADMIN=$((1 << 12)); CAP_SYS_CHROOT=$((1 << 18))
ALLOWED=$((CAP_SETGID | CAP_SETUID | CAP_SETPCAP | CAP_NET_ADMIN | CAP_SYS_CHROOT))
REQUIRED=$((CAP_SETGID | CAP_SETUID | CAP_SETPCAP | CAP_NET_ADMIN))

refuse() { echo "af-worker-launch: refused: $*" >&2; exit 2; }

[ "$(id -u)" = 0 ] || refuse "the launcher must start as root (it drops everything before the Worker runs)"
bounding=$((16#$(awk '/^CapBnd:/ {print $2}' /proc/self/status)))
(( (bounding & ~ALLOWED) == 0 )) || refuse "the capability bounding set holds more than NET_ADMIN, SETUID, SETGID, SETPCAP, SYS_CHROOT (privileged container?)"
(( (bounding & REQUIRED) == REQUIRED )) || refuse "NET_ADMIN, SETUID, SETGID and SETPCAP are required"
[ "$(awk '/^Seccomp:/ {print $2}' /proc/self/status)" = 2 ] || refuse "seccomp filter mode is required"
[ "$SUP_UID" != 0 ] && [ "$WORKER_UID" != 0 ] && [ "$SUP_UID" != "$WORKER_UID" ] || refuse "the supervisor and the Worker need distinct non-root uids"
: "${AF_WORKER_COMMAND:?AF_WORKER_COMMAND is required}"
: "${AF_SUPERVISOR_COMMAND:?AF_SUPERVISOR_COMMAND is required}"

UNIT_INIT="$(dirname "$(readlink -f "$0")")/af-unit-init.sh"
[ -r "$UNIT_INIT" ] || refuse "af-unit-init.sh not found next to the launcher"
NONCE="$(head -c 12 /dev/urandom | od -An -tx1 | tr -d ' \n')"
RUN="${AF_RUN_DIRECTORY:-/tmp}"
SOCKET_DIR="$RUN/af-supervisor-$NONCE"
LEDGER_DIR="$RUN/af-ledger-$NONCE"
WORKER_HOME="$RUN/af-worker-home-$NONCE"

# The handshake directory belongs to the supervisor's uid and exists BEFORE the unit can run; only the Worker's group may
# enter it (setgid: the socket inherits that group). The ledger directory belongs to the Worker and is group-readable (the
# supervisor drains it); the Worker's home is owner-only.
setpriv --reuid="$SUP_UID" --regid="$SUP_UID" --groups="$WORKER_UID" --inh-caps=-all -- \
    sh -c 'mkdir -m 0750 "$1" && chgrp "$2" "$1" && chmod 2750 "$1"' sh "$SOCKET_DIR" "$WORKER_UID"
setpriv --reuid="$WORKER_UID" --regid="$WORKER_UID" --clear-groups --inh-caps=-all -- mkdir -m 0750 "$LEDGER_DIR"
setpriv --reuid="$WORKER_UID" --regid="$WORKER_UID" --clear-groups --inh-caps=-all -- mkdir -m 0700 "$WORKER_HOME"

export Containment__SupervisorSocket="$SOCKET_DIR/handshake.sock"
export Automation__EgressProxy__LedgerDirectory="$LEDGER_DIR"
export AF_WORKER_HOME="$WORKER_HOME"

# The unit wrapper: unshare(CLONE_NEWUSER) with euid = the supervisor's uid, so the supervisor OWNS the unit's user namespace
# (it may signal and inspect unit tasks, nothing else). It waits for its uid map, then creates the PID namespace; the
# namespace init drops to inner uid 1000 with every capability set (bounding included) empty and no_new_privs.
# shellcheck disable=SC2086
setpriv --reuid="$SUP_UID" --regid="$SUP_UID" --clear-groups --inh-caps=-all -- unshare --user --keep-caps -- \
    sh -c 'while [ -z "$(cat /proc/self/uid_map)" ]; do sleep 0.02; done
           exec unshare --pid --fork --kill-child=SIGKILL \
             setpriv --reuid=1000 --regid=1000 --clear-groups --inh-caps=-all --ambient-caps=-all --bounding-set=-all --no-new-privs \
             -- /bin/bash "$@"' sh "$UNIT_INIT" $AF_WORKER_COMMAND &
WRAPPER=$!

# Write the maps only once the wrapper is in its own user namespace (its uid_map reads empty until then).
for _ in $(seq 1 500); do
    [ -d "/proc/$WRAPPER" ] || refuse "the unit wrapper exited before its user namespace was mapped"
    [ -z "$(cat "/proc/$WRAPPER/uid_map" 2>/dev/null)" ] && break
    sleep 0.01
done
[ -z "$(cat "/proc/$WRAPPER/uid_map" 2>/dev/null)" ] || { kill -9 "$WRAPPER" 2>/dev/null; refuse "the unit wrapper did not create its user namespace"; }

# Map inner uid/gid 1000 to the Worker's. Writing needs CAP_SYS_ADMIN in the TARGET namespace (owner rule: euid = the
# supervisor's uid) and CAP_SETUID/CAP_SETGID in the parent: euid switch with the capabilities kept as ambient.
for map in uid_map gid_map; do
    printf '%s %s 1' "$INNER_UID" "$WORKER_UID" | setpriv --euid="$SUP_UID" --securebits=+no_setuid_fixup \
        --inh-caps=+setuid,+setgid --ambient-caps=+setuid,+setgid -- tee "/proc/$WRAPPER/$map" >/dev/null \
        || { kill -9 "$WRAPPER" 2>/dev/null; refuse "writing the unit's $map failed"; }
done

export Supervisor__UnitWrapperPid="$WRAPPER"
export Supervisor__WorkerUid="$WORKER_UID"
export HOME="$SOCKET_DIR"
# shellcheck disable=SC2086
exec setpriv --reuid="$SUP_UID" --regid="$SUP_UID" --groups="$WORKER_UID" \
    --inh-caps=-all,+net_admin --ambient-caps=-all,+net_admin --bounding-set=-all,+net_admin --no-new-privs \
    -- $AF_SUPERVISOR_COMMAND
