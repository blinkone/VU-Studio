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
# Inside a container /proc and /sys already show the host's CPU, memory, disks and sensors.
# Network rates need --network host (otherwise only the container's own interface is visible).
ENV VUSTUDIO_AGENT_PORT=5342
EXPOSE 5342
USER app
ENTRYPOINT ["dotnet", "vustudio-agent.dll"]
