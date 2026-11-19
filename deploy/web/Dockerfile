# AutomataFlow dashboard image: the static React build served by unprivileged nginx, which also forwards /api to the
# Api so the browser talks to ONE origin (ADR-076: the Api has no CORS). TLS is terminated in front of this container
# (docs/DEPLOYMENT.md); the refresh cookie is Secure, so browsers only keep it over HTTPS (or on localhost).
# Build from the repository root:
#   docker build -f deploy/web/Dockerfile -t automataflow-web .
FROM node:24-bookworm-slim@sha256:dc26ae92c9fb15cf6b1d63e20f0944b2efe0f115b20e00be48fb23fceb061c54 AS build
WORKDIR /web
COPY web/automataflow-web/package.json web/automataflow-web/package-lock.json ./
RUN npm ci --no-audit --no-fund --loglevel=error
COPY web/automataflow-web/ ./
RUN npm run build

FROM nginxinc/nginx-unprivileged:1.29-alpine@sha256:0c79d56aee561a1d81c63f00eee5fb5fe29279560cdc55e91425133104c7fbe6
COPY --chown=0:0 deploy/web/nginx.conf.template /etc/nginx/templates/default.conf.template
COPY --from=build --chown=0:0 /web/dist /usr/share/nginx/html
# The Api's address inside the deployment network (envsubst at start).
ENV AF_API_UPSTREAM=http://api:8080
EXPOSE 8080
USER 101
HEALTHCHECK --interval=15s --timeout=5s --retries=3 CMD ["wget", "-q", "-O", "/dev/null", "http://127.0.0.1:8080/healthz"]
