FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Platform/Auxilia.Core.Runner/Auxilia.Core.Runner.csproj \
    -c Release -o /app/publish --no-self-contained

# The credential-probe slot handler is a TEST fake — only the system-test image carries it. The
# runner copies a *.slothandler.dll into the workflow container at launch (the test workflow
# image already carries Auxilia.Workflows.Testing, so the plugin's ICredentialProbe reference
# resolves there).
FROM build AS test-slots
RUN dotnet publish Source/Slots/Auxilia.Slots.CredentialProbe/Auxilia.Slots.CredentialProbe.csproj \
    -c Release -o /app/slots --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# git: the Workspace Manager clones per-run repository workspaces (ARCHITECTURE §9).
RUN apt-get update \
 && apt-get install -y --no-install-recommends git ca-certificates \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
ENTRYPOINT ["dotnet", "Auxilia.Core.Runner.dll"]

# What the system tests run (they build this target): the runner plus the test-fake slot.
FROM runtime AS system-test
COPY --from=test-slots /app/slots /app/slots

# The product image — the default target, the one that is published: no test plugin.
FROM runtime
