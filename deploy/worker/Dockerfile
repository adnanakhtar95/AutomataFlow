# AutomataFlow Worker image (ADR-075): the Worker, the process-death supervisor and the launcher. ONE container = ONE
# Worker incarnation. Run it only with the posture of the "worker" service in deploy/docker-compose.yml:
#   NO init (the supervisor must be PID 1); user: root (the launcher drops everything); cap_drop ALL; cap_add NET_ADMIN, SETUID, SETGID, SETPCAP,
#   SYS_CHROOT; seccomp=deploy/verify/chromium-seccomp.json. Never --privileged, never CAP_SYS_ADMIN.
# The launcher refuses to start under any other capability set or without seccomp filtering.
#
# Build from the repository root:
#   docker build -f deploy/worker/Dockerfile --build-arg PLAYWRIGHT_VERSION=<pin from Directory.Packages.props> -t automataflow-worker .
# Base: the same pinned .NET SDK image as the verification image (ADR-033); a slimmer runtime base is a later optimization.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AutomataFlow.Worker -c Release -o /out/worker \
 && dotnet publish src/AutomataFlow.Supervisor -c Release -o /out/supervisor

FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb
ARG PLAYWRIGHT_VERSION
RUN test -n "$PLAYWRIGHT_VERSION" || (echo "PLAYWRIGHT_VERSION build argument is required" && exit 1)
ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    AF_WORKER_COMMAND="dotnet /app/worker/AutomataFlow.Worker.dll" \
    AF_SUPERVISOR_COMMAND="dotnet /app/supervisor/AutomataFlow.Supervisor.dll"

# Chromium for the pinned Playwright version, installed exactly as in the verification image.
COPY deploy/verify/installer/ /opt/pw-installer/
RUN cd /opt/pw-installer \
 && dotnet build -c Release -p:PlaywrightVersion=${PLAYWRIGHT_VERSION} -o /opt/pw-installer/out \
 && dotnet /opt/pw-installer/out/PlaywrightInstaller.dll install --with-deps chromium \
 && chmod -R a+rX /ms-playwright \
 && rm -rf /opt/pw-installer /root/.nuget/packages /tmp/*

COPY --from=build /out/worker /app/worker
COPY --from=build /out/supervisor /app/supervisor
COPY deploy/worker/af-worker-launch.sh deploy/worker/af-unit-init.sh /app/deploy/

# Binaries and scripts are root-owned and read-only for everyone else. Persistent data belongs to the Worker's uid
# (10001) and is OWNER-ONLY (0700): the supervisor (10002) is in the Worker's group for the ledger and handshake directories
# only, and must never read the Data Protection key ring or artifacts (ADR-075).
RUN chown -R root:root /app && chmod -R a+rX,go-w /app && chmod 0555 /app/deploy/*.sh \
 && mkdir -p /var/lib/automataflow/keys /var/lib/automataflow/artifacts \
 && chown -R 10001:10001 /var/lib/automataflow && chmod 0700 /var/lib/automataflow/keys /var/lib/automataflow/artifacts

USER root
ENTRYPOINT ["/bin/bash", "/app/deploy/af-worker-launch.sh"]
