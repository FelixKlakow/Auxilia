FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.Copilot.Workflow/Auxilia.Copilot.Workflow.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The GitHub Copilot CLI (Node-based) plus git for repository work. This layer never
# changes with source edits, so Docker caches it across rebuilds.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates git gnupg \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g @github/copilot \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# Stand-in CLI for system tests (cost rule: never real AI in system tests). The sed strips
# CR characters in case the file was checked out with Windows line endings.
COPY Source/Auxilia.Copilot.Workflow/copilot-stub.sh /usr/local/bin/copilot-stub
RUN sed -i 's/\r$//' /usr/local/bin/copilot-stub && chmod +x /usr/local/bin/copilot-stub
ENTRYPOINT ["dotnet", "Auxilia.Copilot.Workflow.dll"]
