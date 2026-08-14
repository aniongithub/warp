# Base image: Devcontainer image for .NET 9.0 with ASP.NET Core 10.0 (preview)
FROM mcr.microsoft.com/devcontainers/dotnet:9.0-bookworm as dev

# Install common dev tools
RUN apt-get update && apt-get install -y \
    curl \
    git \
    nano \
    jq \
    unzip \
    zip \
    netcat-traditional \
    iproute2 \
    dnsutils \
    procps \
    && apt-get clean

# Install ngrok (used by warp.latinum DEBUG webhook auto-registration).
# Baked into the image so it is present regardless of how the container is
# started (docker compose or the devcontainer CLI); the devcontainer "ngrok"
# Feature is only applied when built through the devcontainer CLI.
RUN curl -sSL https://ngrok-agent.s3.amazonaws.com/ngrok.asc \
      | gpg --dearmor -o /usr/share/keyrings/ngrok.gpg \
  && echo "deb [signed-by=/usr/share/keyrings/ngrok.gpg] https://ngrok-agent.s3.amazonaws.com buster main" \
      | tee /etc/apt/sources.list.d/ngrok.list \
  && apt-get update && apt-get install -y ngrok \
  && apt-get clean

# Install the .NET SDK for ASP.NET Core 10.0 (preview)
RUN curl -SL --output /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --version 10.0.100-preview.5.25265.106 --install-dir /usr/share/dotnet && \
    bash /tmp/dotnet-install.sh --runtime dotnet --version 9.0.5 --install-dir /usr/share/dotnet && \
    bash /tmp/dotnet-install.sh --runtime aspnetcore --version 9.0.5 --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh

# Install Google Cloud CLI
RUN apt-get update && apt-get install -y curl gnupg \
  && echo "deb [signed-by=/usr/share/keyrings/cloud.google.gpg] http://packages.cloud.google.com/apt cloud-sdk main" \
     | tee -a /etc/apt/sources.list.d/google-cloud-sdk.list \
  && curl https://packages.cloud.google.com/apt/doc/apt-key.gpg \
     | apt-key --keyring /usr/share/keyrings/cloud.google.gpg add - \
  && apt-get update && apt-get install -y google-cloud-cli \
  && apt-get clean

# Install Python 3 and pip
RUN apt-get update &&\
    apt-get install -y python3 python3-pip &&\
    apt-get clean &&\
    rm -rf /var/lib/apt/lists/*
ENV PIP_BREAK_SYSTEM_PACKAGES=1

# Install Python dependencies
COPY ./requirements.txt /tmp/pip-tmp/
RUN pip3 install --no-cache-dir -r /tmp/pip-tmp/requirements.txt &&\
    rm -rf /tmp/pip-tmp

USER vscode

# Builder image for the Warp API Gateway
# This stage builds the Warp API Gateway application using the .NET SDK
FROM dev as builder

USER root

# Set the working directory
WORKDIR /workspace/warp
# Copy the project files
COPY . /workspace/warp

# Restore the project dependencies
RUN dotnet nuget locals all --clear &&\
    dotnet restore &&\
    dotnet tool restore

# Build the project
RUN dotnet build /property:GenerateFullPaths=true /consoleloggerparameters:NoSummary

# Publish the project
RUN dotnet publish -c Release -o /workspace/warp/publish /property:Generate

# Final runtime image for the Warp API Gateway
# This stage runs the published application using the ASP.NET Core runtime
FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime

# Define the port as an argument with default value
ARG WARP_PORT=5000

# Install supervisor for process management
RUN apt-get update && apt-get install -y supervisor &&\
    apt-get clean &&\
    rm -rf /var/lib/apt/lists/*

# Create supervisor log directory
RUN mkdir -p /var/log/supervisor

# Set the working directory
WORKDIR /warp

# Copy the published outputs from the builder stage.
# Binaries, configuration, and OpenAPI specs
COPY --from=builder /workspace/warp/publish .
# Copy the warp-internal generated OpenAPI specs from the builder stage
COPY --from=builder /workspace/warp/warp.apis.*.yml .

# Copy our example configuration - this can be overridden by mounting a volume
COPY --from=builder /workspace/warp/config ./config

# Copy the external OpenAPI specs to the spec directory - this can be overridden by mounting a volume
COPY --from=builder /workspace/warp/spec ./spec

# Copy supervisord configuration and entrypoint script
COPY warp.supervisord.conf /etc/supervisord.conf
COPY warp-entrypoint.sh /usr/local/bin/entrypoint.sh
RUN chmod +x /usr/local/bin/entrypoint.sh

# Only expose the API Gateway port (internal APIs are accessed through the gateway)
EXPOSE ${WARP_PORT}

# Set environment variable defaults
ENV RUN_WARP=true
ENV RUN_PLASMA=false

# Set the entry point for the container to use our custom entrypoint
ENTRYPOINT ["/usr/local/bin/entrypoint.sh"]