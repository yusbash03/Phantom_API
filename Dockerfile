# PHANTOM API: production image (design §13). Build: docker build -t phantom-api .
# Contains the app and an EF Core migrations bundle (/app/efbundle) that the platform runs as a release
# step before the new version starts, so migrations never run on ordinary app boot in production.

FROM mcr.microsoft.com/dotnet/sdk:10.0.400 AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props PHANTOM.sln .editorconfig ./
COPY src/PHANTOM.Domain/PHANTOM.Domain.csproj src/PHANTOM.Domain/
COPY src/PHANTOM.Application/PHANTOM.Application.csproj src/PHANTOM.Application/
COPY src/PHANTOM.Infrastructure/PHANTOM.Infrastructure.csproj src/PHANTOM.Infrastructure/
COPY src/PHANTOM.API/PHANTOM.API.csproj src/PHANTOM.API/
RUN dotnet restore src/PHANTOM.API/PHANTOM.API.csproj

COPY src/ src/
# The OpenAPI contract is generated during development builds and committed; the image does not need it.
RUN dotnet publish src/PHANTOM.API/PHANTOM.API.csproj -c Release -o /app --no-restore -p:OpenApiGenerateDocuments=false

RUN dotnet tool install --tool-path /tools dotnet-ef --version 10.0.15
RUN /tools/dotnet-ef migrations bundle \
      --project src/PHANTOM.Infrastructure/PHANTOM.Infrastructure.csproj \
      --startup-project src/PHANTOM.API/PHANTOM.API.csproj \
      --configuration Release --output /app/efbundle --force

FROM mcr.microsoft.com/dotnet/aspnet:10.0.15 AS runtime
WORKDIR /app
COPY --from=build /app ./
ENV ASPNETCORE_HTTP_PORTS=8080 \
    ASPNETCORE_ENVIRONMENT=Production \
    DOTNET_gcServer=0
EXPOSE 8080
# The base image's unprivileged user; the app never needs root.
USER $APP_UID
ENTRYPOINT ["dotnet", "PHANTOM.API.dll"]
