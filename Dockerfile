FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DailyPosterGenerator.csproj .
RUN dotnet restore DailyPosterGenerator.csproj
COPY . .
RUN dotnet publish DailyPosterGenerator.csproj -c Release -r linux-x64 --self-contained false -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
RUN apt-get update && apt-get install -y --no-install-recommends fontconfig libfontconfig1 libharfbuzz0b libfreetype6 libjpeg-turbo8 libpng16-16 libtiff6 libwebp7 libgif7 libbz2-1.0 libexpat1 libzstd1 fonts-dejavu-core fonts-liberation fonts-liberation2 && rm -rf /var/lib/apt/lists/* && fc-cache -f
WORKDIR /app
COPY --from=build /app/publish .
RUN mkdir -p /app/data /app/wwwroot/posters /app/wwwroot/templates /app/wwwroot/logos

ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapHardLimit=0x1C000000
ENV DOTNET_GCHeapCount=1
ENV DOTNET_EnableDiagnostics=0
ENV DOTNET_hostBuilder__reloadConfigOnChange=false

ENV ASPNETCORE_URLS=http://+:${PORT:-8080}
ENV ASPNETCORE_ENVIRONMENT=Production
ENV UseSqlite=true
ENTRYPOINT ["dotnet", "DailyPosterGenerator.dll"]