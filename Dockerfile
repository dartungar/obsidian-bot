FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ObsidianBot.csproj ./
RUN dotnet restore ObsidianBot.csproj
COPY . ./
RUN dotnet publish ObsidianBot.csproj -c Release -o /app/publish

FROM mcr.microsoft.com/dotnet/runtime:10.0
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "ObsidianBot.dll"]
