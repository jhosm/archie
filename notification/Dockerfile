# Skeleton Dockerfile — /notification (async saga-completion notification service).
# Runtime: .NET (stack-coherent with the engine) per ADR-IC-011; placement by ADR-IC-013.
# A backbone consumer/worker; the runtime base is the target.
# Source + ENTRYPOINT land with the notification build task.

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
# ENTRYPOINT ["dotnet", "Babelstone.Notification.dll"]   # set with the build task
