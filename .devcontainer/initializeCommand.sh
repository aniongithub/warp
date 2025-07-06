#!/bin/bash

if [ ! -f .env ]; then
    echo "Warning, .env file not found. Creating a new one. If you need to set environment variables, please edit the .env file."
    touch .env
else
  echo ".env file already exists."
fi

# Create a data directory if it doesn't exist
mkdir -p ./data