FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy local NuGet packages
COPY Bot.EngTubeBot/nuget/ /nuget

# Add local NuGet source
RUN dotnet nuget add source /nuget

COPY ["Bot.YTMusicFetcher/Bot.YTMusicFetcher.csproj", "Bot.YTMusicFetcher/"]
RUN dotnet restore "Bot.YTMusicFetcher/Bot.YTMusicFetcher.csproj"
COPY . .
WORKDIR "/src/Bot.YTMusicFetcher"
RUN dotnet build "Bot.YTMusicFetcher.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bot.YTMusicFetcher.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.YTMusicFetcher.dll"]
