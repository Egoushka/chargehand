# The image a driven writing session runs in (ADR 0039): the .NET 10 SDK, Node, Python, git, Claude Code at the profile's pinned version, the
# chargehand plugin and the `chargehand session` driver. No Docker client, no cloud CLI, no credential. Build from the repository root:
#   docker build -f images/session/Dockerfile -t chargehand-session .
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Chargehand.Cli -c Release -o /out

FROM mcr.microsoft.com/dotnet/sdk:10.0
# Must match claude_code.version in the profile: the adapter refuses to run on a mismatch (ADR 0020).
ARG CLAUDE_CODE_VERSION=2.1.283
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git nodejs npm python3 python3-pip python3-venv \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/* \
    && useradd --uid 10001 --create-home --home-dir /home/session --shell /bin/bash session
COPY --from=build /out /opt/chargehand
COPY plugins/chargehand /opt/chargehand-plugin
# A new named volume mounted here takes this ownership on first use, so the non-root session can write it.
RUN mkdir -p /work /out && chown 10001:10001 /work /out
# The runner starts the container with a read-only root and a tmpfs at /home/session and /tmp: tool caches and Claude Code's own state go there.
ENV HOME=/home/session DISABLE_AUTOUPDATER=1 DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC=1
USER 10001
WORKDIR /work
ENTRYPOINT ["dotnet", "/opt/chargehand/Chargehand.Cli.dll", "session"]
