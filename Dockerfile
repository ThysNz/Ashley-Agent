# One image for every agent in this folder. Agent-specific things (config,
# workspace, memory) are mounted in by each agent's docker-compose.yml.
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY src/AgentCore/AgentCore.csproj src/AgentCore/
RUN dotnet restore src/AgentCore/AgentCore.csproj
COPY src/AgentCore/ src/AgentCore/
RUN dotnet publish src/AgentCore/AgentCore.csproj -c Release -o /out --no-restore

# Runtime = the full .NET SDK image, so the agent can also build/run C# itself,
# plus the usual developer tools (git, python, node, build tools, ripgrep...).
FROM mcr.microsoft.com/dotnet/sdk:8.0
RUN apt-get update \
 && apt-get install -y --no-install-recommends \
      git curl wget jq ripgrep unzip zip tree nano less procps ca-certificates sudo \
      python3 python3-pip python3-venv nodejs npm build-essential \
 && rm -rf /var/lib/apt/lists/*

RUN useradd -m -u 1000 -s /bin/bash agent \
 && echo "agent ALL=(ALL) NOPASSWD:ALL" > /etc/sudoers.d/agent \
 && mkdir -p /agent/config /agent/workspace /agent/memory \
 && chown -R agent:agent /agent

COPY --from=build /out /opt/agent

ENV AGENT_CONFIG=/agent/config \
    AGENT_WORKSPACE=/agent/workspace \
    AGENT_MEMORY=/agent/memory \
    PIP_BREAK_SYSTEM_PACKAGES=1 \
    DOTNET_NOLOGO=1 \
    DOTNET_CLI_TELEMETRY_OPTOUT=1 \
    TERM=xterm-256color

USER agent
WORKDIR /agent/workspace
ENTRYPOINT ["dotnet", "/opt/agent/AgentCore.dll"]
