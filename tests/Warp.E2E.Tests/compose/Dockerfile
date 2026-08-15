# E2E runtime image.
#
# Rather than rebuilding the whole SDK layer via the repo Dockerfile (whose `target: runtime`
# stage also bakes a ./config that is not tracked in the repo), the E2E harness pre-publishes
# the warp / warp.plasma / warp.latinum entry assemblies into ./publish and this image simply
# layers them plus the dedicated e2e ./config on top of the ASP.NET runtime. Each process is
# started via a per-service command override (dotnet warp.dll | warp.plasma.dll | warp.latinum.dll).
#
# Build context is expected to contain:
#   publish/   -> output of `dotnet publish` for warp, warp.plasma, warp.latinum (one folder)
#   config/    -> tests/Warp.E2E.Tests/config
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim

WORKDIR /warp

COPY publish/ /warp/
COPY config/ /warp/config/

ENV WARP_CONFIG_BASE_DIR=/warp/config
ENV DOTNET_NOLOGO=true
ENV DOTNET_CLI_TELEMETRY_OPTOUT=true

# Default command runs the gateway; overridden for the plasma and latinum services.
CMD ["dotnet", "warp.dll"]
