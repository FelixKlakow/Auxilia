FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.CodingSession.Workflow/Auxilia.CodingSession.Workflow.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Session tooling: the real Claude Code CLI, tmux (the session survives browser
# disconnects), ttyd (the web terminal the dashboard proxies to), and git.
# This layer never changes with source edits, so Docker caches it across rebuilds.
RUN apt-get update \
 && apt-get install -y --no-install-recommends curl ca-certificates git tmux \
 && rm -rf /var/lib/apt/lists/* \
 && curl -fsSL https://claude.ai/install.sh | bash \
 && ln -s /root/.local/bin/claude /usr/local/bin/claude \
 && curl -fsSL -o /usr/local/bin/ttyd \
      https://github.com/tsl0922/ttyd/releases/download/1.7.7/ttyd.x86_64 \
 && chmod +x /usr/local/bin/ttyd
WORKDIR /app
COPY --from=build /app/publish .
# Stand-in CLI for system tests (cost rule: never real AI in system tests). The sed strips
# CR characters in case the file was checked out with Windows line endings.
COPY Source/Auxilia.CodingSession.Workflow/claude-session-stub.sh /usr/local/bin/claude-session-stub
RUN sed -i 's/\r$//' /usr/local/bin/claude-session-stub && chmod +x /usr/local/bin/claude-session-stub
EXPOSE 7681
ENTRYPOINT ["dotnet", "Auxilia.CodingSession.Workflow.dll"]
