# Imagem da notifications-function sobre a base oficial de Azure Functions (host + isolated worker).
# Tag 4-dotnet-isolated8.0: host v4, worker net8.0.

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Directory.Build.props (InvariantGlobalization=false) e global.json valem para todo o build.
COPY Directory.Build.props global.json ./
COPY src/Fcg.Notifications.Function/*.csproj src/Fcg.Notifications.Function/
RUN dotnet restore src/Fcg.Notifications.Function/Fcg.Notifications.Function.csproj

COPY src/ src/
# `publish` gera o layout que o host espera (functions.metadata, worker.config.json, extensions.json).
RUN dotnet publish src/Fcg.Notifications.Function/Fcg.Notifications.Function.csproj \
    -c Release -o /app/publish

FROM mcr.microsoft.com/azure-functions/dotnet-isolated:4-dotnet-isolated8.0 AS final

# Logs no stdout (lidos por `docker logs` / `kubectl logs`) e ICU obrigatório para o pt-BR dos e-mails.
ENV AzureWebJobsScriptRoot=/home/site/wwwroot \
    AzureFunctionsJobHost__Logging__Console__IsEnabled=true \
    DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=false

COPY --from=build /app/publish /home/site/wwwroot
