# Dockerfile — /orchestrator (in-house saga orchestrator, ADR-IC-003).
# Runtime: .NET, per ADR-IC-003's decisive S2 reason — the orchestrator "speaks the
# same language as every other service in the stack" (the .NET engine, ADR-PC-010);
# placement by ADR-IC-013. A Redpanda consumer/worker, not a public HTTP API, so the
# runtime base (not aspnet) is the target.
#
# Build context is the repo root (the central Directory.Packages.props pins versions there),
# so the publish targets the project explicitly: build with
#   docker build -f orchestrator/Dockerfile -t babelstone-orchestrator .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore orchestrator/src/Babelstone.Orchestrator/Babelstone.Orchestrator.csproj
RUN dotnet publish orchestrator/src/Babelstone.Orchestrator/Babelstone.Orchestrator.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.Orchestrator.dll"]
