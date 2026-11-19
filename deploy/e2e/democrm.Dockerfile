# TEST FIXTURE ONLY: the fictional DemoCrm portal for the dashboard end-to-end journey (ADR-027: never a production
# dependency; this image is built and used only by scripts/e2e-dashboard.ps1 and never published).
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/DemoCrm -c Release -o /out -p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15-noble-chiseled@sha256:7c625e44e5d588b4d7b2a913e94ca4bd5098ce47442da404829e4c7936a764e1
ENV ASPNETCORE_HTTP_PORTS=8080 ASPNETCORE_ENVIRONMENT=Production
COPY --from=build --chown=0:0 --chmod=0555 /out /app
WORKDIR /app
USER 10050:10050
ENTRYPOINT ["dotnet", "/app/DemoCrm.dll"]
