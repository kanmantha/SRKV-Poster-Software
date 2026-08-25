FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY DailyPosterGenerator.csproj .
RUN dotnet restore DailyPosterGenerator.csproj
COPY . .
RUN dotnet publish DailyPosterGenerator.csproj -c Release -o /app/publish --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "DailyPosterGenerator.dll"]
