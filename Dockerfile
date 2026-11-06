# Vuelto's production image (ADR-017): one container that serves the API and the Blazor WebAssembly pages from one
# origin. The platform comes as the Perezosoft.Platform.* packages from a private feed (GitHub Packages, ADR-034), so the
# restore needs a token: it arrives as the BuildKit secret `nuget` (the token itself, read:packages), mounted for the
# restore alone and in no layer. `docker build --secret id=nuget,env=PLATFORM_PACKAGES_TOKEN .` here; the platform's CI
# passes its job token; on Render it is the secret file named `nuget` (render.yaml).

# ---- build ---------------------------------------------------------------------------------------
# The SDK global.json pins: the WebAssembly SDK injects patch-sensitive implicit packages, so a floating tag breaks the
# lockfiles. Bump it with global.json (dotnet platform port brings both).
FROM mcr.microsoft.com/dotnet/sdk:10.0.403 AS build
WORKDIR /src

# What steers the restore, before it: the build settings (the Build package, a GlobalPackageReference), the package pins,
# the SDK pin, the feed, the local source switch (off in the image), and the two root files the Build package reads by
# name (the ports, the stamp).
COPY Directory.Build.props Directory.Build.targets Directory.Packages.props global.json nuget.config local-ports.props platform-stamp.json ./
COPY build/ build/

# The project files and their lockfiles (an app commits them after its first restore), so the restore is its own layer.
COPY src/Host/Vuelto.Host.csproj src/Host/packages.lock.jso[n] src/Host/
COPY src/Shared.Ui/Vuelto.Shared.Ui.csproj src/Shared.Ui/packages.lock.jso[n] src/Shared.Ui/
COPY src/Web/Vuelto.Web.csproj src/Web/packages.lock.jso[n] src/Web/

# The restore, with the feed's token as NuGet's per-source credential for this one command: the secret is mounted
# (not copied) and the variable lives only in this RUN, so neither reaches a layer. The host restores in locked mode
# once its lockfile is committed (as CI does); the WebAssembly host never does, because its SDK injects an implicit
# package whose version differs between the SDK that wrote the lockfile and this image.
RUN --mount=type=secret,id=nuget,required=true \
    NuGetPackageSourceCredentials_perezosoft="Username=token;Password=$(cat /run/secrets/nuget)" \
    sh -c 'dotnet restore src/Host/Vuelto.Host.csproj $([ -f src/Host/packages.lock.json ] && echo --locked-mode) \
        && dotnet restore src/Web/Vuelto.Web.csproj'

COPY src/ src/

# Publish the pages and the host, then fold the pages into the host's wwwroot so it serves them from its own origin.
# The pages' appsettings.json is emptied, so no API address is baked in: absent, the pages call their own origin.
RUN dotnet publish src/Web/Vuelto.Web.csproj -c Release --no-restore -o /publish/web \
 && dotnet publish src/Host/Vuelto.Host.csproj -c Release --no-restore -o /publish/api \
 && mkdir -p /publish/api/wwwroot \
 && cp -r /publish/web/wwwroot/. /publish/api/wwwroot/ \
 && echo '{}' > /publish/api/wwwroot/appsettings.json

# ---- runtime -------------------------------------------------------------------------------------
# The runtime pinned to the ASP.NET Core patch the packages compile against.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.14 AS runtime
WORKDIR /app

# curl for the health probe. The image's non-root `app` user runs the host; it owns the local-disk file storage folder
# (ADR-010's default, /app/storage), created before switching to it. On a free host the folder is ephemeral: set
# Storage:S3 for files that must survive a restart.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl \
 && rm -rf /var/lib/apt/lists/* \
 && mkdir -p /app/storage \
 && chown app:app /app/storage
USER app

COPY --from=build --chown=app:app /publish/api ./

ENV Hosting__ServeWebClient=true
EXPOSE 8080

# Liveness only (readiness needs the database; the host checks /health/ready).
HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
  CMD curl -fsS "http://localhost:${PORT:-8080}/health" || exit 1

# exec, so dotnet is PID 1 and gets SIGTERM; the port is $PORT where the host gives one (Render does), else 8080.
ENTRYPOINT ["sh", "-c", "exec dotnet Vuelto.Host.dll --urls http://0.0.0.0:${PORT:-8080}"]
