# Dockerfile — /notification (in-house notification worker, ADR-IC-011).
# Runtime: .NET (stack-coherent with the engine) per ADR-IC-011; placement by ADR-IC-013.
# A backbone consumer/worker (a per-service outbox worker, ADR-IC-004), not a public HTTP
# API, so the runtime base (not aspnet) is the target.
#
# Build context is the repo root (the central Directory.Packages.props pins versions there),
# so the publish targets the project explicitly — exactly the orchestrator/engine pattern.
# A bare `dotnet restore`/`dotnet publish` with no project arg is ambiguous (~35 .csproj
# repo-wide, no root .sln/.slnx), so both restore and publish name the .csproj. Build with:
#   docker build -f notification/Dockerfile -t babelstone-notification .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore notification/src/Babelstone.Notification/Babelstone.Notification.csproj
RUN dotnet publish notification/src/Babelstone.Notification/Babelstone.Notification.csproj \
    -c Release -o /app --no-restore

# Chiseled (distroless-style) runtime base — no shell / perl / ncurses / tar / full util-linux,
# where the base-image OS CVEs grype flags (ADR-IC-014 §S2) live; `-extra` keeps tzdata + ICU
# for the worker's date/culture-sensitive read-model work. Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.Notification.dll"]
