# VU Studio agent image. Build from the repository root:
#   docker build -f deploy/agent/Dockerfile -t vustudio-agent .
FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/VuStudio.Core/VuStudio.Core.csproj src/VuStudio.Core/
COPY src/VuStudio.Sources/VuStudio.Sources.csproj src/VuStudio.Sources/
COPY src/VuStudio.Agent/VuStudio.Agent.csproj src/VuStudio.Agent/
RUN dotnet restore src/VuStudio.Agent/VuStudio.Agent.csproj
COPY src/VuStudio.Core src/VuStudio.Core
COPY src/VuStudio.Sources src/VuStudio.Sources
COPY src/VuStudio.Agent src/VuStudio.Agent
RUN dotnet publish src/VuStudio.Agent/VuStudio.Agent.csproj -c Release --no-restore -o /app /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0-alpine
WORKDIR /app
COPY --from=build /app .
# The container's own /proc and /sys already show the host's CPU, memory, load, disk I/O, uptime and
# hwmon sensors; do not bind-mount /proc (runc refuses it). Network rates and mDNS need --network host,
# the process count needs --pid host, free space needs the host's mounts (e.g. -v /mnt:/mnt:ro).
ENV VUSTUDIO_AGENT_PORT=5342
# A token generated on first start (when none is configured) is kept in /data/token.
ENV VUSTUDIO_AGENT_DATA=/data
RUN mkdir -p /data && chown app:app /data
VOLUME /data
EXPOSE 5342
USER app
ENTRYPOINT ["dotnet", "vustudio-agent.dll"]
