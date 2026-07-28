FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.Implementation.Workflow/Auxilia.Implementation.Workflow.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# BOTH coding CLIs (author/reviewer are provider-configurable), git, tmux+ttyd for the
# driven author console. This layer never changes with source edits, so Docker caches it.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates git gnupg tmux \
 && curl -fsSL https://deb.nodesource.com/setup_22.x | bash - \
 && apt-get install -y --no-install-recommends nodejs \
 && npm install -g @github/copilot \
 && curl -fsSL https://claude.ai/install.sh | bash \
 && ln -s /root/.local/bin/claude /usr/local/bin/claude \
 && curl -fsSL -o /usr/local/bin/ttyd \
      https://github.com/tsl0922/ttyd/releases/download/1.7.7/ttyd.x86_64 \
 && chmod +x /usr/local/bin/ttyd \
 && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app/publish .
# Stand-in CLIs for system tests (cost rule: never real AI in system tests). The sed strips
# CR characters in case the files were checked out with Windows line endings.
COPY Source/Auxilia.ClaudeCode.Workflow/claude-stub.sh /usr/local/bin/claude-stub
COPY Source/Auxilia.Copilot.Workflow/copilot-stub.sh /usr/local/bin/copilot-stub
COPY Source/Auxilia.Implementation.Workflow/driven-stub.sh /usr/local/bin/driven-stub
RUN sed -i 's/\r$//' /usr/local/bin/claude-stub /usr/local/bin/copilot-stub /usr/local/bin/driven-stub \
 && chmod +x /usr/local/bin/claude-stub /usr/local/bin/copilot-stub /usr/local/bin/driven-stub
EXPOSE 7681
ENTRYPOINT ["dotnet", "Auxilia.Implementation.Workflow.dll"]
