FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Tests/System/Auxilia.Workflows.Testing/Auxilia.Workflows.Testing.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
# Install git — required by SimpleGitCommitWorkflow and any future git-based dummy workflows.
RUN apt-get update && apt-get install -y --no-install-recommends git && rm -rf /var/lib/apt/lists/*
# The unprivileged identity agent processes run as (AgentSandbox, uid/gid 10001): workflow
# containers run on a read-only root filesystem, so it is provisioned here, at build time.
RUN groupadd --gid 10001 auxilia-agent \
 && useradd --uid 10001 --gid 10001 --create-home --home-dir /home/auxilia-agent --shell /bin/bash auxilia-agent
WORKDIR /app
COPY --from=build /app/publish .
# The base image ships no locale (POSIX/C = ASCII); slot plugins may spawn agent CLIs in any
# workflow container, and without a UTF-8 locale they mangle every non-ASCII character.
ENV LANG=C.UTF-8 LC_ALL=C.UTF-8
ENTRYPOINT ["dotnet", "Auxilia.Workflows.Testing.dll"]

