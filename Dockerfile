FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY ObsidianBot.csproj ./
RUN dotnet restore ObsidianBot.csproj
COPY . ./
RUN dotnet publish ObsidianBot.csproj -c Release -o /app/publish

FROM node:22-bookworm-slim AS headless
ARG OBSIDIAN_HEADLESS_VERSION=0.0.14
RUN npm install --prefix /opt/obsidian-headless --omit=dev obsidian-headless@${OBSIDIAN_HEADLESS_VERSION}

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app
COPY --from=build /app/publish .
ENV DOTNET_EnableDiagnostics=0
ENTRYPOINT ["dotnet", "ObsidianBot.dll"]

FROM runtime AS sync
COPY --from=headless /usr/local/bin/node /usr/local/bin/node
COPY --from=headless /opt/obsidian-headless /opt/obsidian-headless
RUN ln -s /opt/obsidian-headless/node_modules/obsidian-headless/cli.js /usr/local/bin/ob

FROM runtime AS app
