#!/bin/bash

if [ ! -f .env.local ]; then
    echo "Warning, .env.local file not found. Creating a new one. If you need to set local environment variables, please edit the .env.local file."
    touch .env.local
else
  echo ".env.local file already exists."
fi

# Write a unique identifier to .env.local, mark as do not change this value UNIQUE_WEBHOOK_SUBDOMAIN
if ! grep -q '^UNIQUE_WEBHOOK_SUBDOMAIN=' .env.local; then
    UNIQUE_ID=$(head /dev/urandom | tr -dc A-Za-z0-9 | head -c 13 ; echo '')
    echo "" >> .env.local
    echo "UNIQUE_WEBHOOK_SUBDOMAIN=$UNIQUE_ID" >> .env.local
    echo "Added UNIQUE_WEBHOOK_SUBDOMAIN to .env.local"
else
    echo "UNIQUE_WEBHOOK_SUBDOMAIN already set in .env.local"
fi

# Create a data directory if it doesn't exist
mkdir -p ./data