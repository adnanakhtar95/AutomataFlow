#!/bin/bash
# PID 1 of the unit's PID namespace (ADR-075). Runs with no capability and no_new_privs, as the Worker's uid.
# - reaps every orphan it adopts (Chromium helpers after a browser recycle), so zombies do not accumulate;
# - forwards SIGTERM/SIGINT to the Worker (graceful stop: attempts cancel with barrier-fenced completions);
# - exits with the Worker. When it exits, the kernel kills and reaps every remaining task of the namespace.
export HOME="${AF_WORKER_HOME:-/tmp}"
"$@" &
child=$!
trap 'kill -TERM "$child" 2>/dev/null' TERM INT
status=0
while kill -0 "$child" 2>/dev/null; do
    wait "$child"
    status=$?
done
exit "$status"
