FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY proto/gen/csharp/Ecs.Protos.csproj proto/gen/csharp/
COPY engine/Engine/Engine.csproj engine/Engine/
RUN dotnet restore engine/Engine/Engine.csproj
COPY proto/gen/csharp/ proto/gen/csharp/
COPY engine/Engine/ engine/Engine/
RUN dotnet publish engine/Engine/Engine.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Engine.dll"]
