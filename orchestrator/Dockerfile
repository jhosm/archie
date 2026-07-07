# Dockerfile — /orchestrator (in-house saga orchestrator, ADR-IC-003).
# Runtime: .NET, per ADR-IC-003's decisive S2 reason — the orchestrator "speaks the
# same language as every other service in the stack" (the .NET engine, ADR-PC-010);
# placement by ADR-IC-013. It is both a Redpanda consumer/worker AND — since the project
# moved to Microsoft.NET.Sdk.Web for the I.1 edge HTTP surface (the 202 + SSE front door,
# ADR-IC-006 §P4) — a Kestrel HTTP host, so its runtimeconfig.json takes a FrameworkReference
# on Microsoft.AspNetCore.App: the aspnet base (as the engine uses), NOT the plain runtime
# base, which ships only Microsoft.NETCore.App and fails with "No frameworks were found".
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

# Chiseled (distroless-style) ASP.NET Core runtime base: no shell / perl / ncurses / tar / full
# util-linux, where the base-image OS CVEs the image-build.yml grype scan flags (ADR-IC-014 §S2)
# live. The `-extra` variant keeps tzdata + ICU/globalization for the saga's date/culture-sensitive
# work. `aspnet` (not `runtime`) because Microsoft.NET.Sdk.Web takes a FrameworkReference on the
# ASP.NET Core shared framework this base ships. Runs as non-root (UID 1654) by default.
FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.Orchestrator.dll"]
