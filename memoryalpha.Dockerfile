
FROM aniondocker/memoryalpha-rag-api:v0.5.5

# Install .NET 9.0 runtime dependencies
RUN apt-get update && apt-get install -y --no-install-recommends \
    ca-certificates curl gnupg \
 && rm -rf /var/lib/apt/lists/* \
 && install -d /usr/share/keyrings \
 && curl -fsSL https://packages.microsoft.com/keys/microsoft.asc \
    | gpg --dearmor -o /usr/share/keyrings/microsoft.gpg \
 && echo "deb [arch=$(dpkg --print-architecture) signed-by=/usr/share/keyrings/microsoft.gpg] \
    https://packages.microsoft.com/debian/12/prod bookworm main" \
    > /etc/apt/sources.list.d/microsoft-prod.list

# ASP.NET Core runtime 9.0
RUN apt-get update && apt-get install -y --no-install-recommends \
    aspnetcore-runtime-9.0 \
 && rm -rf /var/lib/apt/lists/*

WORKDIR /warp.plasma
COPY --from=warp-publisher:latest /workspace/warp/publish/* .

RUN apt-get update &&\
    apt-get install -y supervisor libicu-dev &&\
    apt-get clean &&\
    rm -rf /var/lib/apt/lists/*

# Copy supervisord configuration
COPY memoryalpha.supervisord.conf /etc/supervisord.conf
COPY memoryalpha-entrypoint.sh /usr/local/bin/entrypoint.sh