FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy local NuGet packages
COPY Bot.ListenYoutube/nuget/ /nuget

# Add local NuGet source
RUN dotnet nuget add source /nuget

COPY ["Bot.ListenYoutube/Bot.ListenYoutube.csproj", "Bot.ListenYoutube/"]
COPY ["BotSharedLibrary/BotSharedLibrary.csproj", "BotSharedLibrary/"]
RUN dotnet restore "Bot.ListenYoutube/Bot.ListenYoutube.csproj"
COPY . .
WORKDIR "/src/Bot.ListenYoutube"
RUN dotnet build "Bot.ListenYoutube.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bot.ListenYoutube.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.ListenYoutube.dll"]
