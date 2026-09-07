FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY proto/gen/csharp/Ecs.Protos.csproj proto/gen/csharp/
COPY clients/csharp/Client/Client.csproj clients/csharp/Client/
COPY examples/MovementSystem/MovementSystem.csproj examples/MovementSystem/
RUN dotnet restore examples/MovementSystem/MovementSystem.csproj
COPY proto/gen/csharp/ proto/gen/csharp/
COPY clients/csharp/Client/ clients/csharp/Client/
COPY examples/MovementSystem/ examples/MovementSystem/
RUN dotnet publish examples/MovementSystem/MovementSystem.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "MovementSystem.dll"]
