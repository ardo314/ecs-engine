FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY clients/csharp/Client/Client.csproj clients/csharp/Client/
COPY editor/backend/EditorBackend/EditorBackend.csproj editor/backend/EditorBackend/
RUN dotnet restore editor/backend/EditorBackend/EditorBackend.csproj
COPY clients/csharp/Client/ clients/csharp/Client/
COPY editor/backend/EditorBackend/ editor/backend/EditorBackend/
RUN dotnet publish editor/backend/EditorBackend/EditorBackend.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080
ENTRYPOINT ["dotnet", "EditorBackend.dll"]
