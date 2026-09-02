# syntax=docker/dockerfile:1
#
# Production image (DEPLOY-2, ADR-017). Single container that serves BOTH the ASP.NET Core API and the
# Blazor WASM client — single-origin (see DEPLOY-1). Mirrors CI: restore/build only Api + Web (which pull
# in Core/Infrastructure/Shared.Ui via ProjectReference); Maui is never built here.

# ---- build ---------------------------------------------------------------------------------------
# Pin the SDK to the single source of truth — global.json (10.0.400) — which generated the committed
# packages.lock.json files. The WASM SDK injects patch-sensitive implicit package refs, so a floating tag
# breaks --locked-mode. Keep this tag == global.json's version (see the bump-together playbook in CLAUDE.md).
FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src

# Solution-wide build config (warnings-as-error) + Central Package Management + the committed lockfiles.
COPY Directory.Build.props Directory.Packages.props ./

# Project manifests + their lockfiles first, so `dotnet restore` caches as its own layer and only re-runs
# when a dependency actually changes (not on every source edit).
COPY src/Core/Perezosoft.Core.csproj src/Core/packages.lock.json src/Core/
COPY src/Infrastructure/Perezosoft.Infrastructure.csproj src/Infrastructure/packages.lock.json src/Infrastructure/
COPY src/Api/Perezosoft.Api.csproj src/Api/packages.lock.json src/Api/
COPY src/Shared.Ui/Perezosoft.Shared.Ui.csproj src/Shared.Ui/packages.lock.json src/Shared.Ui/
COPY src/Web/Perezosoft.Web.csproj src/Web/packages.lock.json src/Web/

# Restore the two entry projects. The API side restores in LOCKED mode (same guarantee as CI). The WASM
# project is restored without locked-mode: the Blazor SDK injects an implicit, SDK-patch-specific package
# (Microsoft.AspNetCore.App.Internal.Assets) whose presence differs between the SDK that wrote the lockfile
# and the base image, which would trip --locked-mode on Web alone. CI (build-test job) is the authoritative
# lockfile gate for Web; here we just need a reproducible publish.
RUN dotnet restore src/Api/Perezosoft.Api.csproj --locked-mode \
 && dotnet restore src/Web/Perezosoft.Web.csproj

# Now the sources.
COPY src/ src/

# Publish the WASM client and the API, then fold the client's static output into the API's wwwroot so the
# API serves it single-origin. Overwrite the client's appsettings.json so no baked-in ApiBaseUrl remains —
# with it absent the WASM defaults to its own origin (DEPLOY-1), which is exactly the single-origin case.
RUN dotnet publish src/Web/Perezosoft.Web.csproj -c Release --no-restore -o /publish/web \
 && dotnet publish src/Api/Perezosoft.Api.csproj -c Release --no-restore -o /publish/api \
 && mkdir -p /publish/api/wwwroot \
 && cp -r /publish/web/wwwroot/. /publish/api/wwwroot/ \
 && echo '{}' > /publish/api/wwwroot/appsettings.json

# ---- runtime -------------------------------------------------------------------------------------
# Pin the runtime to the exact patch of the ASP.NET Core package line the app compiles against (10.0.11,
# enforced by the R61 gate against Directory.Packages.props), not the
# floating :10.0 tag — a reproducible runtime layer, same discipline as the build stage (v3 audit DEP-11).
FROM mcr.microsoft.com/dotnet/aspnet:10.0.11 AS runtime
WORKDIR /app

# curl for the container/compose health probe. The .NET image already ships a non-root `app` user; run as it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/*
USER app

COPY --from=build --chown=app:app /publish/api ./

# Serve the bundled WASM single-origin. The listen port defaults to 8080 (local compose) but honors $PORT
# when the platform provides one (Render sets it), via the entrypoint below.
ENV Hosting__ServeWebClient=true
EXPOSE 8080

# Liveness only (readiness needs the DB and is checked by the platform against /health/ready).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS "http://localhost:${PORT:-8080}/health" || exit 1

# exec so dotnet is PID 1 (receives SIGTERM for graceful shutdown). Bind $PORT (default 8080).
ENTRYPOINT ["sh", "-c", "exec dotnet Perezosoft.Api.dll --urls http://0.0.0.0:${PORT:-8080}"]
