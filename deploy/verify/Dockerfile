# AutomataFlow verification image (ADR-033). Build it with scripts/verify-in-container.ps1, which passes
# PLAYWRIGHT_VERSION from Directory.Packages.props (single source of truth). Reproduction steps:
# docs/BROWSER_ENGINE.md, section "Verification image".
#
# Base: .NET SDK 10.0.404 on Ubuntu 24.04 (noble), pinned by digest.
FROM mcr.microsoft.com/dotnet/sdk:10.0.404-noble@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb

ARG PLAYWRIGHT_VERSION
RUN test -n "$PLAYWRIGHT_VERSION" || (echo "PLAYWRIGHT_VERSION build argument is required" && exit 1)

ENV PLAYWRIGHT_BROWSERS_PATH=/ms-playwright \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    DOTNET_NOLOGO=1 \
    AUTOMATAFLOW_VERIFY_PLAYWRIGHT_VERSION=${PLAYWRIGHT_VERSION}

# Install Chromium (exact build determined by the Playwright version) plus its OS libraries, using the
# Playwright CLI shipped inside the pinned NuGet package. The helper project is removed afterwards.
COPY installer/ /opt/pw-installer/
RUN cd /opt/pw-installer \
 && dotnet build -c Release -p:PlaywrightVersion=${PLAYWRIGHT_VERSION} -o /opt/pw-installer/out \
 && dotnet /opt/pw-installer/out/PlaywrightInstaller.dll install --with-deps chromium \
 && chmod -R a+rX /ms-playwright \
 && { echo "playwright=${PLAYWRIGHT_VERSION}"; \
      for b in $(find /ms-playwright -type f \( -name chrome -o -name chrome-headless-shell -o -name headless_shell \)); do \
        echo "$b=$($b --version 2>/dev/null | tr -d '\n')"; done; } > /ms-playwright/VERSIONS \
 && cat /ms-playwright/VERSIONS \
 && rm -rf /opt/pw-installer /root/.nuget/packages /tmp/*

# Tests run as an unprivileged user (needed for the Chromium sandbox; never run browsers as root).
RUN useradd --create-home --uid 10001 --shell /bin/bash verify
USER verify
WORKDIR /home/verify
