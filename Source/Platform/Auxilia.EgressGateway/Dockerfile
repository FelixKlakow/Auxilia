FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Source/Platform/Auxilia.EgressGateway/ Source/Platform/Auxilia.EgressGateway/
RUN dotnet publish Source/Platform/Auxilia.EgressGateway/Auxilia.EgressGateway.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/runtime:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# Unprivileged: the gateway binds high ports only and needs no capability at all.
USER app
EXPOSE 3128
ENTRYPOINT ["dotnet", "Auxilia.EgressGateway.dll"]
