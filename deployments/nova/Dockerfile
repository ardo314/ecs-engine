FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY NovaInstaller/ NovaInstaller/
RUN dotnet publish NovaInstaller/NovaInstaller.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "NovaInstaller.dll"]
