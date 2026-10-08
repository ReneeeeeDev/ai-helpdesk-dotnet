# syntax=docker/dockerfile:1
# Construir desde la raíz del repositorio:  docker build -f src/SupportAI.Web/Dockerfile -t supportai-web .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
ENV HUSKY=0 DOTNET_CLI_TELEMETRY_OPTOUT=1

COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/SupportAI.Contracts/SupportAI.Contracts.csproj src/SupportAI.Contracts/
COPY src/SupportAI.Web/SupportAI.Web.csproj src/SupportAI.Web/
RUN dotnet restore src/SupportAI.Web/SupportAI.Web.csproj

COPY src/SupportAI.Contracts/ src/SupportAI.Contracts/
COPY src/SupportAI.Web/ src/SupportAI.Web/
RUN dotnet publish src/SupportAI.Web/SupportAI.Web.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 DOTNET_RUNNING_IN_CONTAINER=true
COPY --from=build /app/publish .
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SupportAI.Web.dll"]
