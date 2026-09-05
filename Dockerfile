# A container for a service with a native inference runtime and a 470 MB model.
#
# The model is baked into the image rather than downloaded at startup or mounted. A cold
# start then reaches ready in a few seconds and needs no network, which is what a readiness
# probe wants; the cost is an image whose size is dominated by one file. The honest number
# is in the last stage's comment.

# ---- 1. the embedding model -------------------------------------------------------
# Its own stage so a code change does not re-download 470 MB, and so the layer is cached
# independently of the build.
FROM debian:bookworm-slim AS model
ARG MODEL_REPO=https://huggingface.co/intfloat/multilingual-e5-small/resolve/main
RUN apt-get update && apt-get install -y --no-install-recommends ca-certificates curl \
    && rm -rf /var/lib/apt/lists/*
WORKDIR /model
# The fp32 export, not a quantised one: the int8 builds are per-architecture and would
# make this image unportable.
RUN curl -fsSL -o model.onnx "${MODEL_REPO}/onnx/model.onnx" \
 && curl -fsSL -o tokenizer.json "${MODEL_REPO}/tokenizer.json"

# ---- 2. build ---------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
ARG TARGETARCH
WORKDIR /src
COPY src/CustomerService/CustomerService.csproj src/CustomerService/
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet restore src/CustomerService/CustomerService.csproj -r linux-$(echo "$TARGETARCH" | sed 's/amd64/x64/')
COPY src/CustomerService/ src/CustomerService/
# A runtime-specific publish so the image carries one ONNX Runtime native library rather
# than every platform's. Not self-contained: the aspnet base image supplies the runtime.
RUN --mount=type=cache,target=/root/.nuget/packages \
    dotnet publish src/CustomerService/CustomerService.csproj -c Release --no-restore \
      -r linux-$(echo "$TARGETARCH" | sed 's/amd64/x64/') --self-contained false -o /out

# ---- 3. runtime -------------------------------------------------------------------
FROM mcr.microsoft.com/dotnet/aspnet:10.0
RUN useradd --system --uid 10001 --create-home app
WORKDIR /app
COPY --from=build /out/ /app/
COPY --from=model /model/model.onnx     /app/model-cache/multilingual-e5-small/model.onnx
COPY --from=model /model/tokenizer.json /app/model-cache/multilingual-e5-small/tokenizer.json
COPY corpus/faq.json /app/corpus/faq.json

ENV EMBEDDING_MODEL_PATH=/app/model-cache/multilingual-e5-small/model.onnx \
    EMBEDDING_TOKENIZER_PATH=/app/model-cache/multilingual-e5-small/tokenizer.json \
    FAQ_CORPUS_PATH=/app/corpus/faq.json \
    HTTP_ADDR=:8082 \
    DOTNET_gcServer=0

USER 10001
EXPOSE 8082

# Measured after the first build; see docs/footprint.md for where it goes.
HEALTHCHECK --interval=15s --timeout=3s --start-period=30s --retries=3 \
  CMD ["dotnet", "/app/CustomerService.dll", "--healthcheck"]

ENTRYPOINT ["dotnet", "/app/CustomerService.dll"]
