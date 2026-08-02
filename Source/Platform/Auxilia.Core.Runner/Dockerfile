FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Platform/Auxilia.Core.Runner/Auxilia.Core.Runner.csproj \
    -c Release -o /app/publish --no-self-contained
# Bake the credential-probe slot handler onto the runner filesystem. The runner copies the
# *.slothandler.dll into the workflow container at launch (the workflow image already carries
# Auxilia.Workflows.Testing, so the plugin's ICredentialProbe reference resolves there).
RUN dotnet publish Source/Slots/Auxilia.Slots.CredentialProbe/Auxilia.Slots.CredentialProbe.csproj \
    -c Release -o /app/slots --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git: the Workspace Manager clones per-run repository workspaces (ARCHITECTURE §9).
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
COPY --from=build /app/slots /app/slots
ENTRYPOINT ["dotnet", "Auxilia.Core.Runner.dll"]
