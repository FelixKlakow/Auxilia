FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY Source/Platform/Auxilia.DockerGuard/ Source/Platform/Auxilia.DockerGuard/
RUN dotnet publish Source/Platform/Auxilia.DockerGuard/Auxilia.DockerGuard.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# The guard alone mounts the daemon socket (root-owned), so it runs as root. It only speaks HTTP
# (to the socket, and to the runner on a private network): deploy it with --read-only,
# --tmpfs /tmp, --cap-drop ALL and no-new-privileges, reachable by the runner alone.
ENV ASPNETCORE_URLS=http://+:2375
EXPOSE 2375
ENTRYPOINT ["dotnet", "Auxilia.DockerGuard.dll"]
