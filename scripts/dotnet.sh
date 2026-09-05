#!/usr/bin/env sh
# Runs the .NET SDK inside its container when no `dotnet` is installed on the host, so a
# machine with Docker and nothing else can build and test this repository. The Docker
# socket is mounted because the test suite starts a real pgvector through Testcontainers.
set -e
cd "$(dirname "$0")/.."
if command -v dotnet >/dev/null 2>&1 && [ -z "${FORCE_CONTAINER_DOTNET:-}" ]; then
  exec dotnet "$@"
fi
exec docker run --rm -t \
  -v "$PWD":/src -w /src \
  -v ai-customer-service-dotnet-nuget:/root/.nuget/packages \
  -v /var/run/docker.sock:/var/run/docker.sock \
  -e TESTCONTAINERS_HOST_OVERRIDE=host.docker.internal \
  -e TESTCONTAINERS_RYUK_DISABLED=true \
  -e DOTNET_CLI_TELEMETRY_OPTOUT=1 -e DOTNET_NOLOGO=1 \
  ${DOTNET_CONTAINER_ARGS:-} \
  mcr.microsoft.com/dotnet/sdk:10.0 dotnet "$@"
