#!/bin/bash

if [ ! -f .env.local ]; then
    echo "Warning, .env.local file not found. Creating a new one. If you need to set local environment variables, please edit the .env.local file."
    touch .env.local
else
  echo ".env.local file already exists."
fi

# Create a data directory if it doesn't exist
mkdir -p ./data