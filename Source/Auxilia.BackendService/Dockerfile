FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.BackendService/Auxilia.BackendService.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# System.DirectoryServices.Protocols (LDAP identity import) needs the native OpenLDAP client
# library on Linux; the package name differs between Debian releases.
RUN apt-get update \
    && (apt-get install -y --no-install-recommends libldap2 \
        || apt-get install -y --no-install-recommends libldap-2.5-0) \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Auxilia.BackendService.dll"]
