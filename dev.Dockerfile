# Base image: .NET SDK for building and running ASP.NET Core
FROM mcr.microsoft.com/devcontainers/dotnet:8.0-bookworm

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
    rm /tmp/dotnet-install.sh

# Install and build our fork of yarp for post-transform fixes
RUN cd /usr/local/src &&\
    git clone https://github.com/aniongithub/yarp.git &&\
        cd yarp &&\
        git checkout backport-post-transform-hook &&\
        dotnet pack --configuration Release --output ./packages

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
    && sudo ln -s $NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/npm /usr/local/bin/npm
ENV PATH="$NVM_DIR/versions/node/$(ls $NVM_DIR/versions/node/)/bin/:$PATH"