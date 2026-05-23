# Skeleton Dockerfile — /acl (anti-corruption layer service).
# Runtime: .NET (stack-coherent with the engine; resilience via Polly per ADR-IC-012);
# placement by ADR-IC-013. Consumes the backbone + calls the Core outbound; the
# runtime base is the target. Source + ENTRYPOINT land with the ACL build task.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
# ENTRYPOINT ["dotnet", "Babelstone.Acl.dll"]   # set with the ACL build task
