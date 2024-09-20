FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS base
USER $APP_UID
WORKDIR /app

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src

# Copy local NuGet packages
COPY Bot.ItalianInjector/nuget/ /nuget

# Add local NuGet source
RUN dotnet nuget add source /nuget

COPY ["Bot.ItalianInjector/Bot.ItalianInjector.csproj", "Bot.ItalianInjector/"]
COPY ["BotSharedLibrary/BotSharedLibrary.csproj", "BotSharedLibrary/"]
RUN dotnet restore "Bot.ItalianInjector/Bot.ItalianInjector.csproj"
COPY . .
WORKDIR "/src/Bot.ItalianInjector"
RUN dotnet build "Bot.ItalianInjector.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "Bot.ItalianInjector.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "Bot.ItalianInjector.dll"]
