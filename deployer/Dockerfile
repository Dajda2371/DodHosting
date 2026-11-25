# Build context is the repo root (see docker-compose.yml) so the deploy scripts can be bundled.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY deployer/Deployer.csproj deployer/
RUN dotnet restore deployer/Deployer.csproj
COPY deployer/ deployer/
RUN dotnet publish deployer/Deployer.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .
COPY scripts/deploy.sh scripts/deploy.ps1 ./scripts/
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
# Runs as root on purpose: it writes into the shared /sites volume and makes data/ writable for PHP.
ENTRYPOINT ["dotnet", "Deployer.dll"]
