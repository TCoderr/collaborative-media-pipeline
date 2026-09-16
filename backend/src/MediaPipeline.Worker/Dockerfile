FROM mcr.microsoft.com/dotnet/sdk:9.0.318-noble AS build
RUN apt-get update && apt-get upgrade -y && rm -rf /var/lib/apt/lists/*
WORKDIR /src
COPY backend ./backend
RUN dotnet restore backend/src/MediaPipeline.Worker/MediaPipeline.Worker.csproj
RUN dotnet publish backend/src/MediaPipeline.Worker/MediaPipeline.Worker.csproj -c Release --no-restore -o /out /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0.20-noble-chiseled-extra
WORKDIR /app
COPY --from=build /out ./
USER 1654
ENV ASPNETCORE_URLS=http://+:8080 DOTNET_EnableDiagnostics=0 Storage__Provider=AzureBlob
EXPOSE 8081
HEALTHCHECK --interval=30s --timeout=4s --start-period=20s CMD ["dotnet", "MediaPipeline.Worker.dll", "--healthcheck"]
ENTRYPOINT ["dotnet", "MediaPipeline.Worker.dll"]
