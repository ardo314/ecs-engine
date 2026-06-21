FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY engine/Core/Core.csproj engine/Core/
COPY engine/Client/Client.csproj engine/Client/
COPY editor/backend/EditorBackend/EditorBackend.csproj editor/backend/EditorBackend/
RUN dotnet restore editor/backend/EditorBackend/EditorBackend.csproj
COPY engine/Core/ engine/Core/
COPY engine/Client/ engine/Client/
COPY editor/backend/EditorBackend/ editor/backend/EditorBackend/
RUN dotnet publish editor/backend/EditorBackend/EditorBackend.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "EditorBackend.dll"]
