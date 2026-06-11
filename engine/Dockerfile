# Dockerfile — /engine (C# .NET 10 command/query host, Babelstone.Engine.Api).
# Runtime pinned by ADR-PC-010 (hand-rolled .NET 10 engine) + ADR-PC-001 (PostgreSQL).
#
# Build context is the repo root (the central Directory.Packages.props pins versions there,
# ManagePackageVersionsCentrally=true), so restore/publish target the host project explicitly —
# the same pattern orchestrator/Dockerfile uses. Build with:
#   docker build -f engine/Dockerfile -t babelstone-engine .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj
RUN dotnet publish engine/src/Babelstone.Engine.Api/Babelstone.Engine.Api.csproj \
    -c Release -o /app --no-restore

# Chiseled (distroless-style) runtime base: no shell / perl / ncurses / tar / full util-linux,
# which is where the base-image OS CVEs that the image-build.yml grype scan flags (ADR-IC-014 §S2)
# live. The `-extra` variant keeps tzdata + ICU/globalization — required for this engine's
# timezone- and culture-sensitive date/accrual math. Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.Engine.Api.dll"]
