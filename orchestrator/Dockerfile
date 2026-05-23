# Skeleton Dockerfile — /orchestrator (in-house saga orchestrator).
# Runtime: .NET, per ADR-IC-003's decisive S2 reason — the orchestrator "speaks the
# same language as every other service in the stack" (the .NET engine, ADR-PC-010);
# placement by ADR-IC-013. A Redpanda consumer/worker, not a public HTTP API, so the
# runtime base (not aspnet) is the target. Source + ENTRYPOINT land with the build task.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
# ENTRYPOINT ["dotnet", "Babelstone.Orchestrator.dll"]   # set with the build task
