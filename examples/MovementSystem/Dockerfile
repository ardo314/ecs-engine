FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY engine/Core/Core.csproj engine/Core/
COPY engine/Client/Client.csproj engine/Client/
COPY examples/MovementSystem/MovementSystem.csproj examples/MovementSystem/
RUN dotnet restore examples/MovementSystem/MovementSystem.csproj
COPY engine/Core/ engine/Core/
COPY engine/Client/ engine/Client/
COPY examples/MovementSystem/ examples/MovementSystem/
RUN dotnet publish examples/MovementSystem/MovementSystem.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:9.0
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "MovementSystem.dll"]
