FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Source/Auxilia.SessionNotifier.Workflow/Auxilia.SessionNotifier.Workflow.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
# The base image ships no locale (POSIX/C = ASCII); slot plugins may spawn agent CLIs in any
# workflow container, and without a UTF-8 locale they mangle every non-ASCII character.
ENV LANG=C.UTF-8 LC_ALL=C.UTF-8
ENTRYPOINT ["dotnet", "Auxilia.SessionNotifier.Workflow.dll"]
