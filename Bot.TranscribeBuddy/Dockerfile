FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy local NuGet packages
COPY Bot.TranscribeBuddy/nuget/ /nuget

# Add local NuGet source
RUN dotnet nuget add source /nuget

COPY ["Bot.TranscribeBuddy/Bot.TranscribeBuddy.csproj", "Bot.TranscribeBuddy/"]
COPY ["BotSharedLibrary/BotSharedLibrary.csproj", "BotSharedLibrary/"]
RUN dotnet restore "Bot.TranscribeBuddy/Bot.TranscribeBuddy.csproj"
COPY . .
WORKDIR "/src/Bot.TranscribeBuddy"
RUN dotnet build "Bot.TranscribeBuddy.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bot.TranscribeBuddy.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.TranscribeBuddy.dll"]
