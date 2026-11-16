FROM mcr.microsoft.com/dotnet/aspnet:11.0 AS base
USER $APP_UID
WORKDIR /app
EXPOSE 8080
EXPOSE 8081

# 1. Build the Phaser 3 game (outputs to /src/wwwroot)
FROM node:lts-alpine AS game-build
WORKDIR /src/game
COPY ["game/package*.json", "./"]
RUN npm install
COPY ["game/", "./"]
RUN npm run build

# 2. Build the ASP.NET Core app
FROM mcr.microsoft.com/dotnet/sdk:11.0 AS build
ARG BUILD_CONFIGURATION=Release
WORKDIR /src
COPY ["TuringTown.csproj", "./"]
RUN dotnet restore "TuringTown.csproj"
COPY . .
COPY --from=game-build /src/wwwroot ./wwwroot
WORKDIR "/src/"
RUN dotnet build "./TuringTown.csproj" -c $BUILD_CONFIGURATION -o /app/build

FROM build AS publish
ARG BUILD_CONFIGURATION=Release
RUN dotnet publish "./TuringTown.csproj" -c $BUILD_CONFIGURATION -o /app/publish /p:UseAppHost=false

FROM base AS final
WORKDIR /app
COPY --from=publish /app/publish .
ENTRYPOINT ["dotnet", "TuringTown.dll"]
