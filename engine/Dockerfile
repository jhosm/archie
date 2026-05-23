# Skeleton Dockerfile — /engine (C# .NET 9 single deployable).
# Runtime pinned by ADR-PC-010 (hand-rolled .NET 9 engine) + ADR-PC-001 (PostgreSQL).
# Source (.csproj, src/) and the ENTRYPOINT land with the engine build task; the
# multi-stage shape below is the target and builds once that source is present.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
# ENTRYPOINT ["dotnet", "Babelstone.Engine.dll"]   # set with the engine build task
