# The primary deployment path is a code deploy to Azure App Service. This file is here so the
# same build can be run locally in a container when that is easier. See docs/azure-deployment.md.

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Restore first so a source only change does not re-download every package.
COPY Directory.Build.props Directory.Packages.props SocialShare.sln ./
COPY src/SocialShare.Core/SocialShare.Core.csproj src/SocialShare.Core/
COPY src/SocialShare.Data/SocialShare.Data.csproj src/SocialShare.Data/
COPY src/SocialShare.Platforms/SocialShare.Platforms.csproj src/SocialShare.Platforms/
COPY src/SocialShare.Web/SocialShare.Web.csproj src/SocialShare.Web/
COPY tests/SocialShare.Tests/SocialShare.Tests.csproj tests/SocialShare.Tests/
RUN dotnet restore SocialShare.sln

COPY . .
RUN dotnet publish src/SocialShare.Web/SocialShare.Web.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .

# Matches the Azure layout, so the same configuration works in both places.
RUN mkdir -p /home/data/uploads /home/data/keys
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ConnectionStrings__Default="Data Source=/home/data/socialshare.db" \
    Storage__ImageRoot=/home/data/uploads \
    DataProtection__KeyRingPath=/home/data/keys

EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD ["/bin/sh", "-c", "wget -q -O - http://localhost:8080/health || exit 1"]

ENTRYPOINT ["dotnet", "SocialShare.Web.dll"]
