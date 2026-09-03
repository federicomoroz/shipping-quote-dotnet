# Build multi-stage: la imagen final lleva el runtime, no el SDK.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Los .csproj primero y el restore antes del resto del codigo: mientras no
# cambien las dependencias, Docker reusa esa capa y no vuelve a bajar paquetes
# en cada build.
COPY Directory.Build.props ./
COPY src/ShippingQuote.Domain/*.csproj src/ShippingQuote.Domain/
COPY src/ShippingQuote.Application/*.csproj src/ShippingQuote.Application/
COPY src/ShippingQuote.Infrastructure/*.csproj src/ShippingQuote.Infrastructure/
COPY src/ShippingQuote.Api/*.csproj src/ShippingQuote.Api/
RUN dotnet restore src/ShippingQuote.Api/ShippingQuote.Api.csproj

COPY src/ src/
RUN dotnet publish src/ShippingQuote.Api/ShippingQuote.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0
WORKDIR /app
COPY --from=build /app .

# Usuario sin privilegios: si alguien escapa del proceso, no es root.
USER $APP_UID

EXPOSE 8080
ENTRYPOINT ["dotnet", "ShippingQuote.Api.dll"]
