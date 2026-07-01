FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.ClaudeCode.Workflow/Auxilia.ClaudeCode.Workflow.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# The real Claude Code CLI (native build, no Node required) plus git for repository work.
# This layer never changes with source edits, so Docker caches it across rebuilds.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates git \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL https://claude.ai/install.sh | bash \
 && ln -s /root/.local/bin/claude /usr/local/bin/claude
WORKDIR /app
COPY --from=build /app/publish .
# Stand-in CLI for system tests (cost rule: never real AI in system tests). The sed strips
# CR characters in case the file was checked out with Windows line endings.
COPY Source/Auxilia.ClaudeCode.Workflow/claude-stub.sh /usr/local/bin/claude-stub
RUN sed -i 's/\r$//' /usr/local/bin/claude-stub && chmod +x /usr/local/bin/claude-stub
ENTRYPOINT ["dotnet", "Auxilia.ClaudeCode.Workflow.dll"]
