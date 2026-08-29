FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
ARG ECS_IMAGE_TAG=latest
WORKDIR /src
COPY NovaInstaller/ NovaInstaller/
RUN dotnet publish NovaInstaller/NovaInstaller.csproj -c Release -o /app -p:ImageTag="$ECS_IMAGE_TAG"

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "NovaInstaller.dll"]
