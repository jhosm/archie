# Skeleton Dockerfile — /notification (async saga-completion notification service).
# Runtime: .NET (stack-coherent with the engine) per ADR-IC-011; placement by ADR-IC-013.
# A backbone consumer/worker; the runtime base is the target.
# Source + ENTRYPOINT land with the notification build task.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore
RUN dotnet publish -c Release -o /app --no-restore

# Chiseled (distroless-style) runtime base — no shell / perl / ncurses / tar / full util-linux,
# where the base-image OS CVEs grype flags (ADR-IC-014 §S2) live; `-extra` keeps tzdata + ICU.
# Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/runtime:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
# ENTRYPOINT ["dotnet", "Babelstone.Notification.dll"]   # set with the build task
