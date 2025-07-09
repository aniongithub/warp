FROM ubuntu:20.04

ARG DEBIAN_FRONTEND=noninteractive

ENV PROJECT_NAME=firestore
ENV FIREBASE_EMULATOR_LOGS=logs
# Environment variables for HTTP/2 support
ENV JAVA_OPTS="-Dio.grpc.netty.useEpoll=false -Djava.net.useSystemProxies=false"
ENV GRPC_ENABLE_HTTP_PROXY=0

# Install JRE (required for Firestore emulator).
RUN apt-get update && \
    apt-get install -y \
        nano \
        git \
        curl \
        openjdk-21-jre-headless

# Install the Google Cloud SDK and Firestore emulator
RUN apt-get update &&\
    apt-get install -y apt-transport-https ca-certificates gnupg curl &&\
    curl https://packages.cloud.google.com/apt/doc/apt-key.gpg | gpg --dearmor -o /usr/share/keyrings/cloud.google.gpg &&\
    echo "deb [signed-by=/usr/share/keyrings/cloud.google.gpg] https://packages.cloud.google.com/apt cloud-sdk main" | tee -a /etc/apt/sources.list.d/google-cloud-sdk.list &&\
    apt-get update && apt-get install -y \
        google-cloud-cli \
        google-cloud-cli-firestore-emulator

# Install the firebase CLI and tools
RUN curl -sL https://firebase.tools | bash

WORKDIR /firestore

# Create firebase.json configuration file
RUN cat <<EOF > firebase.json
{
    "emulators": {
      "firestore": {
        "host": "0.0.0.0",
        "port": 8080
      },
      "ui": {
        "enabled": true,
        "host": "0.0.0.0",
        "port": 4000
      }
    }
  }
EOF

# Expose our API and web interface ports
EXPOSE 8080 4000

# Start Firebase emulator with HTTP/2 and gRPC optimizations
CMD ["firebase", "emulators:start", "--only", "firestore", "--project", "firestore"]
