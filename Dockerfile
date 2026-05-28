FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build

WORKDIR /src

COPY src/DsEap/DsEap.csproj src/DsEap/
COPY config/appsettings.json config/appsettings.json
RUN dotnet restore src/DsEap/DsEap.csproj

COPY src/DsEap src/DsEap
RUN dotnet publish src/DsEap/DsEap.csproj \
    -c Release \
    -o /app/publish \
    /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/runtime:8.0 AS runtime

WORKDIR /app
ENV DOTNET_EnableDiagnostics=0

COPY --from=build /app/publish ./

ENTRYPOINT ["dotnet", "DsEap.dll"]
