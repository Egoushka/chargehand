# chargehand serve with the Claude Code worker runtime (ADR 0024). OpenCode is not in the image: a profile that uses it
# points opencode.url at a server the deployment runs next to this container.
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish src/Chargehand.Cli -c Release -o /out

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Must match claude_code.version in the profile: the adapter refuses to run on a mismatch (ADR 0020).
ARG CLAUDE_CODE_VERSION=2.1.283
RUN apt-get update \
    && apt-get install -y --no-install-recommends ca-certificates git nodejs npm \
    && npm install -g "@anthropic-ai/claude-code@${CLAUDE_CODE_VERSION}" \
    && npm cache clean --force \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /out bin/
COPY prompts prompts/
COPY presets presets/
# runs/: the run log (profile run_log, relative to /app). /srv/chargehand/work: worker clones (ADR 0023).
RUN mkdir -p runs /srv/chargehand/work && chown "$APP_UID" runs /srv/chargehand/work
USER $APP_UID
ENV CHARGEHAND_PROFILE=/config/profile.json DISABLE_AUTOUPDATER=1
EXPOSE 4300
ENTRYPOINT ["dotnet", "/app/bin/Chargehand.Cli.dll"]
CMD ["serve"]
