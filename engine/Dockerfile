FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY Core/Core.csproj Core/
COPY Engine/Engine.csproj Engine/
RUN dotnet restore Engine/Engine.csproj
COPY Core/ Core/
COPY Engine/ Engine/
RUN dotnet publish Engine/Engine.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Engine.dll"]
