# AutomataFlow Api release image (Phase 7, pulled forward for the Phase 6 end-to-end harness).
# Build from the repository root:
#   docker build -f deploy/api/Dockerfile -t automataflow-api .
# Runtime: the chiseled ASP.NET image (no shell, no package manager), non-root uid 10001 (the Worker's uid: the Data
# Protection key ring and the artifacts volume are owner-only, ADR-075). The Api never runs browser automation.
# Commands:
#   (default)      serve HTTP on 8080
#   migrate        apply pending EF migrations and exit (run once per release, before the new Api and Workers start)
#   healthcheck    probe http://127.0.0.1:8080/health/ready (exit 0 = ready); used by HEALTHCHECK
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/AutomataFlow.Api -c Release -o /out/api -p:UseAppHost=false \
 && mkdir -p /out/data/keys /out/data/artifacts

# Same .NET runtime (10.0.15) as the pinned SDK.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_NOLOGO=1 \
    DataProtection__KeysDirectory=/var/lib/automataflow/keys \
    Artifacts__RootDirectory=/var/lib/automataflow/artifacts
COPY --from=build --chown=0:0 --chmod=0555 /out/api /app
# Persistent data: owner-only (0700) for uid 10001. A named volume mounted here inherits this owner and mode.
COPY --from=build --chown=10001:10001 --chmod=0700 /out/data /var/lib/automataflow
WORKDIR /app
USER 10001:10001
EXPOSE 8080
HEALTHCHECK --interval=15s --timeout=6s --start-period=20s --retries=3 CMD ["dotnet", "/app/AutomataFlow.Api.dll", "healthcheck"]
ENTRYPOINT ["dotnet", "/app/AutomataFlow.Api.dll"]
