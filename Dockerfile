# syntax=docker/dockerfile:1

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish DailyPosterGenerator.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS final
WORKDIR /app
COPY --from=build /app/publish .

ENV DOTNET_gcServer=0
ENV DOTNET_GCHeapHardLimit=0x1C000000
ENV DOTNET_GCHeapCount=1
ENV DOTNET_EnableDiagnostics=0

ENV ASPNETCORE_URLS=http://+:
ENV ASPNETCORE_ENVIRONMENT=Production
ENV UseSqlite=true
ENTRYPOINT ["dotnet", "DailyPosterGenerator.dll"]