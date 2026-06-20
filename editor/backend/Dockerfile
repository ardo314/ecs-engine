FROM mcr.microsoft.com/dotnet/sdk:9.0 AS build
WORKDIR /src
COPY EditorBackend/EditorBackend.csproj EditorBackend/
RUN dotnet restore EditorBackend/EditorBackend.csproj
COPY EditorBackend/ EditorBackend/
RUN dotnet publish EditorBackend/EditorBackend.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:9.0
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:5000
EXPOSE 5000
ENTRYPOINT ["dotnet", "EditorBackend.dll"]
