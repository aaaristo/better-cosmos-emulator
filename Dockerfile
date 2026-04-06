FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Cosmos.Emulator.Api/Cosmos.Emulator.Api.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=build /app .

VOLUME /data
EXPOSE 8081

ENTRYPOINT ["dotnet", "Cosmos.Emulator.Api.dll", "--data", "/data"]
