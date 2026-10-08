# syntax=docker/dockerfile:1
# Construir desde la raíz del repositorio:  docker build -f src/SupportAI.Api/Dockerfile -t supportai-api .

FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
ENV HUSKY=0 DOTNET_CLI_TELEMETRY_OPTOUT=1

# Restauración en una capa propia (se cachea mientras no cambien los .csproj / props).
COPY global.json Directory.Build.props Directory.Build.targets Directory.Packages.props .editorconfig ./
COPY src/SupportAI.Domain/SupportAI.Domain.csproj src/SupportAI.Domain/
COPY src/SupportAI.Contracts/SupportAI.Contracts.csproj src/SupportAI.Contracts/
COPY src/SupportAI.Application/SupportAI.Application.csproj src/SupportAI.Application/
COPY src/SupportAI.Infrastructure/SupportAI.Infrastructure.csproj src/SupportAI.Infrastructure/
COPY src/SupportAI.Api/SupportAI.Api.csproj src/SupportAI.Api/
RUN dotnet restore src/SupportAI.Api/SupportAI.Api.csproj

COPY src/ src/
RUN dotnet publish src/SupportAI.Api/SupportAI.Api.csproj -c Release -o /app/publish --no-restore /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0 AS runtime
WORKDIR /app
ENV ASPNETCORE_HTTP_PORTS=8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Workers__Enabled=true
COPY --from=build /app/publish .
# Usuario sin privilegios incluido en las imágenes oficiales de .NET 8+.
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "SupportAI.Api.dll"]
