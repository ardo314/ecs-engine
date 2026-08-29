FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY clients/csharp/Client/ clients/csharp/Client/
COPY examples/Nova.Components/ examples/Nova.Components/
COPY examples/Nova.Systems/ examples/Nova.Systems/
RUN dotnet publish examples/Nova.Systems/Nova.Systems.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
EXPOSE 8080
ENTRYPOINT ["dotnet", "Nova.Systems.dll"]
