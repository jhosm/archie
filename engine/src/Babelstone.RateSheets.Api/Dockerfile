# Base image for the RateSheets.Api deploy host (bd babelstone-zla1.21). The pack-agnostic base;
# the pack + committed rate sheet are baked into the derived rate-sheet-deploy image
# (infra/k8s/apps/rate-sheet-deploy/Dockerfile). Mirrors orchestrator/Dockerfile: restore + publish
# the project with the REPO ROOT as build context, then a chiseled aspnet runtime.
#
# The service is normally an HTTP deploy host (POST /v1/rate-sheets); in the staging Job it runs in
# one-shot mode (RateSheets:DeployOnStartup) — deploy the file, then exit. Both share this image.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet restore engine/src/Babelstone.RateSheets.Api/Babelstone.RateSheets.Api.csproj
RUN dotnet publish engine/src/Babelstone.RateSheets.Api/Babelstone.RateSheets.Api.csproj \
    -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled-extra
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Babelstone.RateSheets.Api.dll"]
