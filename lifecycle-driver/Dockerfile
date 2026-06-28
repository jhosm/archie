# Dockerfile — /lifecycle-driver (in-house lifecycle-command driver worker, ADR-PC-036 §Decision 2 / ADR-IC-011).
# Runtime: .NET (stack-coherent with the engine) per ADR-IC-011; placement by ADR-IC-013.
# A clock-owning poll-loop worker (the same BackgroundService shape as the notification scheduler and the
# engine's outbox relay), not a public HTTP API, so the runtime base (not aspnet) is the target.
#
# Build context is the repo root (the central Directory.Packages.props pins versions there), so the publish
# targets the HOST project explicitly — exactly the notification/orchestrator/engine pattern. A bare
# `dotnet restore`/`dotnet publish` with no project arg is ambiguous (many .csproj repo-wide, no root
# .sln/.slnx at the repo root), so both restore and publish name the .csproj. Building the HOST transitively
# builds the Babelstone.Lifecycle core + the Babelstone.Cadence machinery + the Babelstone.Engine.Hosting seam
# (for the LifecycleCommandKey it references). Build with:
#   docker build -f lifecycle-driver/Dockerfile -t babelstone-lifecycle-driver .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore lifecycle-driver/src/Babelstone.Lifecycle.Host/Babelstone.Lifecycle.Host.csproj
RUN dotnet publish lifecycle-driver/src/Babelstone.Lifecycle.Host/Babelstone.Lifecycle.Host.csproj \
    -c Release -o /app --no-restore

# Chiseled (distroless-style) runtime base — no shell / perl / ncurses / tar / full util-linux, where the
# base-image OS CVEs grype flags (ADR-IC-014 §S2) live; `-extra` keeps tzdata + ICU for the worker's
# date/culture-sensitive calendar work. Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.Lifecycle.Host.dll"]
