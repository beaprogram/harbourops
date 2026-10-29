# syntax=docker/dockerfile:1
# Production image: the React build is served by ASP.NET Core from the same origin as the API.

# ---- 1. Build the frontend ----
FROM node:24.22.1-alpine AS frontend
WORKDIR /src/frontend
COPY frontend/package.json frontend/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY frontend/ ./
RUN npm run build

# ---- 2. Build and publish the API ----
FROM mcr.microsoft.com/dotnet/sdk:10.0.402 AS backend
WORKDIR /src
COPY global.json ./
COPY backend/Directory.Build.props backend/Directory.Packages.props backend/
COPY backend/HarbourOps.Api/HarbourOps.Api.csproj backend/HarbourOps.Api/packages.lock.json backend/HarbourOps.Api/
RUN dotnet restore backend/HarbourOps.Api/HarbourOps.Api.csproj --locked-mode
COPY backend/HarbourOps.Api/ backend/HarbourOps.Api/
RUN dotnet publish backend/HarbourOps.Api/HarbourOps.Api.csproj -c Release -o /app --no-restore /p:UseAppHost=false
# An empty directory for photo storage; the runtime stage has no shell to create it.
RUN mkdir -p /photo-storage
# Licence notices for redistributed native code (Skia and its codecs), shipped inside the image.
# Apache.Arrow (Apache-2.0, pulled in by Azure.Storage.Blobs) ships its licence the same way. See THIRD-PARTY-NOTICES.md.
RUN mkdir -p /app/licenses \
 && cp /root/.nuget/packages/skiasharp/*/LICENSE.txt /app/licenses/SkiaSharp-LICENSE.txt \
 && cp /root/.nuget/packages/skiasharp.nativeassets.linux.nodependencies/*/THIRD-PARTY-NOTICES.txt /app/licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt \
 && cp /root/.nuget/packages/apache.arrow/*/LICENSE.txt /app/licenses/Apache.Arrow-LICENSE.txt

# ---- 3. Runtime: non-root, no shell or package manager ----
# "-extra" adds time-zone data (tzdata) and ICU, needed to write America/Halifax times in CSV exports.
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled-extra AS runtime
# The commit being built (the deploy workflow passes the Git SHA); reported by /health/version.
ARG SOURCE_VERSION=local
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /src/frontend/dist ./wwwroot
# Public Supabase CA used by SSL Mode=VerifyFull; no private keys are included.
COPY infra/supabase/supabase-root-2021-ca.crt ./certs/supabase-root-2021-ca.crt
# Owned by the non-root "app" user (uid 1654). A new named volume mounted here inherits this ownership.
COPY --from=backend --chown=1654:1654 /photo-storage /var/lib/harbourops/attachments
# glibc allocator settings for SkiaSharp's large native pixel buffers. With the defaults, freed multi-megabyte
# buffers stay in per-thread arenas, so memory ratcheted up with every round of photo uploads (1.3 GB after
# 5 rounds in one measurement). These keep at most two arenas and return buffers of 1 MiB or more to the OS
# on free; the same load then peaked at 720 MiB and fell back to about 100 MiB after every round.
# Measurements and method: docs/benchmarks/photo-memory/README.md.
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    HARBOUROPS_VERSION=$SOURCE_VERSION \
    MALLOC_ARENA_MAX=2 \
    MALLOC_MMAP_THRESHOLD_=1048576
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "HarbourOps.Api.dll"]

