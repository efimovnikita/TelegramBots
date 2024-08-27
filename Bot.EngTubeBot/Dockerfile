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

COPY ["Bot.EngTubeBot/Bot.EngTubeBot.csproj", "Bot.EngTubeBot/"]
RUN dotnet restore "Bot.EngTubeBot/Bot.EngTubeBot.csproj"
COPY . .
WORKDIR "/src/Bot.EngTubeBot"
RUN dotnet build "Bot.EngTubeBot.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bot.EngTubeBot.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.EngTubeBot.dll"]
