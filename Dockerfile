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

# Install the .NET SDK for ASP.NET Core 10.0 (preview)
RUN curl -SL --output /tmp/dotnet-install.sh https://dot.net/v1/dotnet-install.sh && \
    bash /tmp/dotnet-install.sh --version 10.0.100-preview.5.25265.106 --install-dir /usr/share/dotnet && \
    bash /tmp/dotnet-install.sh --runtime dotnet --version 9.0.5 --install-dir /usr/share/dotnet && \
    bash /tmp/dotnet-install.sh --runtime aspnetcore --version 9.0.5 --install-dir /usr/share/dotnet && \
    rm /tmp/dotnet-install.sh

# Install and build our fork of yarp for post-transform fixes
WORKDIR /usr/local/packages
RUN cd /usr/local/src &&\
    git clone https://github.com/aniongithub/yarp.git &&\
        cd yarp &&\
        git checkout backport-post-transform-hook &&\
        dotnet pack --configuration Release --output /usr/local/packages

USER vscode

# Install NVM, Node.js (latest LTS), and npm
ENV NVM_DIR=/home/vscode/.nvm
RUN curl -o- https://raw.githubusercontent.com/nvm-sh/nvm/v0.39.7/install.sh | bash \
    && . "$NVM_DIR/nvm.sh" \
    && nvm install --lts \
    && nvm use --lts \
    && nvm alias default 'lts/*' \
    && npm install -g npm \
    && sudo ln -s $NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/node /usr/local/bin/node \
    && sudo ln -s $NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/npm /usr/local/bin/npm \
    && sudo ln -s $NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/npx /usr/local/bin/npx
ENV PATH="$NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/:$PATH"

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

# Set the entry point for the container
CMD ["dotnet", "warp.dll"]