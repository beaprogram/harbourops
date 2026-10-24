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

# ---- 3. Runtime: small, non-root, no shell or package manager ----
FROM mcr.microsoft.com/dotnet/aspnet:10.0.13-noble-chiseled AS runtime
WORKDIR /app
COPY --from=backend /app ./
COPY --from=frontend /src/frontend/dist ./wwwroot
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true
EXPOSE 8080
USER app
ENTRYPOINT ["dotnet", "HarbourOps.Api.dll"]
