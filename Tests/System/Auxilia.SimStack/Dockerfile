FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /repo
COPY . .
RUN dotnet publish Tests/System/Auxilia.SimStack/Auxilia.SimStack.csproj \
    -c Release -o /app/publish --no-self-contained

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV LANG=C.UTF-8 LC_ALL=C.UTF-8
ENTRYPOINT ["dotnet", "Auxilia.SimStack.dll"]
